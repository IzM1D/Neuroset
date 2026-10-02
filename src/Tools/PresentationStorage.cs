using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

// Shared by AI authoring and the local editor. One gate and one atomic commit
// protocol prevent concurrent edits from overwriting each other.
internal static class PresentationStorage
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public static SemaphoreSlim GetGate(Guid conversationId) => Gates[(int)((uint)conversationId.GetHashCode() % Gates.Length)];

    public static PresentationArtifact CreateArtifact(PresentationDocument document, string path, string format, string[] changes) =>
        new("presentation", document.Id, document.Revision, document.Title, path, format, document.Slides.Length,
            PresentationMedia.Count(document), PresentationTheme.Resolve(document.Theme).Name, changes);

    public static async Task<PresentationArtifact> CommitAsync(PresentationPending pending, string directory, CancellationToken token)
    {
        var document = pending.Document;
        PresentationLayout.Validate(document);
        var path = await ExportAsync(document, directory, document.Format, token).ConfigureAwait(false);
        await SaveAsync(Path.Combine(directory, $"revision-{document.Revision:D4}", "document.json"), document, token).ConfigureAwait(false);
        await SaveAsync(Path.Combine(directory, "document.json"), document, token).ConfigureAwait(false);
        try { File.Delete(Path.Combine(directory, "pending.json")); }
        catch (IOException) { /* A stale draft is ignored after a successful commit. */ }
        return CreateArtifact(document, path, document.Format, pending.Changes);
    }

    public static async Task<string> ExportAsync(PresentationDocument document, string directory, string format, CancellationToken token)
    {
        format = PresentationPolicy.NormalizeFormat(format);
        var outputDirectory = Path.Combine(directory, $"revision-{document.Revision:D4}");
        Directory.CreateDirectory(outputDirectory);
        RejectReparsePoints(outputDirectory);
        var path = Path.Combine(outputDirectory, "presentation." + format);
        RejectReparsePoints(path);
        if (File.Exists(path)) return path;
        var temporary = Path.Combine(outputDirectory, Guid.NewGuid().ToString("N") + "." + format);
        try
        {
            await Task.Run(() =>
            {
                if (format == "pptx") PresentationPptxWriter.Write(document, directory, temporary, token);
                else PresentationOdpWriter.Write(document, directory, temporary, token);
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return default;
        RejectReparsePoints(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Presentation state exceeds 2 MB.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), DomainJson.Options);
    }

    public static Task SaveAsync<T>(string path, T value, CancellationToken token) => WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, DomainJson.Options), token);

    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        RejectReparsePoints(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Presentation storage must not traverse filesystem links.");
    }

}
