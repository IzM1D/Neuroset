using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Providers;

public sealed partial class AnthropicMessagesProvider
{
    private static readonly string[] ApiKeyErrorMarkers =
    [
        "rate limit", "resource exhausted", "insufficient balance", "insufficient credit", "credit balance is too low",
        "credit balance too low", "credits exhausted", "payment required", "billing hard limit", "billing limit",
        "invalid api key", "incorrect api key", "invalid x api key", "authentication error", "daily limit exceeded"
    ];

    private static Dictionary<string, object?> CreatePayload(LlmRequest request, bool stream)
    {
        var generation = request.Generation?.Normalize();
        var (system, messages) = BuildMessages(request.Messages);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Connection.Model,
            ["messages"] = messages,
            ["max_tokens"] = Math.Max(1, generation?.MaxTokens ?? request.Connection.MaxTokens),
            ["stream"] = stream
        };
        if (!string.IsNullOrWhiteSpace(system)) payload["system"] = system;
        // Anthropic accepts temperature in [0,1]; higher OpenAI-style values are clamped.
        payload["temperature"] = Math.Clamp(generation?.Temperature ?? request.Connection.Temperature, 0, 1);
        if (generation is not null)
        {
            payload["top_p"] = Math.Clamp(generation.TopP, 0, 1);
            if (generation.TopK is { } topK) payload["top_k"] = topK;
            if (generation.StopSequences.Count > 0) payload["stop_sequences"] = generation.StopSequences;
        }
        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(tool => new { name = tool.Name, description = tool.Description, input_schema = tool.Parameters }).ToArray();
            if (request.RequiredToolName is { } requiredTool)
            {
                if (!request.Tools.Any(tool => tool.Name.Equals(requiredTool, StringComparison.Ordinal)))
                    throw new ArgumentException("The required tool must be present in the request's tools.", nameof(request));
                payload["tool_choice"] = new { type = "tool", name = requiredTool };
            }
            else payload["tool_choice"] = new { type = "auto" };
        }
        else if (request.RequiredToolName is not null)
            throw new ArgumentException("A required tool cannot be used with an empty tool list.", nameof(request));
        return payload;
    }

    // Converts the OpenAI-style message list into Anthropic's (system, messages) shape.
    // Leading system prompts are folded into the top-level system field; system messages that
    // appear mid-conversation become user text so nothing is silently dropped. Consecutive
    // same-role turns are merged to preserve the strict user/assistant alternation the API expects.
    private static (string System, List<object> Messages) BuildMessages(IReadOnlyList<LlmMessage> source)
    {
        var normalized = LlmToolHistory.Normalize(source);
        var system = new System.Text.StringBuilder();
        var pending = new List<(string Role, List<object> Blocks)>();
        var started = false;

        void Append(string role, List<object> blocks)
        {
            if (blocks.Count == 0) return;
            if (pending.Count > 0 && pending[^1].Role == role) pending[^1].Blocks.AddRange(blocks);
            else pending.Add((role, blocks));
        }

        foreach (var message in normalized)
        {
            if (message.Role == MessageRole.System)
            {
                if (!started)
                {
                    if (system.Length > 0) system.Append("\n\n");
                    system.Append(message.Content);
                }
                else Append("user", TextBlocks(message.Content, message));
                continue;
            }
            started = true;
            switch (message.Role)
            {
                case MessageRole.Tool:
                    Append("user", [new { type = "tool_result", tool_use_id = message.ToolCallId ?? string.Empty, content = message.Content }]);
                    break;
                case MessageRole.Assistant:
                    var assistant = new List<object>();
                    if (!string.IsNullOrEmpty(message.Content)) assistant.Add(new { type = "text", text = message.Content });
                    if (message.ToolCalls is { Count: > 0 } calls)
                        foreach (var call in calls)
                            assistant.Add(new { type = "tool_use", id = call.Id, name = call.Name, input = ParseInput(call.ArgumentsJson) });
                    Append("assistant", assistant);
                    break;
                default:
                    Append("user", TextBlocks(message.Content, message));
                    break;
            }
        }

        var messages = pending
            .Select(entry => (object)new Dictionary<string, object?>
            {
                ["role"] = entry.Role,
                ["content"] = entry.Blocks.Count == 0 ? new object[] { new { type = "text", text = " " } } : entry.Blocks.ToArray()
            })
            .ToList();
        return (system.ToString(), messages);
    }

    private static List<object> TextBlocks(string content, LlmMessage message)
    {
        var blocks = new List<object>();
        if (!string.IsNullOrEmpty(content)) blocks.Add(new { type = "text", text = content });
        var images = message.Attachments?.Where(x => x.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(x.LocalPath) && x.SizeBytes <= 10L * 1024 * 1024) ?? [];
        foreach (var image in images)
        {
            var data = Convert.ToBase64String(File.ReadAllBytes(image.LocalPath));
            blocks.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data } });
        }
        if (blocks.Count == 0) blocks.Add(new { type = "text", text = " " });
        return blocks;
    }

    private static object ParseInput(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new Dictionary<string, object?>();
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return new Dictionary<string, object?>(); }
    }

    private static string MapFinishReason(string? stopReason) => stopReason switch
    {
        "tool_use" => "tool_calls",
        "max_tokens" => "length",
        "end_turn" or "stop_sequence" or "pause_turn" or "refusal" => "stop",
        null or "" => "stop",
        _ => stopReason
    };

    private async Task<ApiKeyAttempts> LoadApiKeysAsync(ConnectionProfile connection, CancellationToken cancellationToken)
    {
        var keys = new List<string?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secretId in new[] { connection.ApiKeySecretId }.Concat(connection.BackupApiKeySecretIds)
                     .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var key = await secretStore.GetAsync(secretId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key)) keys.Add(key);
        }
        if (keys.Count == 0) keys.Add(null);
        return new(keys, connection.BackupApiKeySecretIds.Count > 0);
    }

    private async Task<ApiSendResult> SendAsync(
        ConnectionProfile connection, Uri endpoint, byte[]? payload, ApiKeyAttempts keys,
        bool stream, bool retryTransient, CancellationToken cancellationToken, HttpMethod? method = null)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(method ?? HttpMethod.Post, endpoint);
            if (keys.Current is { } apiKey)
            {
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            if (!string.IsNullOrWhiteSpace(connection.Organization))
                message.Headers.TryAddWithoutValidation("anthropic-organization", connection.Organization);
            message.Headers.UserAgent.ParseAdd("Neuroset/1.0");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
            if (payload is not null)
            {
                message.Content = new ByteArrayContent(payload);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (retryTransient && attempt < MaximumRetries)
            {
                await Task.Delay(RetryDelay(attempt++), cancellationToken).ConfigureAwait(false);
                continue;
            }

            ProviderFailure? failure;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                failure = await ReadResponseFailureAsync(response, keys, cancellationToken).ConfigureAwait(false);
            }
            catch { response.Dispose(); throw; }

            if (failure is { IsApiKeyFailure: true } && keys.TryMoveNext())
            {
                response.Dispose();
                attempt = 0;
                continue;
            }
            var isTransient = failure is not null && IsTransient(response.StatusCode) && !(failure.IsApiKeyFailure && keys.FailoverEnabled);
            if (retryTransient && isTransient && attempt < MaximumRetries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay(attempt++), cancellationToken).ConfigureAwait(false);
                continue;
            }
            return new(response, failure?.Message, isTransient);
        }
    }

    private static async Task<ProviderFailure?> ReadResponseFailureAsync(HttpResponseMessage response, ApiKeyAttempts keys, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return null;
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = content;
        if (TryParseJson(content, out var document))
        {
            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                    message = ExtractError(error);
            }
        }
        if (string.IsNullOrWhiteSpace(message)) message = response.ReasonPhrase ?? "Provider request failed.";
        var isKeyFailure = IsApiKeyFailure(response.StatusCode, content);
        message = keys.Redact(message);
        return new(message.Length > 4096 ? message[..4096] : message, isKeyFailure);
    }

    private static bool IsApiKeyFailure(HttpStatusCode status, string error)
    {
        if ((int)status is 401 or 402 or 429) return true;
        var normalized = error.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        return ApiKeyErrorMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsTransient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static Uri BuildEndpoint(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        var normalized = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + '/');
        return new Uri(normalized, path);
    }

    private static System.Threading.CancellationTokenSource CreateTimeout(ConnectionProfile connection, CancellationToken cancellationToken)
    {
        var source = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 5, 3600)));
        return source;
    }

    private static TokenUsage? ParseUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        var input = GetInt(usage, "input_tokens"); var output = GetInt(usage, "output_tokens");
        if (input == 0 && output == 0) return null;
        return new(input, output, input + output);
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(350 * Math.Pow(2, attempt) + Random.Shared.Next(50, 200));
    private static bool TryParseJson(string data, out JsonDocument document) { try { document = JsonDocument.Parse(data); return true; } catch (JsonException) { document = null!; return false; } }
    private static string ExtractError(JsonElement error) => error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() ?? "Provider error" : error.ToString();
    private static string ExtractStreamError(JsonElement root) => root.TryGetProperty("error", out var error) ? ExtractError(error) : "The model stream reported an error.";

    [LoggerMessage(LogLevel.Information, "Anthropic request connection={ConnectionName} model={Model} host={Host}")]
    private static partial void LogRequest(ILogger logger, string connectionName, string model, string host);
    [LoggerMessage(LogLevel.Information, "Anthropic response connection={ConnectionName} latencyMs={LatencyMs}")]
    private static partial void LogResponse(ILogger logger, string connectionName, double latencyMs);
    [LoggerMessage(LogLevel.Warning, "Anthropic request failed connection={ConnectionName} status={StatusCode} error={Error}")]
    private static partial void LogFailure(ILogger logger, string connectionName, int statusCode, string error);

    private sealed class ApiKeyAttempts(IReadOnlyList<string?> keys, bool failoverEnabled)
    {
        private int _index;
        public string? Current => keys[_index];
        public bool FailoverEnabled { get; } = failoverEnabled;
        public bool TryMoveNext() { if (_index + 1 >= keys.Count) return false; _index++; return true; }
        public string Redact(string message)
        {
            foreach (var key in keys)
                if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[redacted]", StringComparison.Ordinal);
            return message;
        }
    }

    private sealed record ProviderFailure(string Message, bool IsApiKeyFailure);
    private sealed record ApiSendResult(HttpResponseMessage Response, string? Error, bool IsTransient);
}
