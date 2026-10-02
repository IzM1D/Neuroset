using System.Text.RegularExpressions;
using Neuroset.Domain;

namespace Neuroset.Application;

// Only the runtime supplies this context after an explicit composer action and model discovery.
// It is never reconstructed from chat history, tool arguments, or model-authored metadata.
public sealed record ImageGenerationAuthorization(ConnectionProfile Connection, string Model);

public sealed record ImageGenerationRequest(
    ConnectionProfile Connection, string Model, string Prompt, string Size = "auto", string Quality = "auto");

public sealed record GeneratedImageData(byte[] Bytes, string MediaType);

public sealed record GeneratedImageArtifact(string Title, string Path, string Model, string MediaType);

public interface IImageGenerationProvider
{
    Task<GeneratedImageData> GenerateImageAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default);
}

public static partial class ImageGenerationPolicy
{
    public const string ToolName = "image_generation";

    public static bool IsImageModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && ImageModelPattern().IsMatch(model);

    public static string? SelectModel(IEnumerable<string> models) => models
        .Where(IsImageModel)
        .OrderByDescending(model => ShortName(model).Equals("gpt-image-2", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(model => ShortName(model).StartsWith("gpt-image-2-", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(model => model, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    private static string ShortName(string model) => model[(model.LastIndexOf('/') + 1)..];

    [GeneratedRegex(@"^(?:[a-z0-9_.-]+/)?(?:gpt-image-\d+(?:\.\d+)?(?:-[a-z0-9.]+)*|chatgpt-image-latest)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageModelPattern();

    public const string PromptInstructions = """
        The user explicitly pressed the image button for THIS message. Call image_generation exactly once now.
        First translate the user's intent into a precise production prompt: subject and action, composition,
        framing, perspective, lighting, materials, palette, style, and any exact text to render. Preserve the
        user's requirements and language for visible text. Add only details that support their request.
        Put compatible positive visual constraints in filters (their intersection describes the image).
        Put unwanted visual traits and artifacts in negative_filters (exclude any matching trait).
        The tool expresses these as {filter} and -{filter}, and explains them in natural language to GPT Image.
        These are visual inclusion/exclusion instructions, not API parameters. Never negate something the
        user requested: e.g. intentional blur or lettering must not be excluded by generic quality negatives.
        Use a short descriptive title. Choose an appropriate size or auto. Do not ask the user to rewrite
        the prompt, use another tool, or claim that an image exists before the tool succeeds.
        The interface displays the returned image automatically. Do not duplicate it with a Markdown image.
        """;
}
