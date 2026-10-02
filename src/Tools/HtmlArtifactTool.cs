using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed record HtmlArtifactOptions(string RootDirectory)
{
    public static HtmlArtifactOptions CreateDefault() => new(
        Path.Combine(Neuroset.Application.ApplicationPaths.DataDirectory, "Artifacts"));
}

public sealed partial class HtmlArtifactTool(HtmlArtifactOptions options) : ITool
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; object-src 'none'; frame-src 'none'; worker-src blob:; base-uri 'none'; form-action 'none'";

    public ToolDescriptor Descriptor { get; } = new(
        "render_html",
        "Render model-authored HTML/CSS/JavaScript in Neuroset as an interactive local visualization. External network resources are blocked and a persistent copy is saved in Neuroset's local Artifacts directory.",
        JsonDocument.Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":160},"html":{"type":"string","minLength":1},"file_name":{"type":"string","maxLength":120}},"required":["title","html"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Write,
        ["artifacts.html.create"]);

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var title = arguments.GetProperty("title").GetString()!.Trim();
        var html = arguments.GetProperty("html").GetString()!;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(html))
            return new(false, string.Empty, "Title and HTML content cannot be blank.");
        if (Encoding.UTF8.GetByteCount(html) > MaximumBytes)
            return new(false, string.Empty, $"HTML exceeds the {MaximumBytes / 1024 / 1024} MB limit.");

        var requestedName = arguments.TryGetProperty("file_name", out var fileNameElement)
            ? fileNameElement.GetString()
            : null;
        var conversationDirectory = Path.Combine(options.RootDirectory, context.ConversationId.ToString("N"));
        Directory.CreateDirectory(conversationDirectory);
        var path = UniquePath(conversationDirectory, CreateFileName(requestedName, title));
        var document = PrepareDocument(html, title);
        await File.WriteAllTextAsync(path, document, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var uri = new Uri(path).AbsoluteUri;
        var output = JsonSerializer.Serialize(new
        {
            status = "created",
            title,
            path,
            uri,
            bytes = Encoding.UTF8.GetByteCount(document),
            network_access = "blocked by Content-Security-Policy",
            inline_preview = true,
            note = "The Neuroset interface embeds this artifact in the assistant message after the tool completes."
        }, DomainJson.Options);
        return new(
            true,
            output,
            null,
            new Dictionary<string, string>
            {
                ["artifact_path"] = path,
                ["artifact_uri"] = uri,
                ["content_type"] = "text/html",
                ["working_directory"] = conversationDirectory
            });
    }

    private static string PrepareDocument(string source, string title)
    {
        var cleaned = MetaRefreshRegex().Replace(BaseTagRegex().Replace(source, string.Empty), string.Empty);
        var encodedTitle = WebUtility.HtmlEncode(title);
        var securityHead = $"<meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"{ContentSecurityPolicy}\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{encodedTitle}</title>";

        var headMatch = HeadRegex().Match(cleaned);
        if (headMatch.Success) return cleaned.Insert(headMatch.Index + headMatch.Length, securityHead);
        var htmlMatch = HtmlRegex().Match(cleaned);
        if (htmlMatch.Success) return cleaned.Insert(htmlMatch.Index + htmlMatch.Length, $"<head>{securityHead}</head>");
        return $"<!doctype html><html lang=\"en\"><head>{securityHead}</head><body>{cleaned}</body></html>";
    }

    private static string CreateFileName(string? requestedName, string title)
    {
        var source = string.IsNullOrWhiteSpace(requestedName) ? Slug(title) : Path.GetFileName(requestedName.Trim());
        if (string.IsNullOrWhiteSpace(source)) source = "interactive-artifact";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(source.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim(' ', '.', '-');
        if (string.IsNullOrWhiteSpace(safe)) safe = "interactive-artifact";
        if (!safe.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) safe += ".html";
        return safe.Length <= 120 ? safe : safe[..115] + ".html";
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            path = Path.Combine(directory, $"{stem}-{suffix}.html");
            if (!File.Exists(path)) return path;
        }
        throw new IOException("Could not allocate a unique HTML artifact name.");
    }

    [GeneratedRegex("<meta\\b[^>]*http-equiv\\s*=\\s*(['\"]?)refresh\\1[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaRefreshRegex();

    [GeneratedRegex("<base\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BaseTagRegex();

    [GeneratedRegex("<head\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadRegex();

    [GeneratedRegex("<html\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlRegex();
}
