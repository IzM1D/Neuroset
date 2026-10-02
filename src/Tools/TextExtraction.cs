using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Neuroset.Application;
using UglyToad.PdfPig;

namespace Neuroset.Tools;

public sealed class CompositeTextExtractor(IEnumerable<ITextExtractor> extractors) : ITextExtractor
{
    private readonly IReadOnlyList<ITextExtractor> _extractors = extractors.Where(x => x is not CompositeTextExtractor).ToArray();
    public bool CanExtract(string extension) => _extractors.Any(x => x.CanExtract(extension));
    public Task<string> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        var extractor = _extractors.FirstOrDefault(x => x.CanExtract(extension)) ?? throw new NotSupportedException($"Unsupported file type: {extension}");
        return extractor.ExtractAsync(path, cancellationToken);
    }
}

public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv", ".json" };
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    public bool CanExtract(string extension) => Extensions.Contains(extension);
    public async Task<string> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidateFile(path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        return text;
    }
    internal static void ValidateFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected knowledge file is unavailable.", fullPath);
        if (new FileInfo(fullPath).Length > 100L * 1024 * 1024) throw new InvalidOperationException("Files larger than 100 MB must be split before indexing.");
    }
}

public sealed class DocxTextExtractor : ITextExtractor
{
    public bool CanExtract(string extension) => extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);
    public Task<string> ExtractAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        PlainTextExtractor.ValidateFile(path); cancellationToken.ThrowIfCancellationRequested();
        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart?.Document?.Body?.Elements<Paragraph>() ?? [];
        return string.Join(Environment.NewLine, paragraphs.Select(x => x.InnerText).Where(x => !string.IsNullOrWhiteSpace(x)));
    }, cancellationToken);
}

public sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanExtract(string extension) => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public Task<string> ExtractAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        PlainTextExtractor.ValidateFile(path); var builder = new StringBuilder();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages()) { cancellationToken.ThrowIfCancellationRequested(); builder.AppendLine(page.Text); builder.AppendLine(); }
        return builder.ToString();
    }, cancellationToken);
}

public static class TextChunker
{
    public static IReadOnlyList<string> Chunk(string text, int targetCharacters = 1200, int overlapCharacters = 180)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        targetCharacters = Math.Max(300, targetCharacters); overlapCharacters = Math.Clamp(overlapCharacters, 0, targetCharacters / 2);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new List<string>(); var start = 0;
        while (start < normalized.Length)
        {
            var end = Math.Min(normalized.Length, start + targetCharacters);
            if (end < normalized.Length)
            {
                var paragraph = normalized.LastIndexOf("\n\n", end, Math.Min(targetCharacters, end - start), StringComparison.Ordinal);
                var sentence = normalized.LastIndexOfAny(['.', '!', '?', '\n'], end - 1, Math.Min(targetCharacters, end - start));
                var boundary = Math.Max(paragraph, sentence); if (boundary > start + targetCharacters / 2) end = boundary + 1;
            }
            var chunk = normalized[start..end].Trim(); if (chunk.Length > 0) result.Add(chunk);
            if (end >= normalized.Length) break;
            start = Math.Max(start + 1, end - overlapCharacters);
        }
        return result;
    }
}
