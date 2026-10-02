using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Providers;

/// <summary>
/// Transport for the Anthropic Messages API (POST /v1/messages). Many gateways expose Claude
/// models only through this native endpoint (and block or omit the OpenAI-style
/// /v1/chat/completions path), so Claude connections are routed here instead.
/// </summary>
public sealed partial class AnthropicMessagesProvider(
    HttpClient httpClient,
    ISecretStore secretStore,
    ILogger<AnthropicMessagesProvider> logger,
    ISemanticResponseTransformer? semanticResponseTransformer = null) : ILLMProvider
{
    private const int MaximumRetries = 2;
    private const string AnthropicVersion = "2023-06-01";
    private static readonly ISemanticResponseTransformer DefaultResponseTransformer = new SemanticResponseTransformer();
    public string ProviderType => "anthropic-messages";

    public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(request.Connection.BaseUrl, "messages");
        using var timeout = CreateTimeout(request.Connection, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        LogRequest(logger, request.Connection.Name, request.Connection.Model, endpoint.Host);

        var keys = await LoadApiKeysAsync(request.Connection, timeout.Token).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(CreatePayload(request, stream: true));
        var sent = await SendAsync(request.Connection, endpoint, payload, keys, stream: true, retryTransient: true, timeout.Token).ConfigureAwait(false);
        using (sent.Response)
        {
            if (sent.Error is { } error)
            {
                LogFailure(logger, request.Connection.Name, (int)sent.Response.StatusCode, error);
                yield return new ModelErrorEvent(sent.Response.IsSuccessStatusCode ? "provider_error" : $"http_{(int)sent.Response.StatusCode}", error, sent.IsTransient);
                yield break;
            }

            await using var stream = await sent.Response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var settings = (request.Generation?.SemanticTransform ?? new SemanticTransformationSettings()).Normalize();
            var detector = semanticResponseTransformer ?? DefaultResponseTransformer;
            var transformSession = detector.CreateSession(settings);
            var semanticEnabled = settings.HedgeReducerEnabled || settings.DirectModeEnabled || settings.CasualModeEnabled;
            var bufferedText = semanticEnabled ? new StringBuilder() : null;
            var rawText = new StringBuilder();
            var toolIdByIndex = new Dictionary<int, string>();
            string? finishReason = null;

            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim();
                if (data is "[DONE]") break;
                if (!TryParseJson(data, out var document)) continue;
                using (document)
                {
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    switch (type)
                    {
                        case "message_start":
                            if (root.TryGetProperty("message", out var startMessage) && startMessage.TryGetProperty("usage", out var startUsage))
                            { var usage = ParseUsage(startUsage); if (usage is not null) yield return new UsageEvent(usage); }
                            break;
                        case "content_block_start":
                            if (root.TryGetProperty("content_block", out var block) && block.TryGetProperty("type", out var blockType) &&
                                blockType.GetString() == "tool_use")
                            {
                                var index = root.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : 0;
                                var id = block.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                                var name = block.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                                var info = (id ?? $"call_{Guid.NewGuid():N}", name ?? "unknown_tool");
                                toolIdByIndex[index] = info.Item1;
                                yield return new ToolCallStartedEvent(info.Item1, info.Item2);
                            }
                            break;
                        case "content_block_delta":
                            if (root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var deltaType))
                            {
                                switch (deltaType.GetString())
                                {
                                    case "text_delta" when delta.TryGetProperty("text", out var textElement) && textElement.GetString() is { Length: > 0 } text:
                                        rawText.Append(text);
                                        var transformed = transformSession.Push(text);
                                        if (!string.IsNullOrEmpty(transformed))
                                        {
                                            if (bufferedText is not null) bufferedText.Append(transformed);
                                            else yield return new TextDeltaEvent(transformed);
                                        }
                                        break;
                                    case "input_json_delta" when delta.TryGetProperty("partial_json", out var partial) && partial.GetString() is { Length: > 0 } json:
                                        var index = root.TryGetProperty("index", out var deltaIndex) ? deltaIndex.GetInt32() : 0;
                                        if (toolIdByIndex.TryGetValue(index, out var toolId)) yield return new ToolCallArgumentsEvent(toolId, json);
                                        break;
                                }
                            }
                            break;
                        case "message_delta":
                            if (root.TryGetProperty("delta", out var messageDelta) && messageDelta.TryGetProperty("stop_reason", out var stopReason) &&
                                stopReason.ValueKind == JsonValueKind.String)
                                finishReason = MapFinishReason(stopReason.GetString());
                            if (root.TryGetProperty("usage", out var deltaUsage))
                            { var usage = ParseUsage(deltaUsage); if (usage is not null) yield return new UsageEvent(usage); }
                            break;
                        case "error":
                            var trailing = transformSession.Complete();
                            if (!string.IsNullOrEmpty(trailing) && bufferedText is not null) bufferedText.Append(trailing);
                            yield return new ModelErrorEvent("provider_error", keys.Redact(ExtractStreamError(root)));
                            yield break;
                        case "message_stop":
                            finishReason ??= "stop";
                            break;
                    }
                }
            }

            var finalText = transformSession.Complete();
            if (!string.IsNullOrEmpty(finalText))
            {
                if (bufferedText is not null) bufferedText.Append(finalText);
                else yield return new TextDeltaEvent(finalText);
            }
            if (bufferedText is not null)
            {
                if (detector.ContainsRefusal(rawText.ToString(), settings))
                    yield return new ModelErrorEvent("semantic_refusal", "The model response contained refusal boilerplate.", IsTransient: true);
                else if (bufferedText.Length > 0)
                    yield return new TextDeltaEvent(bufferedText.ToString());
            }
            if (finishReason is not null) yield return new ModelCompletedEvent(finishReason);
        }
        LogResponse(logger, request.Connection.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(request.Connection.BaseUrl, "messages");
        using var timeout = CreateTimeout(request.Connection, cancellationToken);
        var keys = await LoadApiKeysAsync(request.Connection, timeout.Token).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(CreatePayload(request, stream: false));
        var sent = await SendAsync(request.Connection, endpoint, payload, keys, stream: false, retryTransient: true, timeout.Token).ConfigureAwait(false);
        using var response = sent.Response;
        if (sent.Error is { } error) throw new LlmProviderException((int)response.StatusCode, error, sent.IsTransient);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        var root = document.RootElement;
        var text = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var textElement) && textElement.GetString() is { } value) text.Append(value);
                else if (type == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    var name = block.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    var input = block.TryGetProperty("input", out var inputElement) ? inputElement.GetRawText() : "{}";
                    toolCalls.Add(new(id ?? $"call_{Guid.NewGuid():N}", name ?? "unknown_tool", input));
                }
            }
        }
        var transformed = (semanticResponseTransformer ?? DefaultResponseTransformer).Transform(
            text.ToString(), request.Generation?.SemanticTransform ?? new SemanticTransformationSettings());
        var usage = root.TryGetProperty("usage", out var usageElement) ? ParseUsage(usageElement) : null;
        var finishReason = root.TryGetProperty("stop_reason", out var stopReason) ? MapFinishReason(stopReason.GetString()) : "stop";
        return new(transformed, toolCalls, usage, finishReason);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(connection.BaseUrl, "models");
        using var timeout = CreateTimeout(connection, cancellationToken);
        var keys = await LoadApiKeysAsync(connection, timeout.Token).ConfigureAwait(false);
        var sent = await SendAsync(connection, endpoint, null, keys, stream: false, retryTransient: false, timeout.Token, HttpMethod.Get).ConfigureAwait(false);
        using var response = sent.Response;
        if (sent.Error is { } error) throw new LlmProviderException((int)response.StatusCode, error, sent.IsTransient);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var models = await ListModelsAsync(connection, cancellationToken).ConfigureAwait(false);
            return new(true, models.Count == 0 ? "Connection succeeded; the models endpoint returned no entries." : $"Connection succeeded; {models.Count} model(s) available.", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or LlmProviderException or TaskCanceledException or JsonException)
        {
            return new(false, ex.Message, stopwatch.Elapsed);
        }
    }
}
