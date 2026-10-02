using System.Text;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class ImageGenerationTool(IImageGenerationProvider provider, HtmlArtifactOptions options) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        ImageGenerationPolicy.ToolName,
        "Generate one image with GPT Image, only when the user enables the image button for the current message. Rewrite their request into a detailed technical prompt. Supply positive visual filters and negative_filters for unwanted traits; these become {filter} and -{filter} constraints. The image is displayed automatically in the chat.",
        JsonDocument.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string","minLength":1,"maxLength":160},
          "prompt":{"type":"string","minLength":1,"maxLength":24000},
          "filters":{"type":"array","maxItems":24,"items":{"type":"string","minLength":1,"maxLength":240}},
          "negative_filters":{"type":"array","maxItems":24,"items":{"type":"string","minLength":1,"maxLength":240}},
          "size":{"type":"string","enum":["auto","1024x1024","1536x1024","1024x1536"]},
          "quality":{"type":"string","enum":["auto","low","medium","high"]}
        },"required":["title","prompt","filters","negative_filters"],"additionalProperties":false}
        """).RootElement.Clone(), ToolRisk.Write, ["images.generate"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.ImageGeneration is not { } authorization || !ImageGenerationPolicy.IsImageModel(authorization.Model))
            return new(false, string.Empty, "Image generation requires the image button for this message.");
        var title = arguments.GetProperty("title").GetString()!.Trim();
        var prompt = BuildPrompt(arguments);
        if (string.IsNullOrWhiteSpace(title) || prompt.Length > 32_000 || string.IsNullOrWhiteSpace(arguments.GetProperty("prompt").GetString()))
            return new(false, string.Empty, "The image title and prompt must be nonempty; the complete prompt must not exceed 32000 characters.");
        var result = await provider.GenerateImageAsync(new(authorization.Connection, authorization.Model, prompt,
            arguments.TryGetProperty("size", out var size) ? size.GetString()! : "auto",
            arguments.TryGetProperty("quality", out var quality) ? quality.GetString()! : "auto"), cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(options.RootDirectory, context.ConversationId.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"image-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, result.Bytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        var artifact = new GeneratedImageArtifact(title, path, authorization.Model, result.MediaType);
        return new(true, JsonSerializer.Serialize(artifact, DomainJson.Options), Metadata: new Dictionary<string, string>
        {
            ["artifact_path"] = path, ["content_type"] = result.MediaType, ["image_model"] = authorization.Model
        });
    }

    private static string BuildPrompt(JsonElement arguments)
    {
        var builder = new StringBuilder(arguments.GetProperty("prompt").GetString()!.Trim());
        builder.AppendLine().AppendLine("Visual constraints: {trait} means include; -{trait} means exclude any occurrence. Apply all inclusion constraints together.");
        foreach (var filter in arguments.GetProperty("filters").EnumerateArray())
            builder.Append('{').Append(filter.GetString()!.Trim()).AppendLine("}");
        builder.AppendLine("Exclude these visual traits from the result:");
        foreach (var filter in arguments.GetProperty("negative_filters").EnumerateArray())
            builder.Append("-{").Append(filter.GetString()!.Trim()).AppendLine("}");
        return builder.ToString();
    }
}
