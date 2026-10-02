using System.Text.RegularExpressions;
using Neuroset.Domain;

namespace Neuroset.Application;

// Supplied by the runtime, never by tool arguments. Image generation within a deck is
// independent of the one-image composer action and uses the presentation permission.
public sealed class PresentationTurnContext(ConnectionProfile connection, string userRequest, Guid userMessageId)
{
    private int _mutationAttempts;
    private readonly List<string> _research = [];
    public ConnectionProfile Connection { get; } = connection;
    public string UserRequest { get; } = userRequest;
    public Guid UserMessageId { get; } = userMessageId;
    public Guid? CreatedPresentationId { get; private set; }
    public void RememberCreation(Guid presentationId) => CreatedPresentationId = presentationId;
    public int RemainingMutationAttempts => Math.Max(0, 3 - Volatile.Read(ref _mutationAttempts));
    public bool TryBeginMutation() => Interlocked.Increment(ref _mutationAttempts) <= 3;
    public string ResearchContext => string.Join("\n", _research);
    public void AddResearch(string tool, string output)
    {
        if (tool is not ("web_search" or "fetch_url") || string.IsNullOrWhiteSpace(output)) return;
        _research.Add(tool + ": " + output[..Math.Min(output.Length, 6000)]);
        while (_research.Sum(value => value.Length) > 36_000) _research.RemoveAt(0);
    }
}

public sealed record PresentationWebImageRequest(string Query, string? Url = null);
public sealed record PresentationWebImage(byte[] PngBytes, string ImageUrl, string SourcePage, string Attribution);
public interface IPresentationWebImageProvider
{
    Task<PresentationWebImage> ResolveAsync(PresentationWebImageRequest request, CancellationToken cancellationToken = default);
}
public interface IPresentationImageDecoder
{
    Task<byte[]> ToPngAsync(byte[] bytes, CancellationToken cancellationToken = default);
}

public sealed record PresentationArtifact(
    string Kind, Guid PresentationId, int Revision, string Title, string Path,
    string Format, int SlideCount, int ImageCount, string Theme, string[] Changes);

public static partial class PresentationPolicy
{
    public const string ToolName = "presentation";
    public const string DefaultFormat = "pptx";
    public const int MaximumSlides = 40;
    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["один"] = 1, ["одна"] = 1, ["одну"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3,
        ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9,
        ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13,
        ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16, ["семнадцать"] = 17,
        ["восемнадцать"] = 18, ["девятнадцать"] = 19, ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40,
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
        ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40
    };

    public static string NormalizeFormat(string format) => format.ToLowerInvariant() switch
    {
        "pptx" or "powerpoint" => "pptx",
        "odp" or "odf" or "opendocument" => "odp",
        _ => throw new ArgumentException("Use pptx or odp (OpenDocument/ODF presentation).")
    };

    public static int? RequestedSlideCount(string request)
    {
        var match = SlideCountPattern().Match(request);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var count)) return count;
        var words = WordCountPattern().Match(request);
        if (!words.Success) return null;
        var tokens = words.Groups[1].Value.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(NumberWords.ContainsKey) ? tokens.Sum(word => NumberWords[word]) : null;
    }

    public static string? RequestedFormat(string request)
    {
        var matches = FormatPattern().Matches(request);
        return matches.Count == 0 ? null : NormalizeFormat(matches[^1].Value.TrimStart('.'));
    }

    public static bool IsRevisionRequest(string request) => RevisionPattern().IsMatch(request);
    public static bool ExplicitlyRequestsNewDeck(string request) => NewDeckPattern().IsMatch(request);
    public static bool WithoutImages(string request) => NoImagesPattern().IsMatch(request);
    public static bool WithoutAnimations(string request) => NoAnimationsPattern().IsMatch(request);
    public static bool RequestsWebImages(string request) => Regex.IsMatch(request,
        @"интернет|интерне[тт]\w*\s+источник|не\s+сгенерирован|без\s+генерац|реальн\w*\s+фото|https?://|\b(?:internet|online|web|stock|real photos?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\b(\d{1,4})\s*[-–]?\s*(?:слайд\w*|slides?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlideCountPattern();
    [GeneratedRegex(@"\b((?:(?:двадцать|тридцать|сорок|twenty|thirty|forty)[ -]+)?(?:один|одна|одну|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять|одиннадцать|двенадцать|тринадцать|четырнадцать|пятнадцать|шестнадцать|семнадцать|восемнадцать|девятнадцать|двадцать|тридцать|сорок|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty))\s+(?:слайд\w*|slides?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordCountPattern();
    [GeneratedRegex(@"\b(?:pptx|odp|odf|powerpoint|opendocument)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatPattern();
    [GeneratedRegex(@"(?:передел|исправ|измени|замени|убери|не\s+понрав|не\s+нрав|редакт|доработ|переработ|перегенер|\b(?:edit|revise|change|replace|remove|fix|redo|regenerate|dislike)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();
    [GeneratedRegex(@"(?:\bнов\w*\s+презентац|ещ[её]\s+одну\s+презентац|\b(?:new|another)\s+(?:deck|presentation)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewDeckPattern();
    [GeneratedRegex(@"(?:без\s+(?:изображений|картинок|иллюстраций)|(?:no|without)\s+(?:images|illustrations|pictures))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoImagesPattern();
    [GeneratedRegex(@"(?:без\s+анимац|(?:no|without)\s+animations?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoAnimationsPattern();

    public const string PromptInstructions = """
        Use presentation when the user wants a ready PowerPoint/PPTX or OpenDocument/ODF/ODP slide deck,
        including follow-up corrections, regenerations and format conversions. It writes the text with the
        active conversational model, chooses a coherent design when unspecified, generates illustrations,
        and embeds real transitions and entrance animations. It inherits the actual current user request;
        do not send a rewritten creative brief. Supply the exact requested slide_count (including cover and
        closing slide), or omit it to choose a suitable length. ODF presentations use the .odp extension.
        PPTX is the default. Choose ODP/ODF only when the user explicitly requests OpenDocument.
        For feedback about an existing deck, use edit, never create: the tool selects the last presentation
        in this chat unless presentation_id is given. Inspect can list slide IDs and current revisions.
        Edits freeze a minimal scope before writing and preserve all fields outside it, the slide count,
        order, and unrequested assets. If feedback is ambiguous, ask which specific part should change.
        Export changes only the file format. Resume continues a failed pending revision using saved images.
        Do not substitute render_html, canvas or Python for an actual presentation file. Do not claim a
        deck exists until the tool succeeds. The file card opens Neuroset's built-in slide viewer/editor
        and has a Save action. Manual edits are saved as revisions of the same document; inspect the
        latest revision before subsequent AI edits and preserve the user's manual corrections.
        If validation fails before saving a draft, correct the reported problem and retry the same edit.
        If a pending revision exists, use resume in the same turn: completed assets are reused. There
        are at most three mutation attempts per user turn. Never recreate a deck to recover from an error.
        If a web image cannot be downloaded, read pendingImages from the report or inspect. Search
        for an alternative public image of the SAME subject, then resume with image_sources entries
        {slide_number,image_number,url}. This changes only the missing asset's URL. Do not repeat
        identical failed source lookups or ask the user to restart the presentation. Slide text,
        style, animations and already completed images remain locked during this repair.
        Internet images are supported, including multiple images per slide. Honor requests for real
        photographs, maps, flags and coats of arms; never substitute generated images for requested web
        assets. Research results are passed to the authoring tool. Inspect actual image sources/counts
        before claiming that visual requirements are satisfied. Standalone image_generation still
        requires its composer button; presentation illustrations are covered by the presentation tool.
        """;
}
