using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml.Validation;
using FluentAssertions;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Tools;
using PptxDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;

namespace Neuroset.Tests;

public sealed partial class PresentationTests
{
    [Theory]
    [InlineData("Создай 2 слайда про лес", "odp", "pptx")]
    [InlineData("Создай 2 слайда про лес в OpenDocument", "pptx", "odp")]
    [InlineData("Создай 2 слайда про лес в PowerPoint", "odp", "pptx")]
    public async Task User_Request_Controls_Format_And_Model_Cannot_Override_Pptx_Default(string request, string modelFormat, string expected)
    {
        using var h = new Harness();
        h.QueueCreation();
        var result = await h.Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation = "create", format = modelFormat }), h.Context(h.Turn(request)));
        var artifact = h.Artifact(result);
        artifact.Format.Should().Be(expected);
        Path.GetExtension(artifact.Path).Should().Be("." + expected);
    }

    [Fact]
    public async Task Default_Export_Converts_Odp_To_Pptx_Without_Changing_Revision_Or_Calling_Models()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес в ODP"));
        var document = await h.ReadDocument(original);
        var exported = h.Artifact(await h.Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { operation = "export", format = "odp" }), h.Context(h.Turn("Сохрани эту презентацию"))));
        exported.Format.Should().Be("pptx");
        exported.Revision.Should().Be(original.Revision);
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        var manualExport = await editor.SaveAsync([]);
        manualExport.Path.Should().Be(exported.Path);
        (await h.ReadDocument(original)).Should().BeEquivalentTo(document);
        h.Requests.Should().HaveCount(2);
        h.ImageRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Manual_Title_Edit_Preserves_All_Other_Parts_And_Opens_Latest_From_An_Old_Card()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var service = new PresentationEditorService(new(h.Directory));
        var editor = await service.OpenAsync(original.Path);
        var draft = new PresentationEditorDraft(editor.Snapshot);
        draft.Set("slide-002", "title", "Берегите лес");
        var before = Entries(original.Path);
        var saved = await editor.SaveAsync(draft.Changes);
        saved.PresentationId.Should().Be(original.PresentationId);
        saved.Revision.Should().Be(2);
        saved.Format.Should().Be("pptx");
        var after = Entries(saved.Path);
        foreach (var entry in before.Where(e => e.Key != "ppt/slides/slide2.xml")) after[entry.Key].Should().Equal(entry.Value, entry.Key);
        editor.Snapshot.Slides[1].Title.Should().Be("Шаги");
        using (var deck = PptxDocument.Open(saved.Path, false)) new OpenXmlValidator().Validate(deck).Should().BeEmpty();
        File.Delete(original.Path); // Old cards still resolve through the committed manifest.
        var reopened = await service.OpenAsync(original.Path);
        reopened.Snapshot.Artifact.Revision.Should().Be(2);
        reopened.Snapshot.Slides[1].Title.Should().Be("Берегите лес");
        (await reopened.SaveAsync([])).Path.Should().Be(saved.Path);
        h.Requests.Should().HaveCount(2);
        h.ImageRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Manual_Body_Style_Notes_And_Animation_Changes_Stay_On_The_Selected_Slide()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var document = await h.ReadDocument(original);
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        var draft = new PresentationEditorDraft(editor.Snapshot);
        draft.SetBody("slide-002", ["Посади дерево", "Забери мусор", "Береги природу"]);
        draft.Set("slide-002", "notes", "Три простых шага.");
        draft.Set("slide-002", "font", "Georgia");
        draft.Set("slide-002", "background", "FFF8F0");
        draft.Set("slide-002", "titleColor", "663311");
        draft.Set("slide-002", "bodyColor", "332211");
        draft.Set("slide-002", "animation", "none");
        var saved = await editor.SaveAsync(draft.Changes);
        var changed = await h.ReadDocument(saved);
        changed.Slides[0].Should().BeEquivalentTo(document.Slides[0]);
        changed.Slides[1].Body.Should().Equal("Посади дерево", "Забери мусор", "Береги природу");
        changed.Slides[1].Notes.Should().Be("Три простых шага.");
        changed.Slides[1].Style.Should().Be(new PresentationSlideStyle("FFF8F0", "663311", "332211", "Georgia"));
        changed.Slides[1].Title.Should().Be(document.Slides[1].Title);
        changed.Theme.Should().Be(document.Theme);
        changed.Feedback.Should().ContainSingle().Which.Request.Should().Be("Manual edit in Neuroset");
        var before = Entries(original.Path);
        var after = Entries(saved.Path);
        foreach (var entry in before.Where(e => e.Key != "ppt/slides/slide2.xml" && e.Key != "ppt/notesSlides/notesSlide2.xml"))
            after[entry.Key].Should().Equal(entry.Value, entry.Key);
        using var zip = ZipFile.OpenRead(saved.Path);
        Xml(zip, "ppt/slides/slide2.xml").Descendants(P + "timing").Should().BeEmpty();
        Xml(zip, "ppt/slides/slide1.xml").Descendants(P + "timing").Should().ContainSingle();
        h.Requests.Should().HaveCount(2);
        h.ImageRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Preview_And_Invalid_Input_Do_Not_Write_Or_Mutate_The_Source()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var document = await h.ReadDocument(original);
        var before = Directory.GetFiles(h.Directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        var draft = new PresentationEditorDraft(editor.Snapshot);
        draft.Set("slide-001", "title", "Рядом с нами");
        editor.Preview(draft.Changes).Slides[0].Title.Should().Be("Рядом с нами");
        draft.Set("slide-001", "title", "");
        Action preview = () => editor.Preview(draft.Changes);
        preview.Should().Throw<InvalidDataException>();
        Func<Task> save = () => editor.SaveAsync(draft.Changes);
        await save.Should().ThrowAsync<InvalidDataException>();
        editor.Snapshot.Slides[0].Title.Should().Be(document.Slides[0].Title);
        Directory.GetFiles(h.Directory, "*", SearchOption.AllDirectories).Should().BeEquivalentTo(before.Keys);
        foreach (var file in before) File.ReadAllBytes(file.Key).Should().Equal(file.Value);
    }

    [Fact]
    public async Task Draft_Retains_Invalid_Input_Across_Slides_And_Removes_Reverted_Deltas()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        var draft = new PresentationEditorDraft(editor.Snapshot);
        draft.Set("slide-001", "title", "");
        draft.SetBody("slide-002", ["", "Другой абзац"]);
        draft.Get("slide-001", "title").Should().BeEmpty();
        draft.GetBody("slide-002").Should().Equal("", "Другой абзац");
        draft.Set("slide-001", "title", editor.Snapshot.Slides[0].Title);
        draft.IsChanged("slide-001", "title").Should().BeFalse();
        draft.Changes.Should().ContainSingle().Which.Field.Should().Be("body");
        draft.Reset();
        draft.IsDirty.Should().BeFalse();
        draft.GetBody("slide-002").Should().Equal(editor.Snapshot.Slides[1].Body);
    }

    [Fact]
    public async Task Manual_Save_Uses_Pptx_By_Default_And_Explicit_Odp_Is_Still_Available()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес в ODP"));
        var service = new PresentationEditorService(new(h.Directory));
        var editor = await service.OpenAsync(original.Path);
        var saved = await editor.SaveAsync([new("slide-002", "title", "Шаги", "Берегите лес")]);
        saved.Format.Should().Be("pptx");
        var latest = await service.OpenAsync(original.Path);
        var converted = await latest.SaveAsync([], "odf");
        converted.Format.Should().Be("odp");
        converted.Revision.Should().Be(saved.Revision);
        using var zip = ZipFile.OpenRead(converted.Path);
        var content = Xml(zip, "content.xml");
        content.Descendants(Draw + "page").Should().HaveCount(2);
        content.ToString().Should().Contain("Берегите лес");
    }

    [Fact]
    public async Task Ai_Continues_From_Manual_Edits_And_An_Old_Editor_Cannot_Overwrite_It()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var service = new PresentationEditorService(new(h.Directory));
        var editor = await service.OpenAsync(original.Path);
        await editor.SaveAsync([new("slide-001", "title", "Лес рядом", "Природа рядом")]);
        var stale = await service.OpenAsync(original.Path);
        h.QueueTitleEdit();
        var ai = h.Artifact(await h.Execute("edit", "Измени только заголовок слайда 2 на «Берегите лес»"));
        ai.Revision.Should().Be(3);
        (await h.ReadDocument(ai)).Slides[0].Title.Should().Be("Природа рядом");
        Func<Task> overwrite = () => stale.SaveAsync([new("slide-002", "notes", "Заметки", "Старая правка")]);
        await overwrite.Should().ThrowAsync<InvalidDataException>().WithMessage("*newer revision*");
        (await h.ReadDocument(ai)).Slides[1].Notes.Should().Be("Заметки");
        h.ImageRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_Manual_Saves_Commit_Exactly_One_Revision()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var service = new PresentationEditorService(new(h.Directory));
        var first = await service.OpenAsync(original.Path);
        var second = await service.OpenAsync(original.Path);
        var results = await Task.WhenAll(Save(first, "Первый вариант"), Save(second, "Второй вариант"));
        results.Count(r => r.Artifact is not null).Should().Be(1);
        results.Count(r => r.Error is InvalidDataException).Should().Be(1);
        (await service.OpenAsync(original.Path)).Snapshot.Artifact.Revision.Should().Be(2);

        static async Task<(PresentationArtifact? Artifact, Exception? Error)> Save(IPresentationEditorSession editor, string title)
        {
            try { return (await editor.SaveAsync([new("slide-002", "title", "Шаги", title)]), null); }
            catch (Exception error) { return (null, error); }
        }
    }

    [Fact]
    public async Task Manual_Edit_Preserves_A_Pending_Ai_Revision()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var document = await h.ReadDocument(original);
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        var pendingPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(original.Path))!, "pending.json");
        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(new PresentationPending(document with { Revision = 2 }, 1, ["AI change"]), DomainJson.Options));
        var before = await File.ReadAllBytesAsync(pendingPath);
        Func<Task> save = () => editor.SaveAsync([new("slide-002", "title", "Шаги", "Ручная правка")]);
        await save.Should().ThrowAsync<InvalidDataException>().WithMessage("*pending presentation revision*");
        (await File.ReadAllBytesAsync(pendingPath)).Should().Equal(before);
        (await h.ReadDocument(original)).Should().BeEquivalentTo(document);
        (await editor.SaveAsync([])).Revision.Should().Be(1);
    }

    [Fact]
    public async Task Interrupted_Manual_Commit_Can_Be_Retried_Only_With_The_Same_Changes()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        var deckDirectory = Path.GetDirectoryName(Path.GetDirectoryName(original.Path))!;
        var obstacle = Path.Combine(deckDirectory, "revision-0002", "document.json");
        Directory.CreateDirectory(obstacle);
        PresentationFieldChange[] changes = [new("slide-002", "title", "Шаги", "Берегите лес")];
        Func<Task> save = () => editor.SaveAsync(changes);
        var error = (await save.Should().ThrowAsync<Exception>()).Which;
        (error is IOException or UnauthorizedAccessException).Should().BeTrue();
        (await h.ReadDocument(original)).Revision.Should().Be(1);
        var pending = JsonSerializer.Deserialize<PresentationPending>(await File.ReadAllTextAsync(Path.Combine(deckDirectory, "pending.json")), DomainJson.Options)!;
        pending.Origin.Should().Be("editor");
        Func<Task> different = () => editor.SaveAsync([new("slide-002", "title", "Шаги", "Другой вариант")]);
        await different.Should().ThrowAsync<InvalidDataException>().WithMessage("*pending presentation revision*");
        Directory.Delete(obstacle);
        var saved = await editor.SaveAsync(changes);
        saved.Revision.Should().Be(2);
        (await h.ReadDocument(saved)).Slides[1].Title.Should().Be("Берегите лес");
        File.Exists(Path.Combine(deckDirectory, "pending.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("imagePrompt")]
    [InlineData("imageFile")]
    [InlineData("id")]
    public async Task Editor_Rejects_Unavailable_Fields_And_Stale_Expected_Values(string field)
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        Func<Task> forbidden = () => editor.SaveAsync([new("slide-001", field, "", "replacement")]);
        await forbidden.Should().ThrowAsync<InvalidDataException>();
        Func<Task> stale = () => editor.SaveAsync([new("slide-001", "title", "Outdated title", "Replacement")]);
        await stale.Should().ThrowAsync<InvalidDataException>();
        (await h.ReadDocument(original)).Revision.Should().Be(1);
    }

    [Fact]
    public async Task Editor_Rejects_Cross_Root_Paths_And_Mismatched_Manifest_Ownership()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var wrongRoot = new PresentationEditorService(new(Path.Combine(h.Directory, "other")));
        Func<Task> crossRoot = () => wrongRoot.OpenAsync(original.Path);
        await crossRoot.Should().ThrowAsync<InvalidDataException>();
        var document = await h.ReadDocument(original);
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(original.Path))!, "document.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(document with { ConversationId = Guid.NewGuid() }, DomainJson.Options));
        Func<Task> ownership = () => new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        await ownership.Should().ThrowAsync<InvalidDataException>().WithMessage("*does not belong*");
    }

    [Fact]
    public async Task Theme_And_Local_Color_Edits_Are_Independent_Of_Patch_Order()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        PresentationFieldChange[] changes = [new("deck", "theme", "botanical", "ocean"), new("slide-002", "titleColor", "18392E", "663311")];
        var preview = editor.Preview(changes);
        editor.Preview(changes.Reverse().ToArray()).Should().BeEquivalentTo(preview);
        preview.Slides[0].TitleColor.Should().Be("12334A");
        preview.Slides[1].TitleColor.Should().Be("663311");
        preview.Slides.Select(s => s.Title).Should().Equal(editor.Snapshot.Slides.Select(s => s.Title));
        preview.Slides.SelectMany(s => s.Elements).Where(e => e.Kind == "image").Should().BeEquivalentTo(editor.Snapshot.Slides.SelectMany(s => s.Elements).Where(e => e.Kind == "image"),
            options => options.Excluding(e => e.Color));
    }

    [Fact]
    public async Task Original_Color_Can_Be_Chosen_As_An_Explicit_Override_Of_A_New_Theme()
    {
        using var h = new Harness();
        h.QueueCreation();
        var original = h.Artifact(await h.Execute("create", "Создай 2 слайда про лес"));
        var editor = await new PresentationEditorService(new(h.Directory)).OpenAsync(original.Path);
        var draft = new PresentationEditorDraft(editor.Snapshot);
        draft.Set("deck", "theme", "ocean");
        draft.Set("slide-002", "titleColor", editor.Snapshot.Slides[1].TitleColor);
        var preview = editor.Preview(draft.Changes);
        preview.Slides[0].TitleColor.Should().Be("12334A");
        preview.Slides[1].TitleColor.Should().Be("18392E");
        draft.Set("deck", "theme", "botanical");
        draft.IsDirty.Should().BeFalse();
    }
}
