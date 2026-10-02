using System.Text.Json;
using Neuroset.Application;

namespace Neuroset.Providers;

public sealed partial class OpenAiCompatibleProvider
{
    private const int MaximumImageBytes = 32 * 1024 * 1024;

    public async Task<GeneratedImageData> GenerateImageAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (!ImageGenerationPolicy.IsImageModel(request.Model))
            throw new ArgumentException("The selected model is not a supported GPT Image model.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32_000)
            throw new ArgumentException("The image prompt must contain between 1 and 32000 characters.", nameof(request));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.Connection.TimeoutSeconds, 180, 3600)));
        var keys = await LoadApiKeysAsync(request.Connection, timeout.Token).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = request.Model, prompt = request.Prompt, n = 1,
            size = request.Size, quality = request.Quality, output_format = "png"
        });
        // Do not automatically repeat a paid generation after a timeout or ambiguous server failure.
        var sent = await SendWithApiKeysAsync(request.Connection, BuildEndpoint(request.Connection.BaseUrl, "images/generations"),
            HttpMethod.Post, payload, keys, false, false, timeout.Token, inspectSuccessfulJsonErrors: false).ConfigureAwait(false);
        using var response = sent.Response;
        if (sent.Error is { } error)
            throw new LlmProviderException((int)response.StatusCode, error, sent.IsTransient);
        await response.Content.LoadIntoBufferAsync(MaximumImageBytes * 2L, timeout.Token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidDataException("The image API returned no images.");
        var item = data[0];
        if (!item.TryGetProperty("b64_json", out var encoded) || encoded.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(encoded.GetString()))
            throw new InvalidDataException("The GPT Image API returned no base64 image data (b64_json).");
        var bytes = Convert.FromBase64String(encoded.GetString()!);
        if (bytes.Length == 0 || bytes.Length > MaximumImageBytes)
            throw new InvalidDataException("The generated image is empty or exceeds 32 MB.");
        if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The image API did not return the requested PNG format.");
        return new(bytes, "image/png");
    }
}
