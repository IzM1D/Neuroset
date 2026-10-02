using System.Text.Json;
using Neuroset.Domain;

namespace Neuroset.Application;

public sealed record PresentationPreviewElement(int Id, string Kind, double X, double Y, double Width,
    double Height, string Text, double FontSize, string Color, bool Bold, string? ImagePath, bool Animate, string ImageFit = "cover");

public sealed record PresentationEditorSlide(string Id, int Number, string Title, string[] Body, string Notes,
    string Layout, string Animation, string Background, string TitleColor, string BodyColor, string Font,
    bool HasImage, IReadOnlyList<PresentationPreviewElement> Elements);

public sealed record PresentationEditorSnapshot(PresentationArtifact Artifact, string ThemeValue,
    IReadOnlyList<PresentationEditorSlide> Slides);

public sealed record PresentationFieldChange(string SlideId, string Field, string Expected, string Value);

public interface IPresentationEditorService
{
    Task<IPresentationEditorSession> OpenAsync(string artifactPath, CancellationToken cancellationToken = default);
}

public interface IPresentationEditorSession
{
    PresentationEditorSnapshot Snapshot { get; }
    PresentationEditorSnapshot Preview(IReadOnlyList<PresentationFieldChange> changes);
    Task<PresentationArtifact> SaveAsync(IReadOnlyList<PresentationFieldChange> changes,
        string format = PresentationPolicy.DefaultFormat, CancellationToken cancellationToken = default);
}

// Edits are accumulated independently of the rendered preview. An invalid intermediate
// text field stays editable, and navigating slides never loses unsaved input.
public sealed class PresentationEditorDraft(PresentationEditorSnapshot original)
{
    private readonly Dictionary<(string Slide, string Field), PresentationFieldChange> _changes = [];
    public bool IsDirty => _changes.Count > 0;
    public IReadOnlyList<PresentationFieldChange> Changes => _changes.Values.ToArray();
    public bool IsChanged(string slideId, string field) => _changes.ContainsKey((slideId, field));

    public string Get(string slideId, string field) => _changes.TryGetValue((slideId, field), out var change)
        ? change.Value : OriginalValue(slideId, field);

    public void Set(string slideId, string field, string value)
    {
        var expected = OriginalValue(slideId, field);
        // A color/font equal to the original can still be an explicit override of
        // a newly selected theme. Keep that choice until the theme is reverted.
        var themeOverride = IsChanged("deck", "theme") && field is "background" or "titleColor" or "bodyColor" or "font";
        if (value == expected && !themeOverride) _changes.Remove((slideId, field));
        else _changes[(slideId, field)] = new(slideId, field, expected, value);
        if (slideId == "deck" && field == "theme" && value == expected)
            foreach (var key in _changes.Where(pair => pair.Value.Expected == pair.Value.Value).Select(pair => pair.Key).ToArray()) _changes.Remove(key);
    }

    public string[] GetBody(string slideId) => JsonSerializer.Deserialize<string[]>(Get(slideId, "body"), DomainJson.Options)!;
    public void SetBody(string slideId, IEnumerable<string> paragraphs) => Set(slideId, "body", JsonSerializer.Serialize(paragraphs, DomainJson.Options));
    public void Reset() => _changes.Clear();

    private string OriginalValue(string slideId, string field)
    {
        if (slideId == "deck" && field == "theme") return original.ThemeValue;
        var slide = original.Slides.FirstOrDefault(s => s.Id == slideId) ?? throw new ArgumentException("Unknown slide.", nameof(slideId));
        return field switch
        {
            "title" => slide.Title, "body" => JsonSerializer.Serialize(slide.Body, DomainJson.Options),
            "notes" => slide.Notes, "layout" => slide.Layout, "animation" => slide.Animation,
            "background" => slide.Background, "titleColor" => slide.TitleColor, "bodyColor" => slide.BodyColor,
            "font" => slide.Font,
            _ => throw new ArgumentException("Unsupported editor field.", nameof(field))
        };
    }
}
