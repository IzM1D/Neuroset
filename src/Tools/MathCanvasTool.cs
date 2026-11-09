using System.Net;
using System.Text;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

/// <summary>Creates a dedicated, typeset mathematics card backed by MathJax.</summary>
public sealed class MathCanvasTool(HtmlArtifactOptions options) : ITool
{
    private const int MaximumLatexCharacters = 120_000;
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'";

    public ToolDescriptor Descriptor { get; } = new(
        "math_canvas",
        "Render a standalone LaTeX formula in a beautiful, typeset mathematics canvas. Use for important equations, optimization objectives, derivations, aligned systems, matrices, fractions, and proofs. Pass raw LaTeX in latex; do not include Markdown or HTML.",
        JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "title":{"type":"string","minLength":1,"maxLength":160},
            "latex":{"type":"string","minLength":1,"maxLength":120000},
            "description":{"type":"string","maxLength":500}
          },
          "required":["title","latex"],
          "additionalProperties":false
        }
        """).RootElement.Clone(),
        ToolRisk.Write,
        ["artifacts.math.create"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        var title = arguments.GetProperty("title").GetString()?.Trim() ?? string.Empty;
        var latex = arguments.GetProperty("latex").GetString()?.Trim() ?? string.Empty;
        var description = arguments.TryGetProperty("description", out var descriptionElement)
            ? descriptionElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(latex))
            return new(false, string.Empty, "Title and LaTeX content cannot be blank.");
        if (latex.Length > MaximumLatexCharacters)
            return new(false, string.Empty, $"LaTeX exceeds the {MaximumLatexCharacters:N0} character limit.");

        var directory = Path.Combine(options.RootDirectory, context.ConversationId.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = UniquePath(directory, Slug(title) + ".html");
        var document = CreateDocument(title, latex, description);
        await File.WriteAllTextAsync(path, document, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        return new(
            true,
            JsonSerializer.Serialize(new
            {
                kind = "math_canvas",
                status = "created",
                title,
                path,
                uri = new Uri(path).AbsoluteUri,
                renderer = "MathJax",
                network_access = "only the MathJax script from jsdelivr is allowed by the document policy"
            }, DomainJson.Options),
            null,
            new Dictionary<string, string>
            {
                ["artifact_path"] = path,
                ["artifact_uri"] = new Uri(path).AbsoluteUri,
                ["content_type"] = "text/html",
                ["math_renderer"] = "MathJax"
            });
    }

    private static string CreateDocument(string title, string latex, string description)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedDescription = WebUtility.HtmlEncode(description);
        var encodedLatex = WebUtility.HtmlEncode(UnwrapDelimiters(latex));
        var descriptionMarkup = string.IsNullOrWhiteSpace(description) ? string.Empty : $"<p class=\"description\">{encodedDescription}</p>";
        const string template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="__CSP__">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>__TITLE__</title>
<style>
:root {{ color-scheme: dark; --ink:#f4f7fb; --muted:#aeb8c8; --accent:#8ab4ff; --line:rgba(255,255,255,.12); }}
* {{ box-sizing:border-box; }}
body {{ margin:0; min-height:100vh; display:grid; place-items:center; padding:28px; background:radial-gradient(circle at 20% 0%,#243552 0,#111827 45%,#0b1020 100%); color:var(--ink); font-family:Inter,'Segoe UI',sans-serif; }}
main {{ width:min(100%,900px); padding:34px 38px 30px; border:1px solid var(--line); border-radius:24px; background:rgba(20,28,44,.84); box-shadow:0 22px 70px rgba(0,0,0,.32); }}
.eyebrow {{ color:var(--accent); text-transform:uppercase; letter-spacing:.13em; font-size:11px; font-weight:700; margin-bottom:12px; }}
h1 {{ margin:0 0 24px; font-size:clamp(20px,3vw,30px); line-height:1.2; font-weight:650; }}
.formula {{ overflow-x:auto; padding:34px 14px; border-top:1px solid var(--line); border-bottom:1px solid var(--line); text-align:center; font-size:clamp(24px,4vw,40px); }}
.description {{ margin:22px 0 0; color:var(--muted); font-size:14px; line-height:1.6; white-space:pre-wrap; }}
.fallback {{ display:none; white-space:pre-wrap; font-family:'Cambria Math','Times New Roman',serif; font-size:1.15em; }}
</style>
<script>window.MathJax={{tex:{{inlineMath:[['\\(','\\)']],displayMath:[['\\[','\\]']],packages:{{'[+]':['ams']}}}},svg:{{fontCache:'global'}},options:{{skipHtmlTags:['script','noscript','style','textarea','pre','code']}}}};</script>
<script defer src="https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-svg.js"></script>
</head>
<body><main><div class="eyebrow">Mathematics</div><h1>__TITLE__</h1><div class="formula" id="formula">\[__LATEX__\]<span class="fallback">__LATEX__</span></div>__DESCRIPTION__</main>
<script>window.setTimeout(function(){{if(!window.MathJax)document.querySelector('.fallback').style.display='inline';}},3500);</script>
</body></html>
""";
        return template.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal)
            .Replace("__CSP__", ContentSecurityPolicy, StringComparison.Ordinal)
            .Replace("__TITLE__", encodedTitle, StringComparison.Ordinal)
            .Replace("__LATEX__", encodedLatex, StringComparison.Ordinal)
            .Replace("__DESCRIPTION__", descriptionMarkup, StringComparison.Ordinal);
    }

    private static string UnwrapDelimiters(string value)
    {
        var result = value.Trim();
        if (result.StartsWith("$$", StringComparison.Ordinal) && result.EndsWith("$$", StringComparison.Ordinal)) return result[2..^2].Trim();
        if (result.StartsWith("\\[", StringComparison.Ordinal) && result.EndsWith("\\]", StringComparison.Ordinal)) return result[2..^2].Trim();
        return result;
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.Length == 0 ? "math-canvas" : builder.ToString().Trim('-');
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
        throw new IOException("Could not allocate a unique math canvas name.");
    }
}
