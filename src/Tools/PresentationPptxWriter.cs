using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Validation;

namespace Neuroset.Tools;

public static class PresentationPptxWriter
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private static readonly string[] FontKinds = ["majorFont", "minorFont"];

    public static void Write(PresentationDocument document, string directory, string path, CancellationToken token = default)
    {
        PresentationLayout.Validate(document);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var theme = PresentationTheme.Resolve(document.Theme);
            var types = new XElement(Ct + "Types",
                new XElement(Ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(Ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(Ct + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")),
                ContentType("ppt/presentation.xml", "presentationml.presentation.main"),
                ContentType("ppt/slideMasters/slideMaster1.xml", "presentationml.slideMaster"),
                ContentType("ppt/slideLayouts/slideLayout1.xml", "presentationml.slideLayout"),
                ContentType("ppt/theme/theme1.xml", "theme"));
            var presentation = new XElement(P + "presentation", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", "2147483648"), new XAttribute(R + "id", "rMaster"))),
                new XElement(P + "sldIdLst", document.Slides.Select((_, i) => new XElement(P + "sldId", new XAttribute("id", 256 + i), new XAttribute(R + "id", "rSlide" + (i + 1))))),
                new XElement(P + "sldSz", new XAttribute("cx", Emu(PresentationLayout.Width)), new XAttribute("cy", Emu(PresentationLayout.Height)), new XAttribute("type", "screen16x9")),
                new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000)));
            var presentationRels = Relationships(Relationship("rMaster", "slideMaster", "slideMasters/slideMaster1.xml"));
            Xml(zip, "_rels/.rels", Relationships(Relationship("rPresentation", "officeDocument", "ppt/presentation.xml")));
            Xml(zip, "ppt/presentation.xml", presentation);
            Xml(zip, "ppt/theme/theme1.xml", Theme(theme));
            Xml(zip, "ppt/slideMasters/slideMaster1.xml", new XElement(P + "sldMaster",
                new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(P + "cSld", Tree()), ColorMap(),
                new XElement(P + "sldLayoutIdLst", new XElement(P + "sldLayoutId", new XAttribute("id", "2147483649"), new XAttribute(R + "id", "rLayout")))));
            Xml(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", Relationships(
                Relationship("rLayout", "slideLayout", "../slideLayouts/slideLayout1.xml"), Relationship("rTheme", "theme", "../theme/theme1.xml")));
            Xml(zip, "ppt/slideLayouts/slideLayout1.xml", new XElement(P + "sldLayout", new XAttribute("type", "blank"), new XAttribute("preserve", 1),
                new XElement(P + "cSld", new XAttribute("name", "Neuroset wide"), Tree()), OverrideColorMap()));
            Xml(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", Relationships(Relationship("rMaster", "slideMaster", "../slideMasters/slideMaster1.xml")));

            var copiedImages = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < document.Slides.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var slide = document.Slides[i];
                var slideTheme = PresentationTheme.ForSlide(document, slide);
                var number = i + 1;
                var elements = PresentationLayout.Compose(document, i);
                var tree = Tree();
                foreach (var element in elements)
                    tree.Add(element.Kind == "image" ? Picture(element, directory) : Shape(element, slideTheme.Font));
                var root = new XElement(P + "sld", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R),
                    new XElement(P + "cSld", new XAttribute("name", slide.Id), new XElement(P + "bg", new XElement(P + "bgPr", Fill(slideTheme.Background), new XElement(A + "effectLst"))), tree), OverrideColorMap());
                if (slide.Animation != "none")
                {
                    root.Add(new XElement(P + "transition", new XAttribute("spd", "med"), new XAttribute("advClick", 1), new XElement(P + "fade")));
                    root.Add(Timing(elements.Where(e => e.Animate).OrderBy(e => e.Id is 3 or 30 ? 0 : 1).Select(e => e.Id).ToArray(), slide.Animation));
                }
                Xml(zip, $"ppt/slides/slide{number}.xml", root);
                var slideRels = Relationships(Relationship("rLayout", "slideLayout", "../slideLayouts/slideLayout1.xml"),
                    Relationship("rNotes", "notesSlide", $"../notesSlides/notesSlide{number}.xml"));
                foreach (var element in elements.Where(e => e.Kind == "image"))
                {
                    var image = element.ImageFile ?? throw new InvalidDataException("An illustration has not been generated.");
                    slideRels.Add(Relationship(ImageRelationship(element.Id), "image", "../media/" + image));
                    if (copiedImages.Add(image)) zip.CreateEntryFromFile(Path.Combine(directory, "assets", image), "ppt/media/" + image);
                }
                Xml(zip, $"ppt/slides/_rels/slide{number}.xml.rels", slideRels);
                var notes = PresentationMedia.Notes(slide);
                Xml(zip, $"ppt/notesSlides/notesSlide{number}.xml", Notes(notes, theme.Font));
                Xml(zip, $"ppt/notesSlides/_rels/notesSlide{number}.xml.rels", Relationships(Relationship("rSlide", "slide", $"../slides/slide{number}.xml")));
                types.Add(ContentType($"ppt/slides/slide{number}.xml", "presentationml.slide"), ContentType($"ppt/notesSlides/notesSlide{number}.xml", "presentationml.notesSlide"));
                presentationRels.Add(Relationship("rSlide" + number, "slide", $"slides/slide{number}.xml"));
            }
            Xml(zip, "ppt/_rels/presentation.xml.rels", presentationRels);
            Xml(zip, "[Content_Types].xml", types);
        }
        using var package = PresentationDocumentOpen(path);
        var errors = new OpenXmlValidator().Validate(package, token).Take(8).ToArray();
        if (errors.Length > 0) throw new InvalidDataException("PowerPoint validation failed: " + string.Join("; ", errors.Select(e => e.Description + " at " + e.Path?.XPath)));
    }

    private static DocumentFormat.OpenXml.Packaging.PresentationDocument PresentationDocumentOpen(string path) =>
        DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);

    private static XElement Shape(SlideElement e, string font)
    {
        var shape = new XElement(P + "sp", new XElement(P + "nvSpPr", new XElement(P + "cNvPr", new XAttribute("id", e.Id), new XAttribute("name", "element-" + e.Id)),
            new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
            new XElement(P + "spPr", Transform(e), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")),
                e.Kind == "line" ? Fill(e.Color) : new XElement(A + "noFill"), new XElement(A + "ln", new XElement(A + "noFill"))));
        if (e.Kind != "line") shape.Add(TextBody(e, font));
        return shape;
    }

    private static XElement TextBody(SlideElement e, string font) => new(P + "txBody",
        new XElement(A + "bodyPr", new XAttribute("wrap", "none"), new XAttribute("lIns", 0), new XAttribute("rIns", 0), new XAttribute("tIns", 0), new XAttribute("bIns", 0), new XAttribute("anchor", "t"), new XElement(A + "noAutofit")),
        new XElement(A + "lstStyle"),
        e.Text.Split('\n').Select(line => new XElement(A + "p",
            new XElement(A + "pPr", new XElement(A + "lnSpc", new XElement(A + "spcPts", new XAttribute("val", (int)Math.Round(e.FontSize * 122)))),
                new XElement(A + "spcBef", new XElement(A + "spcPts", new XAttribute("val", 0))), new XElement(A + "spcAft", new XElement(A + "spcPts", new XAttribute("val", 0)))),
            new XElement(A + "r", new XElement(A + "rPr", new XAttribute("sz", (int)(e.FontSize * 100)), new XAttribute("b", e.Bold ? 1 : 0), Fill(e.Color),
                new XElement(A + "latin", new XAttribute("typeface", font)), new XElement(A + "ea", new XAttribute("typeface", font)), new XElement(A + "cs", new XAttribute("typeface", font))), new XElement(A + "t", line)),
            new XElement(A + "endParaRPr", new XAttribute("sz", (int)(e.FontSize * 100))))));

    private static XElement Picture(SlideElement e, string directory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, "assets", e.ImageFile ?? throw new InvalidDataException("Missing image.")));
        var (width, height) = PngSize(bytes);
        var ratio = width / (double)height;
        var target = e.Width / e.Height;
        var crop = new XElement(A + "srcRect");
        if (e.ImageFit == "contain")
        {
            var scale = Math.Min(e.Width / width, e.Height / height);
            e = e with { X = e.X + (e.Width - width * scale) / 2, Y = e.Y + (e.Height - height * scale) / 2, Width = width * scale, Height = height * scale };
        }
        else if (ratio > target)
        {
            var percent = (int)Math.Round((1 - target / ratio) * 50000);
            crop.Add(new XAttribute("l", percent), new XAttribute("r", percent));
        }
        else
        {
            var percent = (int)Math.Round((1 - ratio / target) * 50000);
            crop.Add(new XAttribute("t", percent), new XAttribute("b", percent));
        }
        return new(P + "pic", new XElement(P + "nvPicPr", new XElement(P + "cNvPr", new XAttribute("id", e.Id), new XAttribute("name", "image-" + e.Id), new XAttribute("descr", e.Text)),
            new XElement(P + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1))), new XElement(P + "nvPr")),
            new XElement(P + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", ImageRelationship(e.Id))), crop, new XElement(A + "stretch", new XElement(A + "fillRect"))),
            new XElement(P + "spPr", Transform(e), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
    }

    private static string ImageRelationship(int id) => id == 30 ? "rImage" : "rImage" + id;

    public static (int Width, int Height) PngSize(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("An illustration is not a PNG.");
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 1 or > 32768 || height is < 1 or > 32768) throw new InvalidDataException("Invalid illustration dimensions.");
        return (width, height);
    }

    private static XElement Timing(int[] ids, string animation)
    {
        var nextId = 3;
        var effects = new XElement(P + "childTnLst");
        // Each click reveals one complete semantic element. No object moves across text.
        foreach (var id in ids)
        {
            var click = nextId++;
            var group = nextId++;
            var effect = nextId++;
            var behavior = nextId++;
            var animationNode = new XElement(P + "animEffect", new XAttribute("transition", "in"), new XAttribute("filter", animation == "reveal" ? "wipe(right)" : "fade"),
                new XElement(P + "cBhvr", new XElement(P + "cTn", new XAttribute("id", behavior), new XAttribute("dur", 450)),
                    new XElement(P + "tgtEl", new XElement(P + "spTgt", new XAttribute("spid", id)))));
            var effectNode = new XElement(P + "cTn", new XAttribute("id", effect),
                new XAttribute("presetID", animation == "reveal" ? 22 : 10), new XAttribute("presetClass", "entr"),
                new XAttribute("presetSubtype", animation == "reveal" ? 1 : 0), new XAttribute("fill", "hold"), new XAttribute("nodeType", id is 3 or 30 ? "withEffect" : "clickEffect"),
                new XElement(P + "stCondLst", new XElement(P + "cond", new XAttribute("delay", 0))), new XElement(P + "childTnLst", animationNode));
            var groupNode = new XElement(P + "cTn", new XAttribute("id", group), new XAttribute("fill", "hold"),
                new XElement(P + "stCondLst", new XElement(P + "cond", new XAttribute("delay", 0))), new XElement(P + "childTnLst", new XElement(P + "par", effectNode)));
            effects.Add(new XElement(P + "par", new XElement(P + "cTn", new XAttribute("id", click), new XAttribute("fill", "hold"),
                new XElement(P + "stCondLst", new XElement(P + "cond", new XAttribute("delay", id is 3 or 30 ? "0" : "indefinite"))), new XElement(P + "childTnLst", new XElement(P + "par", groupNode)))));
        }
        return new(P + "timing", new XElement(P + "tnLst", new XElement(P + "par", new XElement(P + "cTn", new XAttribute("id", 1), new XAttribute("dur", "indefinite"), new XAttribute("restart", "never"), new XAttribute("nodeType", "tmRoot"),
            new XElement(P + "childTnLst", new XElement(P + "seq", new XAttribute("concurrent", 1), new XAttribute("nextAc", "seek"),
                new XElement(P + "cTn", new XAttribute("id", 2), new XAttribute("dur", "indefinite"), new XAttribute("nodeType", "mainSeq"), effects),
                new XElement(P + "prevCondLst", new XElement(P + "cond", new XAttribute("evt", "onPrev"), new XAttribute("delay", 0), new XElement(P + "tgtEl", new XElement(P + "sldTgt")))),
                new XElement(P + "nextCondLst", new XElement(P + "cond", new XAttribute("evt", "onNext"), new XAttribute("delay", 0), new XElement(P + "tgtEl", new XElement(P + "sldTgt"))))))))));
    }

    private static XElement Notes(string text, string font)
    {
        var tree = Tree();
        var shape = Shape(new(2, "text", 40, 40, 640, 800, text, 12, "222222"), font);
        shape.Element(P + "nvSpPr")!.Element(P + "nvPr")!.Add(new XElement(P + "ph", new XAttribute("type", "body"), new XAttribute("idx", 1)));
        tree.Add(shape);
        return new(P + "notes", new XElement(P + "cSld", tree), OverrideColorMap());
    }

    private static XElement Tree() => new(P + "spTree", new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
        new XElement(P + "grpSpPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)),
            new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)))));
    private static XElement Transform(SlideElement e) => new(A + "xfrm", new XElement(A + "off", new XAttribute("x", Emu(e.X)), new XAttribute("y", Emu(e.Y))), new XElement(A + "ext", new XAttribute("cx", Emu(e.Width)), new XAttribute("cy", Emu(e.Height))));
    private static long Emu(double value) => (long)Math.Round(value * 12700);
    private static XElement Fill(string color) => new(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color)));
    private static XElement ColorMap() => new(P + "clrMap", new[] { ("bg1", "lt1"), ("tx1", "dk1"), ("bg2", "lt2"), ("tx2", "dk2"), ("accent1", "accent1"), ("accent2", "accent2"), ("accent3", "accent3"), ("accent4", "accent4"), ("accent5", "accent5"), ("accent6", "accent6"), ("hlink", "hlink"), ("folHlink", "folHlink") }.Select(x => new XAttribute(x.Item1, x.Item2)));
    private static XElement OverrideColorMap() => new(P + "clrMapOvr", new XElement(A + "masterClrMapping"));

    private static XElement Theme(PresentationTheme t) => new(A + "theme", new XAttribute("name", t.Name), new XElement(A + "themeElements",
        new XElement(A + "clrScheme", new XAttribute("name", t.Name),
            new[] { ("dk1", t.Foreground), ("lt1", t.Background), ("dk2", t.Muted), ("lt2", "FFFFFF"), ("accent1", t.Accent), ("accent2", t.Accent), ("accent3", t.Muted), ("accent4", t.Accent), ("accent5", t.Muted), ("accent6", t.Accent), ("hlink", t.Accent), ("folHlink", t.Muted) }.Select(x => new XElement(A + x.Item1, new XElement(A + "srgbClr", new XAttribute("val", x.Item2))))),
        new XElement(A + "fontScheme", new XAttribute("name", t.Name), FontKinds.Select(name => new XElement(A + name,
            new XElement(A + "latin", new XAttribute("typeface", t.Font)), new XElement(A + "ea", new XAttribute("typeface", t.Font)), new XElement(A + "cs", new XAttribute("typeface", t.Font))))),
        new XElement(A + "fmtScheme", new XAttribute("name", t.Name),
            new XElement(A + "fillStyleLst", Enumerable.Range(0, 3).Select(_ => Fill(t.Background))),
            new XElement(A + "lnStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "ln", new XAttribute("w", 12700), Fill(t.Accent), new XElement(A + "prstDash", new XAttribute("val", "solid"))))),
            new XElement(A + "effectStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "effectStyle", new XElement(A + "effectLst")))),
            new XElement(A + "bgFillStyleLst", Enumerable.Range(0, 3).Select(_ => Fill(t.Background))))));

    private static XElement ContentType(string path, string type) => new(Ct + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument." + type + "+xml"));
    private static XElement Relationship(string id, string type, string target) => new(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", RelationshipBase + type), new XAttribute("Target", target));
    private static XElement Relationships(params XElement[] entries) => new(Rel + "Relationships", entries);
    internal static void Xml(ZipArchive zip, string name, XElement root)
    {
        using var stream = zip.CreateEntry(name).Open();
        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(stream, SaveOptions.DisableFormatting);
    }
}
