using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Neuroset.Tools;

// Native ODF 1.3 presentation package. No extension-renaming or lossy Office conversion.
public static class PresentationOdpWriter
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private static readonly XNamespace Anim = "urn:oasis:names:tc:opendocument:xmlns:animation:1.0";
    private static readonly XNamespace Smil = "urn:oasis:names:tc:opendocument:xmlns:smil-compatible:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    public const string MediaType = "application/vnd.oasis.opendocument.presentation";

    public static void Write(PresentationDocument document, string directory, string path, CancellationToken token = default)
    {
        PresentationLayout.Validate(document);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        // ODF requires the first entry, uncompressed, without a byte-order mark.
        using (var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false))) writer.Write(MediaType);
        var auto = new XElement(Office + "automatic-styles");
        var body = new XElement(Office + "presentation");
        var manifest = new XElement(Manifest + "manifest", new XAttribute(Manifest + "version", "1.3"), Entry("/", MediaType), Entry("content.xml", "text/xml"), Entry("styles.xml", "text/xml"));
        auto.Add(new XElement(Style + "style", new XAttribute(Style + "name", "frame"), new XAttribute(Style + "family", "graphic"),
            new XElement(Style + "graphic-properties", new XAttribute(Draw + "stroke", "none"), new XAttribute(Draw + "fill", "none"),
                new XAttribute(Fo + "padding", "0pt"), new XAttribute(Draw + "auto-grow-height", "false"), new XAttribute(Draw + "auto-grow-width", "false"))));
        var copied = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < document.Slides.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var slide = document.Slides[i];
            var theme = PresentationTheme.ForSlide(document, slide);
            var pageProperties = new XElement(Style + "drawing-page-properties", new XAttribute(Draw + "fill", "solid"), new XAttribute(Draw + "fill-color", "#" + theme.Background),
                new XAttribute(Presentation + "transition-type", "manual"));
            if (slide.Animation != "none") pageProperties.Add(new XAttribute(Smil + "type", "fade"), new XAttribute(Smil + "subtype", "crossfade"), new XAttribute(Presentation + "transition-speed", "medium"));
            auto.Add(new XElement(Style + "style", new XAttribute(Style + "name", "page" + i), new XAttribute(Style + "family", "drawing-page"), pageProperties));
            var page = new XElement(Draw + "page", new XAttribute(Draw + "name", slide.Id), new XAttribute(Draw + "style-name", "page" + i), new XAttribute(Draw + "master-page-name", "Wide"));
            var elements = PresentationLayout.Compose(document, i);
            foreach (var element in elements)
            {
                var id = $"s{i}e{element.Id}";
                if (element.Kind == "line")
                {
                    auto.Add(new XElement(Style + "style", new XAttribute(Style + "name", id), new XAttribute(Style + "family", "graphic"),
                        new XElement(Style + "graphic-properties", new XAttribute(Draw + "stroke", "none"), new XAttribute(Draw + "fill", "solid"), new XAttribute(Draw + "fill-color", "#" + element.Color))));
                    page.Add(new XElement(Draw + "rect", new XAttribute(Draw + "style-name", id), Bounds(element)));
                    continue;
                }
                var frame = new XElement(Draw + "frame", new XAttribute(XNamespace.Xml + "id", id), new XAttribute(Draw + "name", id), new XAttribute(Draw + "style-name", "frame"));
                if (element.Kind == "image")
                {
                    var image = element.ImageFile ?? throw new InvalidDataException("An illustration has not been generated.");
                    var source = Path.Combine(directory, "assets", image);
                    var size = PresentationPptxWriter.PngSize(File.ReadAllBytes(source));
                    var scale = Math.Min(element.Width / size.Width, element.Height / size.Height);
                    frame.Add(Bounds(element with { X = element.X + (element.Width - size.Width * scale) / 2, Y = element.Y + (element.Height - size.Height * scale) / 2, Width = size.Width * scale, Height = size.Height * scale }));
                    frame.Add(new XElement(Draw + "image", new XAttribute(Xlink + "href", "Pictures/" + image), new XAttribute(Xlink + "type", "simple"), new XAttribute(Xlink + "show", "embed"), new XAttribute(Xlink + "actuate", "onLoad")));
                    if (copied.Add(image))
                    {
                        zip.CreateEntryFromFile(source, "Pictures/" + image);
                        manifest.Add(Entry("Pictures/" + image, "image/png"));
                    }
                }
                else
                {
                    frame.Add(Bounds(element));
                    auto.Add(new XElement(Style + "style", new XAttribute(Style + "name", id), new XAttribute(Style + "family", "paragraph"),
                        new XElement(Style + "paragraph-properties", new XAttribute(Fo + "line-height", Pt(element.FontSize * 1.22)), new XAttribute(Fo + "margin-top", "0pt"), new XAttribute(Fo + "margin-bottom", "0pt")),
                        new XElement(Style + "text-properties", new XAttribute(Fo + "font-family", theme.Font), new XAttribute(Fo + "font-size", Pt(element.FontSize)),
                            new XAttribute(Fo + "font-weight", element.Bold ? "bold" : "normal"), new XAttribute(Fo + "color", "#" + element.Color))));
                    frame.Add(new XElement(Draw + "text-box", element.Text.Split('\n').Select(line => new XElement(Text + "p", new XAttribute(Text + "style-name", id), line))));
                }
                page.Add(frame);
            }
            if (slide.Animation != "none")
            {
                var sequence = new XElement(Anim + "seq", new XAttribute(Presentation + "node-type", "main-sequence"));
                foreach (var element in elements.Where(e => e.Animate).OrderBy(e => e.Id is 3 or 30 ? 0 : 1))
                {
                    var target = $"s{i}e{element.Id}";
                    sequence.Add(new XElement(Anim + "par", new XAttribute(Smil + "begin", element.Id is 3 or 30 ? "0s" : "next"), new XAttribute(Smil + "fill", "hold"),
                        new XElement(Anim + "par", new XAttribute(Smil + "begin", "0s"), new XAttribute(Smil + "fill", "hold"),
                            new XElement(Anim + "par", new XAttribute(Smil + "begin", "0s"), new XAttribute(Smil + "fill", "hold"), new XAttribute(Presentation + "node-type", element.Id is 3 or 30 ? "with-previous" : "on-click"),
                                new XElement(Anim + "set", new XAttribute(Smil + "targetElement", target), new XAttribute(Smil + "attributeName", "visibility"), new XAttribute(Smil + "to", "visible"), new XAttribute(Smil + "dur", "0.001s"), new XAttribute(Smil + "fill", "hold")),
                                new XElement(Anim + "transitionFilter", new XAttribute(Smil + "targetElement", target), new XAttribute(Smil + "dur", "0.45s"), new XAttribute(Smil + "type", slide.Animation == "reveal" ? "barWipe" : "fade"),
                                    new XAttribute(Smil + "subtype", slide.Animation == "reveal" ? "leftToRight" : "crossfade"), new XAttribute(Smil + "mode", "in"))))));
                }
                page.Add(new XElement(Anim + "par", new XAttribute(Presentation + "node-type", "timing-root"), sequence));
            }
            var notes = PresentationMedia.Notes(slide);
            page.Add(new XElement(Presentation + "notes", new XElement(Draw + "frame", new XAttribute(Draw + "style-name", "frame"),
                new XAttribute(Presentation + "class", "notes"), new XAttribute(Svg + "x", "36pt"), new XAttribute(Svg + "y", "36pt"), new XAttribute(Svg + "width", "600pt"), new XAttribute(Svg + "height", "700pt"),
                new XElement(Draw + "text-box", notes.Split('\n').Select(line => new XElement(Text + "p", line))))));
            body.Add(page);
        }
        var content = Root("document-content", auto, new XElement(Office + "body", body));
        PresentationPptxWriter.Xml(zip, "content.xml", content);
        PresentationPptxWriter.Xml(zip, "styles.xml", Root("document-styles", new XElement(Office + "styles"),
            new XElement(Office + "automatic-styles", new XElement(Style + "page-layout", new XAttribute(Style + "name", "WideLayout"),
                new XElement(Style + "page-layout-properties", new XAttribute(Fo + "page-width", Pt(PresentationLayout.Width)), new XAttribute(Fo + "page-height", Pt(PresentationLayout.Height)), new XAttribute(Style + "print-orientation", "landscape")))),
            new XElement(Office + "master-styles", new XElement(Style + "master-page", new XAttribute(Style + "name", "Wide"), new XAttribute(Style + "page-layout-name", "WideLayout")))));
        PresentationPptxWriter.Xml(zip, "META-INF/manifest.xml", manifest);
    }

    private static XElement Root(string name, params XElement[] content) => new(Office + name, new XAttribute(Office + "version", "1.3"),
        new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "style", Style), new XAttribute(XNamespace.Xmlns + "text", Text),
        new XAttribute(XNamespace.Xmlns + "draw", Draw), new XAttribute(XNamespace.Xmlns + "fo", Fo), new XAttribute(XNamespace.Xmlns + "svg", Svg),
        new XAttribute(XNamespace.Xmlns + "xlink", Xlink), new XAttribute(XNamespace.Xmlns + "presentation", Presentation), new XAttribute(XNamespace.Xmlns + "anim", Anim), new XAttribute(XNamespace.Xmlns + "smil", Smil), content);
    private static XAttribute[] Bounds(SlideElement e) => [new(Svg + "x", Pt(e.X)), new(Svg + "y", Pt(e.Y)), new(Svg + "width", Pt(e.Width)), new(Svg + "height", Pt(e.Height))];
    private static string Pt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
    private static XElement Entry(string path, string type) => new(Manifest + "file-entry", new XAttribute(Manifest + "full-path", path), new XAttribute(Manifest + "media-type", type));
}
