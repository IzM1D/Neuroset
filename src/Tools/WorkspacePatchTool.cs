using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class WorkspacePatchTool : ITool
{
    private const int MaximumChanges = 64;
    private const int MaximumFileCharacters = 4_000_000;

    public ToolDescriptor Descriptor { get; } = new(
        "workspace_patch",
        "Apply precise atomic text replacements or create new text files inside the selected coding workspace. Every replacement verifies the expected old text before any file is changed, preventing stale or partial edits.",
        JsonDocument.Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":160},"changes":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"object","properties":{"path":{"type":"string","minLength":1},"operation":{"type":"string","enum":["replace","create"]},"old_text":{"type":"string"},"new_text":{"type":"string"},"content":{"type":"string"},"expected_occurrences":{"type":"integer","minimum":1,"maximum":10000},"expected_sha256":{"type":"string","pattern":"^[A-Fa-f0-9]{64}$"}},"required":["path","operation"],"additionalProperties":false}}},"required":["changes"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Write,
        ["files.workspace"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess)
            return new(false, string.Empty, "Atomic workspace patches are available only in explicitly authorized coding chats.");
        var root = Path.GetFullPath(context.WorkingDirectory);
        if (!Directory.Exists(root)) return new(false, string.Empty, $"Coding workspace does not exist: {root}");
        var changeElements = arguments.GetProperty("changes").EnumerateArray().Take(MaximumChanges).ToArray();
        var pending = new List<PendingChange>(changeElements.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changeElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawPath = change.GetProperty("path").GetString()!;
            var path = ResolveWithinWorkspace(rawPath, root);
            if (!seen.Add(path)) return Failure($"A patch may change each path only once: {rawPath}");
            var operation = change.GetProperty("operation").GetString()!;
            var exists = File.Exists(path);
            var original = exists ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : string.Empty;
            if (original.Length > MaximumFileCharacters)
                return Failure($"File is too large for an atomic text patch ({original.Length} characters): {rawPath}");
            if (change.TryGetProperty("expected_sha256", out var hashElement))
            {
                var expectedHash = hashElement.GetString()!;
                var actualHash = Sha256(original);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    return Failure($"File changed since it was inspected. SHA-256 mismatch for {rawPath}. Expected {expectedHash}, actual {actualHash}.");
            }

            switch (operation)
            {
                case "create":
                {
                    if (exists) return Failure($"Create refused because the file already exists: {rawPath}");
                    var content = change.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;
                    if (content.Length > MaximumFileCharacters) return Failure($"New file is too large for an atomic text patch: {rawPath}");
                    pending.Add(new(path, rawPath, operation, false, string.Empty, content, 0));
                    break;
                }
                case "replace":
                {
                    if (!exists) return Failure($"Replace refused because the file does not exist: {rawPath}");
                    if (!change.TryGetProperty("old_text", out var oldElement)) return Failure($"old_text is required for replace: {rawPath}");
                    var oldText = oldElement.GetString() ?? string.Empty;
                    if (oldText.Length == 0) return Failure($"old_text cannot be empty: {rawPath}");
                    var newText = change.TryGetProperty("new_text", out var newElement) ? newElement.GetString() ?? string.Empty : string.Empty;
                    var expectedOccurrences = change.TryGetProperty("expected_occurrences", out var occurrenceElement) ? occurrenceElement.GetInt32() : 1;
                    var occurrences = CountOccurrences(original, oldText);
                    if (occurrences != expectedOccurrences)
                        return Failure($"Exact replacement check failed for {rawPath}: expected {expectedOccurrences} occurrence(s), found {occurrences}. No files were changed.");
                    var updated = original.Replace(oldText, newText, StringComparison.Ordinal);
                    pending.Add(new(path, rawPath, operation, true, original, updated, occurrences));
                    break;
                }
                default:
                    return Failure($"Unsupported patch operation: {operation}");
            }
        }

        var temporaryFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var committed = new List<PendingChange>();
        try
        {
            foreach (var change in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(change.Path) ?? root;
                Directory.CreateDirectory(directory);
                var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(change.Path)}.{Guid.NewGuid():N}.neuroset.tmp");
                await File.WriteAllTextAsync(temporaryPath, change.Updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                temporaryFiles[change.Path] = temporaryPath;
            }
            foreach (var change in pending)
            {
                File.Move(temporaryFiles[change.Path], change.Path, true);
                committed.Add(change);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var change in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Existed) File.WriteAllText(change.Path, change.Original, new UTF8Encoding(false));
                    else if (File.Exists(change.Path)) File.Delete(change.Path);
                }
                catch (Exception) { }
            }
            return Failure($"Atomic patch failed and committed changes were rolled back: {exception.Message}");
        }
        finally
        {
            foreach (var temporaryPath in temporaryFiles.Values)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception) { }
            }
        }

        var title = arguments.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var report = new
        {
            title = string.IsNullOrWhiteSpace(title) ? "Atomic workspace patch" : title,
            workspace = root,
            changedFileCount = pending.Count,
            replacementCount = pending.Sum(change => change.Occurrences),
            files = pending.Select(change => new
            {
                path = Path.GetRelativePath(root, change.Path).Replace('\\', '/'),
                operation = change.Operation,
                replacements = change.Occurrences,
                beforeSha256 = change.Existed ? Sha256(change.Original) : null,
                afterSha256 = Sha256(change.Updated),
                beforeCharacters = change.Original.Length,
                afterCharacters = change.Updated.Length,
                preview = BuildPreview(change.Original, change.Updated)
            }).ToArray()
        };
        return new(true, JsonSerializer.Serialize(report, DomainJson.Options), Metadata: new Dictionary<string, string>
        {
            ["working_directory"] = root,
            ["changed_file_count"] = pending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["replacement_count"] = pending.Sum(change => change.Occurrences).ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private static string ResolveWithinWorkspace(string rawPath, string root)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(root, rawPath));
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Atomic patches cannot address paths outside the selected coding workspace.");
        return path;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string BuildPreview(string before, string after)
    {
        const int maximum = 1_200;
        var prefix = 0;
        var shared = Math.Min(before.Length, after.Length);
        while (prefix < shared && before[prefix] == after[prefix]) prefix++;
        var beforeStart = Math.Max(0, prefix - 180);
        var afterStart = Math.Max(0, prefix - 180);
        var beforePart = before.Substring(beforeStart, Math.Min(maximum / 2, before.Length - beforeStart));
        var afterPart = after.Substring(afterStart, Math.Min(maximum / 2, after.Length - afterStart));
        return $"--- before\n{beforePart}\n+++ after\n{afterPart}";
    }

    private static ToolExecutionResult Failure(string message) => new(false, string.Empty, message);

    private sealed record PendingChange(
        string Path,
        string RequestedPath,
        string Operation,
        bool Existed,
        string Original,
        string Updated,
        int Occurrences);
}
