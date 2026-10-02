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
using DeckDocument = Neuroset.Tools.PresentationDocument;
using PptxDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;

namespace Neuroset.Tests;

public sealed partial class PresentationTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Anim = "urn:oasis:names:tc:opendocument:xmlns:animation:1.0";
    private static readonly XNamespace Smil = "urn:oasis:names:tc:opendocument:xmlns:smil-compatible:1.0";

    [Theory]
    [InlineData("pptx")]
    [InlineData("odp")]
    [InlineData("odf")]
    public async Task Creates_An_Editable_Animated_Deck_With_Exact_Count_And_Embedded_Images(string format)
    {
        using var h = new Harness();
        h.QueueCreation();
        var result = await h.Execute("create", $"Создай презентацию про лес на 2 слайда в {format}");
        result.Success.Should().BeTrue(result.Error);
        var artifact = h.Artifact(result);
        artifact.SlideCount.Should().Be(2);
        artifact.ImageCount.Should().Be(1);
        Path.GetExtension(artifact.Path).Should().Be(format == "pptx" ? ".pptx" : ".odp");
        h.ImageRequests.Should().ContainSingle().Which.Prompt.Should().Contain("botanical");
        h.Requests.Should().OnlyContain(r => r.Tools.Count == 0 && !r.Generation!.EnableTools);
        using var zip = ZipFile.OpenRead(artifact.Path);
        if (format == "pptx")
        {
            using var pptx = PptxDocument.Open(artifact.Path, false);
            new OpenXmlValidator().Validate(pptx).Should().BeEmpty();
            pptx.PresentationPart!.SlideParts.Should().HaveCount(2);
            var slide = Xml(zip, "ppt/slides/slide1.xml");
            slide.Descendants(A + "t").Should().Contain(t => t.Value == "Лес рядом");
            slide.Descendants(P + "transition").Should().ContainSingle();
            slide.Descendants(P + "animEffect").Should().HaveCount(3);
            var targets = slide.Descendants(P + "spTgt").Select(e => e.Attribute("spid")!.Value).ToArray();
            var ids = slide.Descendants(P + "cNvPr").Select(e => e.Attribute("id")!.Value).ToHashSet();
            targets.Should().OnlyContain(id => ids.Contains(id));
            zip.Entries.Where(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)).Should().ContainSingle();
        }
        else
        {
            zip.Entries[0].FullName.Should().Be("mimetype");
            zip.Entries[0].CompressedLength.Should().Be(zip.Entries[0].Length);
            using var reader = new StreamReader(zip.Entries[0].Open());
            reader.ReadToEnd().Should().Be(PresentationOdpWriter.MediaType);
            var content = Xml(zip, "content.xml");
            var pages = content.Descendants(Draw + "page").ToArray();
            pages.Should().HaveCount(2);
            foreach (var page in pages)
            {
                var ids = page.Descendants().Attributes(XNamespace.Xml + "id").Select(a => a.Value).ToHashSet();
                page.Descendants(Anim + "transitionFilter").Should().NotBeEmpty().And.OnlyContain(a => ids.Contains(a.Attribute(Smil + "targetElement")!.Value));
            }
            zip.Entries.Where(e => e.FullName.StartsWith("Pictures/", StringComparison.Ordinal)).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Title_Edit_Preserves_Unrequested_Slide_Parts_Images_Notes_And_Animations_After_Reload()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай презентацию про лес на 2 слайда"));
        var before = Entries(original.Path);
        h.QueueTitleEdit();
        // Fresh tool instance and no chat-history dependency, as after restarting the app.
        h.RecreateTool();
        var changed = h.Artifact(await h.Execute("edit", "Измени только заголовок слайда 2 на «Берегите лес»"));
        changed.PresentationId.Should().Be(original.PresentationId);
        changed.Revision.Should().Be(2);
        changed.Path.Should().NotBe(original.Path);
        var after = Entries(changed.Path);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach (var part in before.Where(p => p.Key != "ppt/slides/slide2.xml")) after[part.Key].Should().Equal(part.Value, part.Key);
        using var oldZip = ZipFile.OpenRead(original.Path);
        using var newZip = ZipFile.OpenRead(changed.Path);
        var oldSlide = Xml(oldZip, "ppt/slides/slide2.xml");
        var newSlide = Xml(newZip, "ppt/slides/slide2.xml");
        newSlide.Descendants(P + "timing").Single().ToString().Should().Be(oldSlide.Descendants(P + "timing").Single().ToString());
        newSlide.Descendants(A + "t").Should().Contain(t => t.Value == "Берегите лес");
        h.ImageRequests.Should().ContainSingle();
        var doc = await h.ReadDocument(changed);
        doc.Feedback.Should().ContainSingle().Which.Request.Should().Contain("только заголовок");
    }

    [Fact]
    public async Task Format_Conversion_Does_Not_Call_Models_Or_Change_Authored_State()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var document = await h.ReadDocument(original);
        var converted = h.Artifact(await h.Execute("export", "Сохрани эту презентацию в ODF"));
        converted.Format.Should().Be("odp");
        converted.PresentationId.Should().Be(original.PresentationId);
        converted.Revision.Should().Be(original.Revision);
        h.Requests.Should().HaveCount(2);
        (await h.ReadDocument(converted)).Should().BeEquivalentTo(document);
    }

    [Fact]
    public async Task Wrong_Count_Is_Rejected_Before_Paid_Generation()
    {
        using var h = new Harness();
        h.QueueCreation();
        var result = await h.Execute("create", "Создай 3 слайда про лес");
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("exact requested slide count");
        h.ImageRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_Image_Generation_Can_Resume_In_The_Same_Turn_And_Reuses_Completed_Assets()
    {
        using var h = new Harness { FailImageAttempt = 2 };
        h.QueueCreation(twoImages: true);
        var turn = h.Turn("Создай 2 слайда про лес");
        var first = await h.Execute("create", turn);
        first.Success.Should().BeFalse();
        h.ImageRequests.Should().HaveCount(2);
        var resumed = h.Artifact(await h.Execute("resume", turn));
        resumed.ImageCount.Should().Be(2);
        h.ImageRequests.Should().HaveCount(3);
        h.ImageRequests[0].Prompt.Should().NotBe(h.ImageRequests[2].Prompt);
        h.Requests.Should().HaveCount(2); // No replanning of a saved draft.
    }

    [Fact]
    public async Task Missing_Image_Model_Preserves_Draft_And_Can_Resume_On_Another_Connection()
    {
        using var h = new Harness { Models = ["chat"] };
        h.QueueCreation();
        (await h.Execute("create", "Создай 2 слайда про лес")).Success.Should().BeFalse();
        h.ImageRequests.Should().BeEmpty();
        h.Models = ["gpt-image-2"];
        h.Artifact(await h.Execute("resume", "Продолжи")).ImageCount.Should().Be(1);
    }

    [Fact]
    public async Task Explicitly_Disabling_Images_And_Animations_Uses_No_Image_Api_And_No_Timing()
    {
        using var h = new Harness { Models = ["chat"] };
        h.QueueCreation();
        var artifact = h.Artifact(await h.Execute("create", "Создай два слайда про лес без изображений и без анимаций"));
        artifact.SlideCount.Should().Be(2);
        artifact.ImageCount.Should().Be(0);
        h.ImageRequests.Should().BeEmpty();
        using var zip = ZipFile.OpenRead(artifact.Path);
        foreach (var n in new[] { 1, 2 })
        {
            var slide = Xml(zip, $"ppt/slides/slide{n}.xml");
            slide.Descendants(P + "timing").Should().BeEmpty();
            slide.Descendants(P + "transition").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Regenerating_One_Illustration_Does_Not_Reuse_The_Rejected_Image_Or_Regenerate_Others()
    {
        using var h = new Harness();
        h.QueueCreation(twoImages: true);
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var document = await h.ReadDocument(original);
        h.Responses.Enqueue("""{"clarification":"","targets":[{"slideId":"slide-001","field":"imagePrompt","quote":"Перегенерируй изображение на слайде 1"}]}""");
        h.Responses.Enqueue(JsonSerializer.Serialize(new { patches = new[] { new { slideId = "slide-001", field = "imagePrompt", expected = document.Slides[0].ImagePrompt, value = document.Slides[0].ImagePrompt } } }));
        var changed = h.Artifact(await h.Execute("edit", "Перегенерируй изображение на слайде 1"));
        var updated = await h.ReadDocument(changed);
        h.ImageRequests.Should().HaveCount(3);
        updated.Slides[0].ImageFile.Should().NotBe(document.Slides[0].ImageFile);
        updated.Slides[1].Should().BeEquivalentTo(document.Slides[1]);
        updated.Slides[0].Title.Should().Be(document.Slides[0].Title);
    }

    [Fact]
    public async Task Cancelling_An_Edit_Preserves_The_Committed_Deck()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var before = await h.ReadDocument(original);
        using var cancellation = new CancellationTokenSource();
        h.Provider.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            cancellation.Cancel();
            await Task.Delay(1, (CancellationToken)call[1]);
            return new LlmResponse("{}", [], null, "stop");
        });
        Func<Task> run = async () => await h.Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation = "edit" }), h.Context(h.Turn("Измени заголовок слайда 2")), cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        (await h.ReadDocument(original)).Should().BeEquivalentTo(before);
        h.ImageRequests.Should().ContainSingle();
    }

    [Fact]
    public void Local_Title_Color_Change_Preserves_Text_And_Deck_Theme()
    {
        var document = Sample();
        const string request = "Измени цвет заголовка слайда 2";
        PresentationTool.EditTarget[] scope = [new("slide-002", "titleColor", request)];
        PresentationTool.ValidateScope(scope, request, document);
        var updated = PresentationTool.ApplyPatch(document, scope, [new("slide-002", "titleColor", "18392E", "993311")]);
        PresentationLayout.Validate(updated);
        updated.Theme.Should().Be(document.Theme);
        updated.Slides[0].Should().BeEquivalentTo(document.Slides[0]);
        updated.Slides[1].Title.Should().Be(document.Slides[1].Title);
        updated.Slides[1].Style!.TitleColor.Should().Be("993311");
        Action global = () => PresentationTool.ValidateScope([new("deck", "theme", request)], request, document);
        global.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Custom_Theme_Respects_Requested_Colors_And_Rejects_Unreadable_Contrast()
    {
        var theme = new PresentationTheme("Red and cream", "FFF8F0", "261914", "A12722", "675750", "Georgia");
        PresentationTheme.Resolve(JsonSerializer.Serialize(theme, DomainJson.Options)).Should().Be(theme);
        Action unreadable = () => PresentationTheme.Resolve(JsonSerializer.Serialize(theme with { Foreground = "FFF8F0" }, DomainJson.Options));
        unreadable.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Tool_Schema_Rejects_A_Model_Supplied_Replacement_Deck()
    {
        using var h = new Harness();
        var executor = new ToolExecutor(new ToolRegistry([h.Tool]), new FixedPermissionService(PermissionDecision.AlwaysAllow), NullLogger<ToolExecutor>.Instance);
        var result = await executor.ExecuteAsync(PresentationPolicy.ToolName, JsonSerializer.SerializeToElement(new { operation = "edit", slides = new[] { new { title = "Unrequested content" } } }), h.Context(h.Turn("Исправь заголовок")));
        result.Success.Should().BeFalse();
        h.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Ambiguous_Feedback_Does_Not_Change_The_Deck()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        h.Responses.Enqueue("""{"clarification":"Что именно изменить? Укажите слайд и элемент.","targets":[]}""");
        var result = await h.Execute("edit", "Мне не понравилось");
        result.Success.Should().BeFalse();
        (await h.ReadDocument(original)).Revision.Should().Be(1);
        h.ImageRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Creating_Another_Deck_For_A_Correction_Is_Blocked()
    {
        using var h = new Harness();
        h.QueueCreation();
        await h.Execute("create", "Создай 2 слайда про лес");
        var result = await h.Execute("create", "Не понравился заголовок слайда 2, переделай его");
        result.Success.Should().BeFalse();
        h.Requests.Should().HaveCount(2);
        h.ImageRequests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("slide-001", "title")]
    [InlineData("slide-002", "notes")]
    [InlineData("deck", "theme")]
    public void Scope_Cannot_Expand_An_Explicit_Title_Request(string slide, string field)
    {
        const string request = "Измени только заголовок слайда 2";
        Action edit = () => PresentationTool.ValidateScope([new(slide, field, request)], request, Sample());
        edit.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Patch_Cannot_Add_Unscoped_Fields_Or_Use_Stale_Values()
    {
        var doc = Sample();
        PresentationTool.EditTarget[] scope = [new("slide-002", "title", "заголовок")];
        Action extra = () => PresentationTool.ApplyPatch(doc, scope, [new("slide-002", "title", "Шаги", "Новый"), new("slide-001", "title", "Лес рядом", "Другое")]);
        Action stale = () => PresentationTool.ApplyPatch(doc, scope, [new("slide-002", "title", "Не тот заголовок", "Новый")]);
        extra.Should().Throw<InvalidDataException>();
        stale.Should().Throw<InvalidDataException>();
        doc.Slides[0].Title.Should().Be("Лес рядом");
    }

    [Fact]
    public async Task Scope_Violation_Stops_Before_Any_Rewrite_Or_Image_Request()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        h.Responses.Enqueue("""{"clarification":"","targets":[{"slideId":"slide-001","field":"title","quote":"Измени заголовок слайда 2"}]}""");
        h.Responses.Enqueue("""{"clarification":"","targets":[{"slideId":"slide-001","field":"title","quote":"Измени заголовок слайда 2"}]}""");
        (await h.Execute("edit", "Измени заголовок слайда 2")).Success.Should().BeFalse();
        h.Requests.Should().HaveCount(4);
        (await h.ReadDocument(original)).Revision.Should().Be(1);
    }

    [Fact]
    public async Task Stale_Revision_And_Cross_Conversation_Id_Are_Rejected()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var stale = await h.Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation = "edit", expected_revision = 99 }), h.Context(h.Turn("Измени заголовок")));
        stale.Success.Should().BeFalse();
        var cross = await h.Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation = "inspect", presentation_id = original.PresentationId }), h.Context(h.Turn("Покажи")) with { ConversationId = Guid.NewGuid() });
        cross.Success.Should().BeFalse();
        h.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Untrusted_Metadata_Cannot_Authorize_Presentation_Models()
    {
        using var h = new Harness();
        var context = h.Context(h.Turn("Создай")) with { Presentation = null, Metadata = new Dictionary<string, string> { ["user_request"] = "Создай презентацию" } };
        (await h.Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation = "create" }), context)).Success.Should().BeFalse();
        h.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Runtime_Provides_Actual_User_Request_And_Persists_The_File_Result()
    {
        using var h = new Harness();
        h.QueueCreation();
        h.Provider.StreamAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(call => Stream((LlmRequest)call[0]));
        var repo = new InMemoryConversationRepository();
        var registry = new ToolRegistry([h.Tool]);
        var runtime = new AgentRuntime(h.Factory, registry, new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AlwaysAllow), NullLogger<ToolExecutor>.Instance), repo,
            new InMemoryMemoryStore(), new InMemorySettingsRepository(), NullLogger<AgentRuntime>.Instance);
        var user = new ChatMessage { ConversationId = h.ConversationId, Role = MessageRole.User, Content = "Создай 2 слайда про лес" };
        var events = new List<ModelEvent>();
        await foreach (var item in runtime.RunAsync(new(new Conversation { Id = h.ConversationId }, [user], new ConnectionProfile { Model = "chat" }, new AgentRunOptions(2, Reasoning: ReasoningPreference.High)))) events.Add(item);
        events.OfType<ToolExecutionCompletedEvent>().Should().ContainSingle(e => e.Succeeded);
        repo.Messages.SelectMany(m => m.ToolCalls).Should().ContainSingle(c => c.Execution != null && c.Execution.Metadata.ContainsKey("presentation_id"));

        static async IAsyncEnumerable<ModelEvent> Stream(LlmRequest request)
        {
            request.Messages.Should().Contain(m => m.Content.Contains(PresentationPolicy.PromptInstructions, StringComparison.Ordinal));
            if (request.Messages.Any(m => m.Role == MessageRole.Tool)) yield return new TextDeltaEvent("Готово.");
            else
            {
                yield return new ToolCallStartedEvent("deck", PresentationPolicy.ToolName);
                yield return new ToolCallArgumentsEvent("deck", """{"operation":"create","slide_count":2}""");
            }
            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("Сделай 12 слайдов", 12)]
    [InlineData("Create 1 slide", 1)]
    [InlineData("Create 100 slides", 100)]
    [InlineData("Сделай десять слайдов", 10)]
    [InlineData("Создай двадцать три слайда", 23)]
    [InlineData("Make twenty-one slides", 21)]
    public void Extracts_Explicit_Count_Without_Silently_Clamping(string request, int count) => PresentationPolicy.RequestedSlideCount(request).Should().Be(count);

    [Fact]
    public void Rejects_Overflow_Instead_Of_Shrinking_Text()
    {
        var doc = Sample();
        doc.Slides[0] = doc.Slides[0] with { Title = new string('Ш', 95) };
        Action validate = () => PresentationLayout.Validate(doc);
        validate.Should().Throw<InvalidDataException>();
    }

    private static DeckDocument Sample() => new(Guid.NewGuid(), Guid.NewGuid(), 1, "Лес", "Про лес", "botanical", "pptx",
        [new("slide-001", "Лес рядом", ["Ценность природы"], "Описание", "cover", "fade", "A forest", "image.png"),
         new("slide-002", "Шаги", ["Беречь деревья", "Сохранять чистоту"], "Заметки", "editorial", "reveal", "")], [], DateTimeOffset.UtcNow);

    private static XDocument Xml(ZipArchive zip, string name) { using var stream = zip.GetEntry(name)!.Open(); return XDocument.Load(stream); }
    private static Dictionary<string, byte[]> Entries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var stream = e.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray(); }, StringComparer.Ordinal);
    }

    private sealed class Harness : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "NeurosetPresentationTests", Guid.NewGuid().ToString("N"));
        public Guid ConversationId { get; } = Guid.NewGuid();
        public ConnectionProfile Connection { get; } = new() { Model = "chat" };
        public Queue<string> Responses { get; } = new();
        public List<LlmRequest> Requests { get; } = [];
        public List<ImageGenerationRequest> ImageRequests { get; } = [];
        public int? FailImageAttempt { get; init; }
        public int? FailWebImageAttempt { get; init; }
        public List<PresentationWebImageRequest> WebImageRequests { get; } = [];
        public string[] Models { get; set; } = ["chat", "gpt-image-2"];
        public ILLMProvider Provider { get; } = Substitute.For<ILLMProvider>();
        public ILLMProviderFactory Factory { get; } = Substitute.For<ILLMProviderFactory>();
        private IImageGenerationProvider ImageProvider { get; } = Substitute.For<IImageGenerationProvider>();
        private IPresentationWebImageProvider WebImageProvider { get; } = Substitute.For<IPresentationWebImageProvider>();
        public PresentationTool Tool { get; private set; }

        public Harness()
        {
            Factory.Create(Arg.Any<ConnectionProfile>()).Returns(Provider);
            Provider.ListModelsAsync(Arg.Any<ConnectionProfile>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<string>>(Models));
            Provider.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Requests.Add((LlmRequest)call[0]);
                return Task.FromResult(new LlmResponse(Responses.Dequeue(), [], null, "stop"));
            });
            ImageProvider.GenerateImageAsync(Arg.Any<ImageGenerationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                ImageRequests.Add((ImageGenerationRequest)call[0]);
                if (ImageRequests.Count == FailImageAttempt) throw new IOException("Image API failed");
                return Task.FromResult(new GeneratedImageData(Convert.FromBase64String(Png), "image/png"));
            });
            WebImageProvider.ResolveAsync(Arg.Any<PresentationWebImageRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var request = (PresentationWebImageRequest)call[0];
                WebImageRequests.Add(request);
                if (WebImageRequests.Count == FailWebImageAttempt) throw new IOException("Web image failed");
                return Task.FromResult(new PresentationWebImage(Convert.FromBase64String(Png), "https://images.example/" + Uri.EscapeDataString(request.Query) + ".png",
                    "https://sources.example/" + Uri.EscapeDataString(request.Query), "Photographer; CC BY 4.0"));
            });
            Tool = new(Factory, ImageProvider, new(Directory), WebImageProvider);
        }

        public void RecreateTool() => Tool = new(Factory, ImageProvider, new(Directory), WebImageProvider);
        public PresentationTurnContext Turn(string text) => new(Connection, text, Guid.NewGuid());
        public ToolExecutionContext Context(PresentationTurnContext turn) => new(ConversationId, Guid.NewGuid().ToString("N"), Directory, false, new Dictionary<string, string>(), Presentation: turn);
        public Task<ToolExecutionResult> Execute(string operation, string request) => Execute(operation, Turn(request));
        public Task<ToolExecutionResult> Execute(string operation, PresentationTurnContext turn) => Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation }), Context(turn));
        public PresentationArtifact Artifact(ToolExecutionResult result)
        {
            result.Success.Should().BeTrue(result.Error);
            var artifact = JsonSerializer.Deserialize<PresentationArtifact>(result.Output, DomainJson.Options)!;
            artifact.Path.Should().StartWith(Path.Combine(Directory, ConversationId.ToString("N")));
            return artifact;
        }
        public async Task<DeckDocument> ReadDocument(PresentationArtifact artifact) => JsonSerializer.Deserialize<DeckDocument>(await File.ReadAllTextAsync(Path.Combine(Directory, ConversationId.ToString("N"), "presentations", artifact.PresentationId.ToString("N"), "document.json")), DomainJson.Options)!;

        public void QueueCreation(bool twoImages = false)
        {
            Responses.Enqueue(JsonSerializer.Serialize(new
            {
                title = "Лес", theme = "botanical", slides = new[]
                {
                    new { title = "Лес рядом", layout = "cover", animation = "fade", imagePrompt = "A peaceful forest in soft daylight, central composition" },
                    new { title = "Шаги", layout = twoImages ? "split" : "editorial", animation = "reveal", imagePrompt = twoImages ? "A young sapling among moss" : "" }
                }
            }));
            Responses.Enqueue("""{"slides":[{"id":"slide-001","body":["Ценность природы"],"notes":"Описание леса"},{"id":"slide-002","body":["Беречь деревья","Сохранять чистоту"],"notes":"Заметки"}]}""");
        }

        public void QueueTitleEdit()
        {
            Responses.Enqueue("""{"clarification":"","targets":[{"slideId":"slide-002","field":"title","quote":"Измени только заголовок слайда 2"}]}""");
            Responses.Enqueue("""{"patches":[{"slideId":"slide-002","field":"title","expected":"Шаги","value":"Берегите лес"}]}""");
        }

        public void Dispose()
        {
            var path = Path.GetFullPath(Directory);
            if (path.StartsWith(Path.Combine(Path.GetTempPath(), "NeurosetPresentationTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, true);
        }
    }
}
