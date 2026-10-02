using System.Globalization;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;
using static Neuroset.Tools.PresentationStorage;

namespace Neuroset.Tools;

public sealed class PresentationEditorService(HtmlArtifactOptions options) : IPresentationEditorService
{
    public async Task<IPresentationEditorSession> OpenAsync(string artifactPath, CancellationToken cancellationToken = default)
    {
        var location = ResolveLocation(artifactPath);
        // The pointer is atomically replaced by both writers. Opening a viewer does not
        // wait for a potentially long AI generation holding the mutation gate.
        var document = await ReadAsync<PresentationDocument>(Path.Combine(location.Directory, "document.json"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The editable presentation is no longer available.");
        ValidateIdentity(document, location.ConversationId, location.Id);
        return new Session(document, location.Directory);
    }

    private (string Directory, Guid ConversationId, Guid Id) ResolveLocation(string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath) || !Path.IsPathFullyQualified(artifactPath))
            throw new InvalidDataException("Open a presentation created in Neuroset from its chat card.");
        var root = Path.GetFullPath(options.RootDirectory);
        var path = Path.GetFullPath(artifactPath);
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 5 || !Guid.TryParseExact(parts[0], "N", out var conversationId) || parts[1] != "presentations" ||
            !Guid.TryParseExact(parts[2], "N", out var id) || !parts[3].StartsWith("revision-", StringComparison.Ordinal) ||
            !int.TryParse(parts[3].AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1 ||
            parts[3] != $"revision-{revision:D4}" || parts[4] is not ("presentation.pptx" or "presentation.odp"))
            throw new InvalidDataException("The built-in editor opens presentations created in Neuroset. External decks are not supported.");
        var directory = Path.Combine(root, conversationId.ToString("N"), "presentations", id.ToString("N"));
        RejectReparsePoints(directory);
        return (directory, conversationId, id);
    }

    private static void ValidateIdentity(PresentationDocument document, Guid conversationId, Guid id)
    {
        if (document.ConversationId != conversationId || document.Id != id)
            throw new InvalidDataException("The presentation does not belong to this chat.");
        PresentationLayout.Validate(document);
    }

    private sealed class Session : IPresentationEditorSession
    {
        private readonly PresentationDocument _source;
        private readonly string _directory;

        public Session(PresentationDocument source, string directory)
        {
            _source = source;
            _directory = directory;
            Snapshot = MakeSnapshot(source, directory);
        }

        public PresentationEditorSnapshot Snapshot { get; }

        public PresentationEditorSnapshot Preview(IReadOnlyList<PresentationFieldChange> changes) =>
            MakeSnapshot(ApplyChanges(_source, changes), _directory);

        public async Task<PresentationArtifact> SaveAsync(IReadOnlyList<PresentationFieldChange> changes,
            string format = PresentationPolicy.DefaultFormat, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(changes);
            var frozen = changes.ToArray();
            var candidate = ApplyChanges(_source, frozen);
            PresentationLayout.Validate(candidate);
            format = PresentationPolicy.NormalizeFormat(format);
            var gate = GetGate(_source.ConversationId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await ReadAsync<PresentationDocument>(Path.Combine(_directory, "document.json"), cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The editable presentation is no longer available.");
                ValidateIdentity(current, _source.ConversationId, _source.Id);
                if (current.Revision != _source.Revision)
                    throw new InvalidDataException("The presentation has a newer revision. Reload it before saving; your changes have not been overwritten.");
                var pending = await ReadAsync<PresentationPending>(Path.Combine(_directory, "pending.json"), cancellationToken).ConfigureAwait(false);
                if (pending?.Document.Revision == current.Revision) pending = null;
                if (frozen.Length == 0)
                {
                    // Saving a copy or converting the file does not invent an edited revision.
                    var path = await ExportAsync(current, _directory, format, cancellationToken).ConfigureAwait(false);
                    return CreateArtifact(current, path, format, []);
                }

                var labels = frozen.Select(c => c.SlideId == "deck" ? "Theme" :
                    $"Slide {Array.FindIndex(current.Slides, s => s.Id == c.SlideId) + 1}: {c.Field}").ToArray();
                candidate = candidate with
                {
                    Revision = current.Revision + 1, Format = format, UpdatedAt = DateTimeOffset.UtcNow,
                    Feedback = current.Feedback.Append(new PresentationFeedback(current.Revision + 1, "Manual edit in Neuroset", labels)).ToArray()
                };
                if (pending is not null)
                {
                    // A manual save interrupted during export can be retried without
                    // replacing a different pending AI/user edit.
                    if (pending.Origin != "editor" || pending.BaseRevision != current.Revision || !SameContent(pending.Document, candidate))
                        throw new InvalidDataException("A pending presentation revision already exists. Resume it in the chat before saving other changes.");
                }
                else
                {
                    pending = new(candidate, current.Revision, labels, "editor");
                    await PresentationStorage.SaveAsync(Path.Combine(_directory, "pending.json"), pending, cancellationToken).ConfigureAwait(false);
                }
                return await CommitAsync(pending, _directory, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        private static bool SameContent(PresentationDocument left, PresentationDocument right) =>
            left.Id == right.Id && left.Revision == right.Revision && left.Format == right.Format && left.Theme == right.Theme &&
            JsonSerializer.Serialize(left.Slides, DomainJson.Options) == JsonSerializer.Serialize(right.Slides, DomainJson.Options);
    }

    private static PresentationDocument ApplyChanges(PresentationDocument source, IReadOnlyList<PresentationFieldChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count > source.Slides.Length * 10 + 1 || changes.Select(c => (c.SlideId, c.Field)).Distinct().Count() != changes.Count)
            throw new InvalidDataException("Each existing field can be changed only once.");
        var result = source with { Slides = source.Slides.Select(s => s with { Body = s.Body.ToArray() }).ToArray() };
        foreach (var change in changes)
        {
            if (change.Value is null || change.Expected is null || change.Value.Length > 6000)
                throw new InvalidDataException("An editor field is invalid or too long.");
            if (change.Field == "body")
            {
                var index = Array.FindIndex(source.Slides, s => s.Id == change.SlideId);
                if (index < 0 || change.Expected != JsonSerializer.Serialize(source.Slides[index].Body, DomainJson.Options))
                    throw new InvalidDataException("The original slide text no longer matches this edit.");
                var paragraphs = JsonSerializer.Deserialize<string[]>(change.Value, DomainJson.Options)
                    ?? throw new InvalidDataException("Body must be an array of paragraphs.");
                result.Slides[index] = result.Slides[index] with { Body = paragraphs };
                continue;
            }
            if (change.Field is not ("title" or "notes" or "layout" or "animation" or "background" or "titleColor" or "bodyColor" or "font" or "theme") ||
                (change.Field == "theme") != (change.SlideId == "deck"))
                throw new InvalidDataException("The editor can change only text, layout, style and animation fields.");
            if (PresentationTool.GetField(source, change.SlideId, change.Field) != change.Expected)
                throw new InvalidDataException("The original field no longer matches this edit.");
            // Expected values refer to the source, even when an earlier change selected a theme.
            var expectedNow = PresentationTool.GetField(result, change.SlideId, change.Field);
            result = PresentationTool.ApplyPatch(result, [new(change.SlideId, change.Field, "Manual edit")],
                [new(change.SlideId, change.Field, expectedNow, change.Value)]);
        }
        return result;
    }

    private static PresentationEditorSnapshot MakeSnapshot(PresentationDocument document, string directory)
    {
        PresentationLayout.Validate(document);
        var theme = PresentationTheme.Resolve(document.Theme);
        var slides = document.Slides.Select((slide, index) =>
        {
            var localTheme = PresentationTheme.ForSlide(document, slide);
            var elements = PresentationLayout.Compose(document, index).Select(element =>
            {
                var imagePath = element.ImageFile is null ? null : Path.Combine(directory, "assets", element.ImageFile);
                if (imagePath is not null) RejectReparsePoints(imagePath);
                return new PresentationPreviewElement(element.Id, element.Kind, element.X, element.Y, element.Width, element.Height,
                    element.Text, element.FontSize, element.Color, element.Bold, imagePath, element.Animate, element.ImageFit);
            }).ToArray();
            return new PresentationEditorSlide(slide.Id, index + 1, slide.Title, slide.Body.ToArray(), slide.Notes, slide.Layout, slide.Animation,
                localTheme.Background, slide.Style?.TitleColor ?? theme.Foreground, slide.Style?.BodyColor ?? theme.Foreground, localTheme.Font,
                PresentationMedia.Images(slide).Any(image => image.File is not null), elements);
        }).ToArray();
        var path = Path.Combine(directory, $"revision-{document.Revision:D4}", "presentation." + document.Format);
        return new(CreateArtifact(document, path, document.Format, []), document.Theme, slides);
    }
}
