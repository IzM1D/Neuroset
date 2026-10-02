using System.Globalization;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed record PresentationTheme(string Name, string Background, string Foreground, string Accent, string Muted, string Font = "Arial")
{
    private static readonly string[] Fonts = ["Arial", "Aptos", "Georgia", "Trebuchet MS", "Verdana", "Tahoma"];
    public static IReadOnlyDictionary<string, PresentationTheme> Presets { get; } = new Dictionary<string, PresentationTheme>(StringComparer.Ordinal)
    {
        ["midnight"] = new("midnight", "101B30", "F4F6FA", "79D9CF", "B1BED0"),
        ["editorial"] = new("editorial", "F5F0E8", "252A32", "994122", "626368"),
        ["botanical"] = new("botanical", "F0F4EC", "18392E", "347854", "51685B"),
        ["gallery"] = new("gallery", "FCFAFF", "27203D", "7351B5", "696078"),
        ["ocean"] = new("ocean", "F1F7FA", "12334A", "176783", "506779"),
        ["ember"] = new("ember", "251D23", "FFF3EB", "FFAD78", "D1B9B5")
    };

    public static PresentationTheme Resolve(string value)
    {
        if (Presets.TryGetValue(value, out var preset)) return preset;
        PresentationTheme theme;
        try { theme = JsonSerializer.Deserialize<PresentationTheme>(value, DomainJson.Options) ?? throw new InvalidDataException("Missing custom theme."); }
        catch (JsonException) { throw new InvalidDataException("Choose a built-in theme or a custom theme object."); }
        if (string.IsNullOrWhiteSpace(theme.Name) || theme.Name.Length > 60 || !Fonts.Contains(theme.Font, StringComparer.Ordinal) ||
            !IsColor(theme.Background) || !IsColor(theme.Foreground) || !IsColor(theme.Accent) || !IsColor(theme.Muted) ||
            Contrast(theme.Background, theme.Foreground) < 4.5 || Contrast(theme.Background, theme.Muted) < 4.5)
            throw new InvalidDataException("Use six-digit RGB colors, a supported font, and readable text contrast (at least 4.5:1).");
        return theme;
    }

    public static bool IsColor(string? color) => color is { Length: 6 } && color.All(Uri.IsHexDigit);
    private static double Contrast(string a, string b)
    {
        static double Luminance(string c)
        {
            var channels = Enumerable.Range(0, 3).Select(i => int.Parse(c.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d)
                .Select(v => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4)).ToArray();
            return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
        }
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    public static PresentationTheme ForSlide(PresentationDocument document, PresentationSlide slide)
    {
        var theme = Resolve(document.Theme);
        return theme with { Background = slide.Style?.Background ?? theme.Background, Font = slide.Style?.Font ?? theme.Font };
    }

    public static bool IsFont(string? font) => font is not null && Fonts.Contains(font, StringComparer.Ordinal);
}

public sealed record PresentationSlideStyle(string? Background = null, string? TitleColor = null, string? BodyColor = null, string? Font = null);
public sealed record PresentationImage(string Source, string Query, string? Url = null, string? File = null,
    string? SourcePage = null, string? Attribution = null, string Fit = "contain");

public sealed record PresentationSlide(
    string Id, string Title, string[] Body, string Notes, string Layout, string Animation,
    string ImagePrompt, string? ImageFile = null, PresentationSlideStyle? Style = null, PresentationImage[]? Images = null);

public sealed record PresentationFeedback(int Revision, string Request, string[] Changes);

public sealed record PresentationDocument(
    Guid Id, Guid ConversationId, int Revision, string Title, string Brief, string Theme,
    string Format, PresentationSlide[] Slides, PresentationFeedback[] Feedback, DateTimeOffset UpdatedAt);

public sealed record PresentationImageFailure(string SlideId, int ImageNumber, string Query, string Error, int Attempts = 1);
public sealed record PresentationPending(PresentationDocument Document, int BaseRevision, string[] Changes, string Origin = "agent", PresentationImageFailure[]? ImageFailures = null);

public sealed record SlideElement(int Id, string Kind, double X, double Y, double Width, double Height,
    string Text, double FontSize, string Color, bool Bold = false, string? ImageFile = null, bool Animate = true, string ImageFit = "cover");

// One measured composition is consumed by both exporters. Text is native and editable.
public static class PresentationLayout
{
    public const double Width = 960;
    public const double Height = 540;
    public static readonly string[] Layouts = ["cover", "split", "image_left", "editorial", "statement", "photo_grid"];
    public static readonly string[] Animations = ["fade", "reveal", "none"];

    public static IReadOnlyList<SlideElement> Compose(PresentationDocument document, int index)
    {
        var slide = document.Slides[index];
        var theme = PresentationTheme.ForSlide(document, slide);
        var elements = new List<SlideElement>();
        var hasImage = slide.Layout is "cover" or "split" or "image_left";
        var gallery = slide.Layout == "photo_grid";
        var leftImage = slide.Layout == "image_left";
        var textX = leftImage ? 504 : 64;
        var textWidth = hasImage ? 392 : 832;
        var titleSize = slide.Layout is "cover" or "statement" ? 50 : 36;
        var titleY = slide.Layout is "cover" or "statement" ? 108 : 64;
        var titleHeight = slide.Layout == "cover" ? 206 : hasImage ? 152 : gallery ? 88 : 140;
        elements.Add(new(2, "line", 64, 38, 48, 3, "", 0, theme.Accent, Animate: false));
        elements.Add(Text(3, slide.Title, textX, titleY, textWidth, titleHeight, titleSize, slide.Style?.TitleColor ?? theme.Foreground, true));
        var bodyY = gallery ? 470 : slide.Layout == "cover" ? 348 : slide.Layout == "statement" ? 296 : hasImage ? 238 : 228;
        var bodyHeight = gallery ? 30 : slide.Layout == "cover" ? 100 : 74;
        for (var i = 0; i < slide.Body.Length; i++)
            elements.Add(Text(10 + i, slide.Body[i], textX, bodyY + i * (bodyHeight + 8), textWidth, bodyHeight, gallery ? 18 : 22, slide.Style?.BodyColor ?? theme.Foreground));
        if (hasImage || gallery)
            elements.AddRange(PresentationMedia.Frames(slide, gallery ? 64 : leftImage ? 64 : 504, gallery ? 168 : 64, gallery ? 832 : 392, gallery ? 284 : 418, theme.Background));
        var footer = document.Title;
        while (Measure(footer, 11, false) > 720) footer = footer[..^2] + "…";
        elements.Add(new(40, "text", 64, 510, 740, 18, footer, 11, theme.Muted, Animate: false));
        elements.Add(new(41, "text", 840, 510, 60, 18, $"{index + 1:00} / {document.Slides.Length:00}", 11, theme.Muted, Animate: false));
        foreach (var element in elements)
        {
            if (element.X < 0 || element.Y < 0 || element.X + element.Width > Width || element.Y + element.Height > Height)
                throw new InvalidDataException($"Slide {index + 1}: {element.Id} overflows the slide. Shorten the content or use another layout.");
        }
        for (var i = 0; i < elements.Count; i++)
            for (var j = i + 1; j < elements.Count; j++)
                if (elements[i].X < elements[j].X + elements[j].Width && elements[j].X < elements[i].X + elements[i].Width &&
                    elements[i].Y < elements[j].Y + elements[j].Height && elements[j].Y < elements[i].Y + elements[i].Height)
                    throw new InvalidDataException($"Slide {index + 1}: elements {elements[i].Id} and {elements[j].Id} overlap.");
        return elements;
    }

    private static SlideElement Text(int id, string text, double x, double y, double width, double height, double size, string color, bool bold = false)
    {
        var lines = Wrap(text, width - 6, size, bold);
        if (lines.Length * size * 1.22 > height - 2)
            throw new InvalidDataException($"Text does not fit its frame at {size.ToString(CultureInfo.InvariantCulture)}pt: '{text}'. Shorten only this field; do not shrink type.");
        return new(id, "text", x, y, width, height, string.Join('\n', lines), size, color, bold);
    }

    public static string[] Wrap(string text, double width, double size, bool bold)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Measure(word, size, bold) > width)
                    throw new InvalidDataException("A word is too long for its text frame. Use a shorter label.");
                if (line.Length > 0 && Measure(line + " " + word, size, bold) > width)
                {
                    lines.Add(line);
                    line = "";
                }
                line += (line.Length > 0 ? " " : "") + word;
            }
            lines.Add(line);
        }
        return lines.ToArray();
    }

    private static double Measure(string text, double size, bool bold) => text.Sum(c => c switch
    {
        ' ' or '.' or ',' or ':' or ';' or '!' or 'i' or 'l' or 'I' => .3,
        'W' or 'M' or 'Ж' or 'Ш' or 'Щ' or 'Ю' or 'ж' or 'ш' or 'щ' or 'ю' => .95,
        >= '\u2E80' => 1.05,
        _ when char.IsUpper(c) => .76,
        _ => .60
    }) * size * (bold ? 1.04 : 1);

    public static void Validate(PresentationDocument document)
    {
        if (document.Slides.Length is < 1 or > PresentationPolicy.MaximumSlides)
            throw new InvalidDataException("Invalid slide count or theme.");
        _ = PresentationTheme.Resolve(document.Theme);
        CheckText(document.Title, 100, "Presentation title");
        if (document.Slides.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != document.Slides.Length)
            throw new InvalidDataException("Slide IDs must be unique.");
        foreach (var slide in document.Slides)
        {
            CheckText(slide.Title, 100, "Slide title");
            if (slide.Body is null || slide.Body.Length > (slide.Layout is "cover" or "photo_grid" ? 1 : slide.Layout == "statement" ? 2 : 3))
                throw new InvalidDataException("Use at most 3 concise body paragraphs; cover/photo_grid allow 1 and statement allows 2.");
            foreach (var paragraph in slide.Body) CheckText(paragraph, 300, "Body paragraph");
            if (slide.Notes is null || slide.Notes.Length > 5000 || slide.ImagePrompt is null || slide.ImagePrompt.Length > 3000 ||
                !Layouts.Contains(slide.Layout, StringComparer.Ordinal) || !Animations.Contains(slide.Animation, StringComparer.Ordinal))
                throw new InvalidDataException("Invalid notes, image prompt, layout or animation.");
            var media = PresentationMedia.Images(slide);
            if (slide.Layout is "cover" or "split" or "image_left" or "photo_grid" && media.Count == 0)
                throw new InvalidDataException("Image layouts require at least one web image or generated illustration.");
            if (slide.Layout is "editorial" or "statement" && media.Count > 0)
                throw new InvalidDataException("An illustration requires a visible image frame. Include the necessary layout change in the requested image edit.");
            if (slide.Style is { } style && (new[] { style.Background, style.TitleColor, style.BodyColor }.Any(c => c is not null && !PresentationTheme.IsColor(c)) ||
                (style.Font is not null && !PresentationTheme.IsFont(style.Font)))) throw new InvalidDataException("Invalid slide colors or font.");
            if (slide.ImageFile is { } image && (Path.GetFileName(image) != image || !image.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Images must be presentation-owned PNG assets.");
            PresentationMedia.Validate(slide);
        }
        for (var i = 0; i < document.Slides.Length; i++) Compose(document, i);
    }

    private static void CheckText(string value, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new InvalidDataException($"{label} must contain 1–{max} printable characters.");
    }
}
