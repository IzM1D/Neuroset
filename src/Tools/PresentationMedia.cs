using System.Text;

namespace Neuroset.Tools;

public static class PresentationMedia
{
    public static IReadOnlyList<PresentationImage> Images(PresentationSlide slide) => slide.Images ??
        (string.IsNullOrWhiteSpace(slide.ImagePrompt) ? [] : [new("generated", slide.ImagePrompt, File: slide.ImageFile, Fit: "cover")]);

    public static int Count(PresentationDocument document) => document.Slides.Sum(slide => Images(slide).Count(image => image.File is not null));

    public static void Validate(PresentationSlide slide)
    {
        var images = Images(slide);
        if (images.Count > 4) throw new InvalidDataException("Use at most four images per slide.");
        foreach (var image in images)
        {
            if (image.Source is not ("generated" or "web") || string.IsNullOrWhiteSpace(image.Query) || image.Query.Length > 3000 || image.Fit is not ("contain" or "cover"))
                throw new InvalidDataException("Images require source=web/generated, a specific subject query, and fit=contain/cover.");
            if (image.File is { } file && (Path.GetFileName(file) != file || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Images must be presentation-owned PNG assets.");
            if (image.Url is { Length: > 0 } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
                throw new InvalidDataException("Web images require public HTTP(S) URLs.");
        }
    }

    public static IEnumerable<SlideElement> Frames(PresentationSlide slide, double x, double y, double width, double height, string background)
    {
        var images = Images(slide);
        var columns = images.Count <= 1 ? 1 : images.Count == 3 && width > height * 2 ? 3 : 2;
        var rows = (int)Math.Ceiling(images.Count / (double)columns);
        var cellWidth = (width - (columns - 1) * 12) / columns;
        var cellHeight = (height - (rows - 1) * 12) / rows;
        for (var i = 0; i < images.Count; i++)
            yield return new(30 + i, "image", x + i % columns * (cellWidth + 12), y + i / columns * (cellHeight + 12),
                cellWidth, cellHeight, images[i].Query, 0, background, ImageFile: images[i].File, ImageFit: images[i].Fit);
    }

    public static string Notes(PresentationSlide slide)
    {
        var notes = new StringBuilder(slide.Notes);
        foreach (var image in Images(slide).Where(image => image.File is not null))
        {
            notes.Append("\n[Sources]\n");
            if (image.Source == "generated") notes.Append("AI-generated illustration; prompt: ").Append(image.Query);
            else notes.Append("Web image: ").Append(image.Query).Append("\nSource: ").Append(image.SourcePage ?? image.Url)
                .Append("\nImage: ").Append(image.Url).Append("\nAttribution: ").Append(image.Attribution);
            notes.Append("\n[/Sources]");
        }
        return notes.ToString();
    }
}
