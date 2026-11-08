using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class RichTableTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "title":{"type":"string","minLength":1,"maxLength":160},
            "columns":{"type":"array","minItems":1,"maxItems":12,"items":{"type":"string","minLength":1,"maxLength":120}},
            "rows":{"type":"array","minItems":1,"maxItems":200,"items":{"type":"array","maxItems":12,"items":{"type":"string","maxLength":4000}}},
            "caption":{"type":"string","maxLength":500},
            "style":{"type":"string","enum":["default","compact","accent"]}
          },
          "required":["title","columns","rows"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public ToolDescriptor Descriptor { get; } = new(
        "rich_table",
        "Display structured comparison data as a polished native table in the assistant message. Use for Markdown-like tables, matrices, feature comparisons, benchmarks, schedules, or any data with stable columns. Do not print the same table again as plain Markdown.",
        Schema,
        ToolRisk.Safe,
        []);

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var title = arguments.GetProperty("title").GetString()!.Trim();
            if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("Table title cannot be blank.");
            var columns = arguments.GetProperty("columns").EnumerateArray().Select(item => item.GetString()?.Trim() ?? string.Empty).ToArray();
            if (columns.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Table column names cannot be blank.");
            var rows = new List<string[]>();
            foreach (var rowElement in arguments.GetProperty("rows").EnumerateArray())
            {
                var source = rowElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
                var row = new string[columns.Length];
                for (var index = 0; index < row.Length; index++) row[index] = index < source.Length ? source[index] : string.Empty;
                rows.Add(row);
            }
            var caption = arguments.TryGetProperty("caption", out var captionElement) ? captionElement.GetString()?.Trim() : null;
            var style = arguments.TryGetProperty("style", out var styleElement) ? styleElement.GetString() ?? "default" : "default";
            var output = JsonSerializer.Serialize(new { kind = "rich_table", title, columns, rows, caption, style }, DomainJson.Options);
            return Task.FromResult(new ToolExecutionResult(true, output, Metadata: new Dictionary<string, string> { ["presentation_kind"] = "rich_table", ["row_count"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return Task.FromResult(new ToolExecutionResult(false, string.Empty, exception.Message));
        }
    }
}

public sealed class CanvasTool : ITool
{
    private const int MaximumContentLength = 250_000;
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "title":{"type":"string","minLength":1,"maxLength":160},
            "format":{"type":"string","enum":["markdown","code"]},
            "content":{"type":"string","minLength":1,"maxLength":250000},
            "language":{"type":"string","maxLength":40},
            "description":{"type":"string","maxLength":500}
          },
          "required":["title","format","content"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public ToolDescriptor Descriptor { get; } = new(
        "canvas",
        "Display substantial Markdown or source code in a dedicated native canvas card with its own title and copy action. Use for standalone documents, plans, specifications, long formatted Markdown, scripts, Python examples, configuration, or code that should be visually separate from the conversational answer. In Markdown, write mathematics with LaTeX delimiters such as $...$, $$...$$, \\( ... \\), or \\[ ... \\] so it is rendered as readable math.",
        Schema,
        ToolRisk.Safe,
        []);

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var title = arguments.GetProperty("title").GetString()!.Trim();
            var format = arguments.GetProperty("format").GetString()!;
            var content = arguments.GetProperty("content").GetString()!;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Canvas title and content cannot be blank.");
            if (content.Length > MaximumContentLength) throw new InvalidOperationException($"Canvas content exceeds the {MaximumContentLength:N0} character limit.");
            var language = arguments.TryGetProperty("language", out var languageElement) ? languageElement.GetString()?.Trim() : null;
            if (format == "code" && string.IsNullOrWhiteSpace(language)) language = "text";
            var description = arguments.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString()?.Trim() : null;
            var output = JsonSerializer.Serialize(new { kind = "canvas", title, format, content, language, description }, DomainJson.Options);
            return Task.FromResult(new ToolExecutionResult(true, output, Metadata: new Dictionary<string, string> { ["presentation_kind"] = "canvas", ["canvas_format"] = format }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return Task.FromResult(new ToolExecutionResult(false, string.Empty, exception.Message));
        }
    }
}
