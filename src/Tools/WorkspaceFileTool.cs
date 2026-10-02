using System.Text;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class WorkspaceFileTool : ITool
{
    private const int MaxReadCharacters = 1_000_000;
    public ToolDescriptor Descriptor { get; } = new(
        "workspace_files",
        "List, read, write, append, move, create directories, or delete files. In full-access coding chats paths may address the complete user filesystem; normal chats are restricted to their temporary workspace.",
        JsonDocument.Parse("""{"type":"object","properties":{"action":{"type":"string","enum":["list","read","write","append","move","mkdir","delete","exists"]},"path":{"type":"string","minLength":1},"destination":{"type":"string"},"content":{"type":"string"},"recursive":{"type":"boolean"}},"required":["action","path"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Write,
        ["files.workspace"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        var action = arguments.GetProperty("action").GetString()!;
        var path = Resolve(arguments.GetProperty("path").GetString()!, context);
        switch (action)
        {
            case "list":
            {
                if (!Directory.Exists(path)) return new(false, string.Empty, "Directory does not exist.");
                var recursive = arguments.TryGetProperty("recursive", out var recursiveElement) && recursiveElement.GetBoolean();
                var entries = Directory.EnumerateFileSystemEntries(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Take(5000).Select(item => new { path = item, type = Directory.Exists(item) ? "directory" : "file" }).ToArray();
                return Success(entries, context);
            }
            case "read":
            {
                if (!File.Exists(path)) return new(false, string.Empty, "File does not exist.");
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (text.Length > MaxReadCharacters) text = text[..MaxReadCharacters] + "\n[truncated]";
                return new(true, text, null, Metadata(context));
            }
            case "write":
            case "append":
            {
                var content = arguments.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? context.WorkingDirectory);
                if (action == "write") await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                else await File.AppendAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                return Success(new { status = action == "write" ? "written" : "appended", path }, context);
            }
            case "move":
            {
                if (!arguments.TryGetProperty("destination", out var destinationElement) || string.IsNullOrWhiteSpace(destinationElement.GetString())) return new(false, string.Empty, "destination is required.");
                var destination = Resolve(destinationElement.GetString()!, context);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? context.WorkingDirectory);
                if (File.Exists(path)) File.Move(path, destination, false);
                else if (Directory.Exists(path)) Directory.Move(path, destination);
                else return new(false, string.Empty, "Source path does not exist.");
                return Success(new { status = "moved", path, destination }, context);
            }
            case "mkdir":
                Directory.CreateDirectory(path);
                return Success(new { status = "created", path }, context);
            case "delete":
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, arguments.TryGetProperty("recursive", out var recursiveElement) && recursiveElement.GetBoolean());
                else return new(false, string.Empty, "Path does not exist.");
                return Success(new { status = "deleted", path }, context);
            case "exists":
                return Success(new { path, exists = File.Exists(path) || Directory.Exists(path), type = Directory.Exists(path) ? "directory" : File.Exists(path) ? "file" : null }, context);
            default:
                return new(false, string.Empty, "Unsupported file action.");
        }
    }

    private static string Resolve(string rawPath, ToolExecutionContext context)
    {
        var combined = Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(context.WorkingDirectory, rawPath);
        var fullPath = Path.GetFullPath(combined);
        if (context.FullAccess) return fullPath;
        var root = Path.GetFullPath(context.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !fullPath.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The path is outside the chat workspace.");
        return fullPath;
    }

    private static ToolExecutionResult Success<T>(T value, ToolExecutionContext context) => new(true, JsonSerializer.Serialize(value, DomainJson.Options), null, Metadata(context));
    private static Dictionary<string, string> Metadata(ToolExecutionContext context) => new() { ["access"] = context.FullAccess ? "full" : "workspace", ["working_directory"] = context.WorkingDirectory };
}
