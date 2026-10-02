using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Tools;
using NSubstitute;
using PptxDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;

namespace Neuroset.Tests;

public sealed partial class PresentationTests
{
    private const string PhotoFeedback = "Да, это не то, что я просил, сделай побольше картинок и поменьше текста, причем картинки не сгенерированные, а взятые с интернет источников.";

    [Fact]
    public void Less_Text_And_More_Web_Images_Authorize_Body_And_Image_Fields_But_Keep_Notes_And_Style_Locked()
    {
        PresentationTool.ValidateScope([new("slide-002", "body", PhotoFeedback), new("slide-002", "images", PhotoFeedback), new("slide-002", "layout", PhotoFeedback)], PhotoFeedback, Sample());
        foreach (var field in new[] { "notes", "title", "animation", "titleColor", "font", "imagePrompt" })
        {
            Action expand = () => PresentationTool.ValidateScope([new("slide-002", field, PhotoFeedback)], PhotoFeedback, Sample());
            expand.Should().Throw<InvalidDataException>(field + " was not requested");
        }
    }

    [Fact]
    public async Task Corrects_Invalid_Scope_And_Patch_Automatically_Then_Edits_The_Existing_Deck_With_Real_Assets()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var before = await h.ReadDocument(original);
        h.Responses.Enqueue(JsonSerializer.Serialize(new { clarification = "", targets = new[] { new { slideId = "slide-002", field = "notes", quote = PhotoFeedback } } }));
        QueuePhotoEdit(h, before, invalidPatchFirst: true);
        var edited = h.Artifact(await h.Execute("edit", PhotoFeedback));
        var after = await h.ReadDocument(edited);
        edited.PresentationId.Should().Be(original.PresentationId);
        edited.Revision.Should().Be(2);
        edited.ImageCount.Should().Be(4);
        after.Slides.Select(slide => slide.Id).Should().Equal(before.Slides.Select(slide => slide.Id));
        after.Slides.Select(slide => slide.Title).Should().Equal(before.Slides.Select(slide => slide.Title));
        after.Slides.Select(slide => slide.Notes).Should().Equal(before.Slides.Select(slide => slide.Notes));
        after.Slides.Select(slide => slide.Animation).Should().Equal(before.Slides.Select(slide => slide.Animation));
        after.Theme.Should().Be(before.Theme);
        after.Slides.Sum(slide => slide.Body.Sum(text => text.Length)).Should().BeLessThan(before.Slides.Sum(slide => slide.Body.Sum(text => text.Length)));
        after.Slides.SelectMany(PresentationMedia.Images).Should().OnlyContain(image => image.Source == "web" && image.File != null && image.SourcePage != null);
        h.ImageRequests.Should().ContainSingle("editing web images must not call the image-generation API");
        h.WebImageRequests.Should().HaveCount(4);
        h.Requests.Should().HaveCount(6);
        h.Requests[3].Messages[0].Content.Should().Contain("previous response failed validation");
        h.Requests[5].Messages[0].Content.Should().Contain("frozen scope");
        (await new PresentationEditorService(new(h.Directory)).OpenAsync(edited.Path)).Snapshot.Slides.Sum(slide => slide.Elements.Count(element => element.Kind == "image")).Should().Be(4);
        File.Exists(original.Path).Should().BeTrue();
    }

    [Theory]
    [InlineData("pptx")]
    [InlineData("odp")]
    public async Task Web_Photo_Deck_Embeds_All_Images_With_Sources_And_Animations_Without_An_Image_Model(string format)
    {
        using var h = new Harness { Models = ["chat"] };
        QueueWebCreation(h);
        var artifact = h.Artifact(await h.Execute("create", $"Создай 2 слайда о Швейцарии: флаг, герб, карта и Берн. Картинки только из интернета. Формат {format}."));
        artifact.ImageCount.Should().Be(4);
        h.ImageRequests.Should().BeEmpty();
        h.WebImageRequests.Should().HaveCount(4);
        using var zip = ZipFile.OpenRead(artifact.Path);
        if (format == "pptx")
        {
            using var pptx = PptxDocument.Open(artifact.Path, false);
            new OpenXmlValidator().Validate(pptx).Should().BeEmpty();
            foreach (var number in new[] { 1, 2 })
            {
                var slide = Xml(zip, $"ppt/slides/slide{number}.xml");
                slide.Descendants(P + "pic").Should().HaveCount(2);
                slide.Descendants(A + "srcRect").Attributes().Should().BeEmpty("maps and flags must not be cropped");
                slide.Descendants(P + "animEffect").Should().HaveCount(4);
                var relations = Xml(zip, $"ppt/slides/_rels/slide{number}.xml.rels");
                relations.Root!.Elements().Where(element => element.Attribute("Type")!.Value.EndsWith("/image", StringComparison.Ordinal)).Select(element => element.Attribute("Id")!.Value).Should().OnlyHaveUniqueItems();
                var notes = Xml(zip, $"ppt/notesSlides/notesSlide{number}.xml").ToString();
                notes.Should().Contain("https://sources.example/").And.Contain("CC BY 4.0").And.NotContain("AI-generated");
            }
            zip.Entries.Count(entry => entry.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)).Should().Be(4);
        }
        else
        {
            var content = Xml(zip, "content.xml");
            content.Descendants(Draw + "image").Should().HaveCount(4);
            content.Descendants(Anim + "transitionFilter").Should().HaveCount(8);
            content.ToString().Should().Contain("https://sources.example/").And.NotContain("AI-generated");
            zip.Entries.Count(entry => entry.FullName.StartsWith("Pictures/", StringComparison.Ordinal)).Should().Be(4);
        }
        var session = await new PresentationEditorService(new(h.Directory)).OpenAsync(artifact.Path);
        session.Snapshot.Slides.Should().OnlyContain(slide => slide.HasImage);
        session.Snapshot.Slides.SelectMany(slide => slide.Elements).Where(element => element.Kind == "image").Should().OnlyContain(element => element.ImageFit == "contain" && File.Exists(element.ImagePath));
    }

    [Fact]
    public async Task Replacing_One_Image_With_A_Web_Photo_Preserves_Other_Generated_Images_And_All_Unrequested_Content()
    {
        using var h = new Harness();
        h.Responses.Enqueue(JsonSerializer.Serialize(new { title = "Лес", theme = "botanical", slides = new[]
        {
            new { title = "Лес рядом", layout = "photo_grid", animation = "fade", imagePrompt = "", images = new[] { new PresentationImage("generated", "Pine forest"), new("generated", "Forest cottage") } },
            new { title = "Шаги", layout = "editorial", animation = "reveal", imagePrompt = "", images = Array.Empty<PresentationImage>() }
        } }));
        h.Responses.Enqueue("""{"slides":[{"id":"slide-001","body":["Лес и город"],"notes":"Описание леса"},{"id":"slide-002","body":["Беречь деревья"],"notes":"Заметки"}]}""");
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var before = await h.ReadDocument(original);
        const string request = "На слайде 1 замени только вторую картинку на фото Берна из интернета";
        var originalImage = before.Slides[0].Images![1];
        h.Responses.Enqueue(JsonSerializer.Serialize(new { clarification = "", targets = new[] { new PresentationTool.EditTarget("slide-001", "images.1", request) } }));
        h.Responses.Enqueue(JsonSerializer.Serialize(new { patches = new[] { new PresentationTool.FieldPatch("slide-001", "images.1",
            JsonSerializer.Serialize(new { originalImage.Source, originalImage.Query, originalImage.Url, originalImage.Fit }, DomainJson.Options),
            JsonSerializer.Serialize(new PresentationImage("web", "Bern old town"), DomainJson.Options)) } }, DomainJson.Options));
        var edited = h.Artifact(await h.Execute("edit", request));
        var after = await h.ReadDocument(edited);
        edited.PresentationId.Should().Be(original.PresentationId);
        edited.Revision.Should().Be(2);
        after.Slides[0].Images![0].Should().Be(before.Slides[0].Images![0]);
        after.Slides[0].Images![1].Source.Should().Be("web");
        after.Slides[0].Images![1].File.Should().NotBe(before.Slides[0].Images![1].File);
        (after.Slides[0] with { Images = before.Slides[0].Images }).Should().BeEquivalentTo(before.Slides[0]);
        after.Slides[1].Should().BeEquivalentTo(before.Slides[1]);
        after.Theme.Should().Be(before.Theme);
        h.WebImageRequests.Should().ContainSingle().Which.Query.Should().Be("Bern old town");
        h.ImageRequests.Should().HaveCount(2, "unselected illustrations must not be regenerated");
        h.Requests.Should().HaveCount(4, "a valid local replacement needs no scope expansion or repair");
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("create")]
    public async Task Failed_Web_Image_Resumes_In_The_Same_Turn_Without_Redownloading_Completed_Images_Or_Rewriting_Slides(string retryOperation)
    {
        using var h = new Harness { FailWebImageAttempt = 2, Models = ["chat"] };
        QueueWebCreation(h);
        var turn = h.Turn("Создай новую презентацию на 2 слайда про Швейцарию. Изображения из интернета.");
        var failed = await h.Execute("create", turn);
        failed.Success.Should().BeFalse();
        using var report = JsonDocument.Parse(failed.Output);
        report.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
        report.RootElement.GetProperty("nextOperation").GetString().Should().Be("resume");
        report.RootElement.GetProperty("completedImages").GetInt32().Should().Be(3, "one failed image must not prevent the later assets from completing");
        var missing = report.RootElement.GetProperty("pendingImages").EnumerateArray().Should().ContainSingle().Which;
        missing.GetProperty("slideNumber").GetInt32().Should().Be(1);
        missing.GetProperty("imageNumber").GetInt32().Should().Be(2);
        h.RecreateTool();
        var resumed = h.Artifact(await h.Execute(retryOperation, turn));
        resumed.Revision.Should().Be(1);
        resumed.ImageCount.Should().Be(4);
        h.WebImageRequests.Should().HaveCount(5);
        h.WebImageRequests.Count(request => request.Query == "Switzerland flag").Should().Be(1);
        h.Requests.Should().HaveCount(2);
        h.ImageRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_Can_Repair_A_Missing_Image_URL_While_Preserving_The_Frozen_Draft_And_Completed_Assets()
    {
        using var h = new Harness { FailWebImageAttempt = 2, Models = ["chat"] };
        QueueWebCreation(h);
        var turn = h.Turn("Создай 2 слайда про Швейцарию с картинками из интернета");
        var failed = await h.Execute("create", turn);
        using var report = JsonDocument.Parse(failed.Output);
        var id = report.RootElement.GetProperty("presentationId").GetGuid();
        var pendingPath = Path.Combine(h.Directory, h.ConversationId.ToString("N"), "presentations", id.ToString("N"), "pending.json");
        var before = JsonSerializer.Deserialize<PresentationPending>(await File.ReadAllTextAsync(pendingPath), DomainJson.Options)!.Document;
        var arguments = JsonSerializer.SerializeToElement(new { operation = "resume", presentation_id = id,
            image_sources = new[] { new { slide_number = 1, image_number = 2, url = "https://commons.wikimedia.org/wiki/File:Coat_of_arms_of_Switzerland.svg" } } });
        var executor = new ToolExecutor(new ToolRegistry([h.Tool]), new FixedPermissionService(PermissionDecision.AlwaysAllow), NullLogger<ToolExecutor>.Instance);
        var artifact = h.Artifact(await executor.ExecuteAsync("presentation", arguments, h.Context(turn)));
        var after = await h.ReadDocument(artifact);
        for (var slide = 0; slide < before.Slides.Length; slide++)
            (after.Slides[slide] with { Images = before.Slides[slide].Images }).Should().BeEquivalentTo(before.Slides[slide]);
        after.Slides[0].Images![0].Should().Be(before.Slides[0].Images![0]);
        after.Slides[1].Images.Should().BeEquivalentTo(before.Slides[1].Images);
        after.Slides[0].Images![1].Query.Should().Be(before.Slides[0].Images![1].Query);
        h.WebImageRequests.Last().Url.Should().Be("https://commons.wikimedia.org/wiki/File:Coat_of_arms_of_Switzerland.svg");
        h.WebImageRequests.Should().HaveCount(5);
        h.ImageRequests.Should().BeEmpty();
        h.Requests.Should().HaveCount(2);
        artifact.PresentationId.Should().Be(id);
        artifact.Revision.Should().Be(1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    public async Task Resume_Image_Sources_Cannot_Change_Completed_Images_Or_Add_New_Ones(int slide, int image)
    {
        using var h = new Harness { FailWebImageAttempt = 2, Models = ["chat"] };
        QueueWebCreation(h);
        var turn = h.Turn("Создай 2 слайда про Швейцарию с картинками из интернета");
        using var report = JsonDocument.Parse((await h.Execute("create", turn)).Output);
        var id = report.RootElement.GetProperty("presentationId").GetGuid();
        var pendingPath = Path.Combine(h.Directory, h.ConversationId.ToString("N"), "presentations", id.ToString("N"), "pending.json");
        var before = await File.ReadAllTextAsync(pendingPath);
        var result = await h.Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation = "resume", presentation_id = id,
            image_sources = new[] { new { slide_number = slide, image_number = image, url = "https://images.example/unrequested.png" } } }), h.Context(turn));
        result.Success.Should().BeFalse();
        (await File.ReadAllTextAsync(pendingPath)).Should().Be(before);
        h.WebImageRequests.Should().HaveCount(4);
        h.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Repeated_Invalid_Edits_Are_Bounded_And_Never_Change_The_Committed_Deck()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var before = await h.ReadDocument(original);
        var turn = h.Turn("Измени только заголовок слайда 2");
        for (var attempt = 0; attempt < 6; attempt++)
            h.Responses.Enqueue(JsonSerializer.Serialize(new { clarification = "", targets = new[] { new { slideId = "slide-001", field = "notes", quote = turn.UserRequest } } }));
        for (var attempt = 0; attempt < 4; attempt++) (await h.Execute("edit", turn)).Success.Should().BeFalse();
        h.Requests.Should().HaveCount(8);
        h.ImageRequests.Should().ContainSingle();
        (await h.ReadDocument(original)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Runtime_Allows_The_Third_Recovery_Attempt_For_The_Same_Edit_Without_A_New_User_Message()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var before = await h.ReadDocument(original);
        const string request = "Измени только заголовок слайда 2";
        for (var attempt = 0; attempt < 4; attempt++)
            h.Responses.Enqueue(JsonSerializer.Serialize(new { clarification = "", targets = new[] { new PresentationTool.EditTarget("slide-002", "notes", request) } }));
        h.QueueTitleEdit();
        var streamCalls = 0;
        var editArguments = JsonSerializer.Serialize(new { operation = "edit", presentation_id = original.PresentationId, expected_revision = 1, slide_numbers = new[] { 2 } });
        h.Provider.StreamAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(call => Stream((LlmRequest)call[0]));
        var repository = new InMemoryConversationRepository();
        var registry = new ToolRegistry([h.Tool]);
        var runtime = new AgentRuntime(h.Factory, registry, new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AlwaysAllow), NullLogger<ToolExecutor>.Instance), repository,
            new InMemoryMemoryStore(), new InMemorySettingsRepository(), NullLogger<AgentRuntime>.Instance);
        var user = new ChatMessage { ConversationId = h.ConversationId, Role = MessageRole.User, Content = request };
        var events = new List<ModelEvent>();
        await foreach (var item in runtime.RunAsync(new(new Conversation { Id = h.ConversationId }, [user], h.Connection, new AgentRunOptions(4, Reasoning: ReasoningPreference.High)))) events.Add(item);
        events.OfType<ToolExecutionCompletedEvent>().Select(item => item.Succeeded).Should().Equal(false, false, true);
        var after = await h.ReadDocument(original);
        after.Revision.Should().Be(2);
        after.Slides[1].Title.Should().Be("Берегите лес");
        (after.Slides[1] with { Title = before.Slides[1].Title }).Should().BeEquivalentTo(before.Slides[1]);
        after.Slides[0].Should().BeEquivalentTo(before.Slides[0]);
        h.Requests.Should().HaveCount(8);
        h.ImageRequests.Should().ContainSingle();

        async IAsyncEnumerable<ModelEvent> Stream(LlmRequest modelRequest)
        {
            streamCalls++;
            if (streamCalls > 1) modelRequest.Messages.Should().Contain(message => message.Role == MessageRole.System && message.Content.Contains("recoverable failure", StringComparison.Ordinal));
            if (streamCalls <= 3)
            {
                yield return new ToolCallStartedEvent("edit-" + streamCalls, PresentationPolicy.ToolName);
                yield return new ToolCallArgumentsEvent("edit-" + streamCalls, editArguments);
            }
            else yield return new TextDeltaEvent("Готово.");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Research_From_The_Runtime_Reaches_The_Inner_Presentation_Writer()
    {
        using var h = new Harness();
        h.QueueCreation();
        var turn = h.Turn("Создай 2 слайда про лес");
        turn.AddResearch("web_search", "Verified forest source https://example.org/forest");
        turn.AddResearch("fetch_url", "A measured fact from the source.");
        h.Artifact(await h.Execute("create", turn));
        h.Requests.Should().OnlyContain(request => request.Messages.Any(message => message.Content.Contains("https://example.org/forest")));
    }

    [Fact]
    public async Task Repeated_Successful_Creation_Reuses_The_Same_Deck_Without_Duplicate_Paid_Work()
    {
        using var h = new Harness();
        h.QueueCreation();
        var turn = h.Turn("Создай новую презентацию про лес на 2 слайда");
        var original = h.Artifact(await h.Execute("create", turn));
        var repeated = h.Artifact(await h.Execute("create", turn));
        repeated.PresentationId.Should().Be(original.PresentationId);
        repeated.Path.Should().Be(original.Path);
        repeated.Revision.Should().Be(1);
        h.Requests.Should().HaveCount(2);
        h.ImageRequests.Should().ContainSingle();
    }

    private static void QueueWebCreation(Harness h)
    {
        h.Responses.Enqueue(JsonSerializer.Serialize(new { title = "Швейцария", theme = "ocean", slides = new[]
        {
            new { title = "Символы", layout = "photo_grid", animation = "fade", imagePrompt = "", images = new[] { new PresentationImage("web", "Switzerland flag"), new("web", "Switzerland coat of arms") } },
            new { title = "Страна", layout = "photo_grid", animation = "reveal", imagePrompt = "", images = new[] { new PresentationImage("web", "Switzerland map"), new("web", "Bern Switzerland old town") } }
        } }));
        h.Responses.Enqueue("""{"slides":[{"id":"slide-001","body":["Флаг и герб"],"notes":"Значение символов"},{"id":"slide-002","body":["Карта и Берн"],"notes":"Подробнее о стране"}]}""");
    }

    private static void QueuePhotoEdit(Harness h, Neuroset.Tools.PresentationDocument source, bool invalidPatchFirst = false)
    {
        var fields = new[] { ("slide-001", "body"), ("slide-001", "images"), ("slide-002", "body"), ("slide-002", "images"), ("slide-002", "layout") };
        h.Responses.Enqueue(JsonSerializer.Serialize(new { clarification = "", targets = fields.Select(field => new { slideId = field.Item1, field = field.Item2, quote = PhotoFeedback }) }));
        var patches = fields.Select(field =>
        {
            var slide = source.Slides.First(slide => slide.Id == field.Item1);
            var expected = field.Item2 switch
            {
                "body" => JsonSerializer.Serialize(slide.Body, DomainJson.Options), "layout" => slide.Layout,
                _ => JsonSerializer.Serialize(PresentationMedia.Images(slide).Select(image => new { image.Source, image.Query, image.Url, image.Fit }), DomainJson.Options)
            };
            var value = field.Item2 switch
            {
                "body" => "[\"Лес\"]", "layout" => "photo_grid",
                _ => JsonSerializer.Serialize(new[] { new PresentationImage("web", "Forest " + slide.Id), new("web", "Trees " + slide.Id) }, DomainJson.Options)
            };
            return new PresentationTool.FieldPatch(slide.Id, field.Item2, expected, value);
        }).ToArray();
        if (invalidPatchFirst) h.Responses.Enqueue(JsonSerializer.Serialize(new { patches = patches.Append(new("slide-001", "notes", source.Slides[0].Notes, "Unrequested explanation")) }, DomainJson.Options));
        h.Responses.Enqueue(JsonSerializer.Serialize(new { patches }, DomainJson.Options));
    }
}
