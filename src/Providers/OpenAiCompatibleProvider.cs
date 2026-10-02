using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Providers;

public sealed partial class OpenAiCompatibleProvider(
    HttpClient httpClient,
    ISecretStore secretStore,
    ILogger<OpenAiCompatibleProvider> logger,
    ISemanticResponseTransformer? semanticResponseTransformer = null,
    TimeSpan? toolStreamIdleTimeout = null) : ILLMProvider, IImageGenerationProvider
{
    private const int MaximumRetries = 2;
    private readonly TimeSpan _toolStreamIdleTimeout = toolStreamIdleTimeout ?? TimeSpan.FromSeconds(30);
    private static readonly ISemanticResponseTransformer DefaultResponseTransformer = new SemanticResponseTransformer();
    public string ProviderType => "openai-compatible";

    public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(request.Connection.BaseUrl, "chat/completions");
        using var timeout = CreateTimeout(request.Connection, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        LogRequest(logger, request.Connection.Name, request.Connection.Model, endpoint.Host, true);

        var keys = await LoadApiKeysAsync(request.Connection, timeout.Token).ConfigureAwait(false);
        var state = new ChatRequestState(request, true);
        var historyRecoveryReported = false;
        while (true)
        {
            var sent = await SendChatRequestAsync(state, endpoint, keys, timeout.Token).ConfigureAwait(false);
            using (sent.Response)
            {
                if (sent.Error is { } error)
                {
                    LogFailure(logger, request.Connection.Name, (int)sent.Response.StatusCode, error);
                    yield return new ModelErrorEvent(sent.Response.IsSuccessStatusCode ? "provider_error" : $"http_{(int)sent.Response.StatusCode}", error, sent.IsTransient);
                    yield break;
                }

                if (state.HistoryRecovered && !historyRecoveryReported)
                {
                    yield return new ModelRecoveryEvent(LlmToolHistory.RecoveryCode, LlmToolHistory.RecoveryMessage);
                    historyRecoveryReported = true;
                }
                await using var stream = await sent.Response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var toolNames = new Dictionary<int, (string Id, string Name)>();
                var transformSession = (semanticResponseTransformer ?? DefaultResponseTransformer).CreateSession(
                    state.EffectiveRequest.Generation?.SemanticTransform ?? new SemanticTransformationSettings());
                var semanticSettings = (state.EffectiveRequest.Generation?.SemanticTransform ?? new SemanticTransformationSettings()).Normalize();
                var refusalDetector = semanticResponseTransformer ?? DefaultResponseTransformer;
                var rawText = new StringBuilder();
                var semanticEnabled = semanticSettings.HedgeReducerEnabled || semanticSettings.DirectModeEnabled || semanticSettings.CasualModeEnabled;
                var bufferedSemanticText = semanticEnabled ? new StringBuilder() : null;
                string? finishReason = null;
                var receivedUsageOrToolData = false;
                var retryWithNextKey = false;
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    if (toolNames.Count > 0) idle.CancelAfter(_toolStreamIdleTimeout);
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested && toolNames.Count > 0)
                    {
                        // A finish marker makes the calls complete even if a proxy never closes SSE.
                        if (finishReason is not null) break;
                        throw new TimeoutException("The model stopped sending tool-call data. Tool preparation timed out; the pending calls were not sent.");
                    }
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                    var data = line[5..].Trim();
                    if (data == "[DONE]") break;
                    if (!TryParseJson(data, out var document)) continue;
                    using (document)
                    {
                        var root = document.RootElement;
                        if (root.TryGetProperty("error", out var providerError) && providerError.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                        {
                            if (rawText.Length == 0 && !receivedUsageOrToolData && finishReason is null &&
                                IsApiKeyFailure(HttpStatusCode.OK, providerError.GetRawText()) && keys.TryMoveNext())
                            {
                                retryWithNextKey = true;
                                break;
                            }
                            var trailing = transformSession.Complete();
                            if (!string.IsNullOrEmpty(trailing))
                            {
                                if (bufferedSemanticText is not null) bufferedSemanticText.Append(trailing);
                                else yield return new TextDeltaEvent(trailing);
                            }
                            if (bufferedSemanticText is not null && refusalDetector.ContainsRefusal(rawText.ToString(), semanticSettings))
                                yield return new ModelErrorEvent("semantic_refusal", "The model response contained refusal boilerplate.", IsTransient: true);
                            yield return new ModelErrorEvent("provider_error", keys.Redact(ExtractError(providerError)));
                            yield break;
                        }
                        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                        {
                            receivedUsageOrToolData = true;
                            var usage = ParseUsage(usageElement); if (usage is not null) yield return new UsageEvent(usage);
                        }
                        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta))
                        {
                            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            {
                                var text = content.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    rawText.Append(text);
                                    var transformed = transformSession.Push(text);
                                    if (!string.IsNullOrEmpty(transformed))
                                    {
                                        if (bufferedSemanticText is not null) bufferedSemanticText.Append(transformed);
                                        else yield return new TextDeltaEvent(transformed);
                                    }
                                }
                            }
                            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                            {
                                receivedUsageOrToolData |= toolCalls.GetArrayLength() > 0;
                                var pendingText = transformSession.Complete();
                                if (!string.IsNullOrEmpty(pendingText))
                                {
                                    if (bufferedSemanticText is not null) bufferedSemanticText.Append(pendingText);
                                    else yield return new TextDeltaEvent(pendingText);
                                }
                                foreach (var toolCall in toolCalls.EnumerateArray())
                                {
                                    var index = toolCall.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : 0;
                                    var id = toolCall.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                                    var function = toolCall.TryGetProperty("function", out var functionElement) ? functionElement : default;
                                    var name = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                                    if (!toolNames.ContainsKey(index) && (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(name)))
                                    {
                                        var info = (id ?? $"call_{Guid.NewGuid():N}", name ?? "unknown_tool");
                                        toolNames[index] = info; yield return new ToolCallStartedEvent(info.Item1, info.Item2);
                                    }
                                    if (toolNames.TryGetValue(index, out var existing) && function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var arguments))
                                    {
                                        var argumentsDelta = arguments.GetString();
                                        if (!string.IsNullOrEmpty(argumentsDelta)) yield return new ToolCallArgumentsEvent(existing.Id, argumentsDelta);
                                    }
                                }
                            }
                        }
                        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() is { } reason)
                            finishReason = reason;
                    }
                }
                if (retryWithNextKey) continue;
                var finalText = transformSession.Complete();
                if (!string.IsNullOrEmpty(finalText))
                {
                    if (bufferedSemanticText is not null) bufferedSemanticText.Append(finalText);
                    else yield return new TextDeltaEvent(finalText);
                }
                if (bufferedSemanticText is not null)
                {
                    if (refusalDetector.ContainsRefusal(rawText.ToString(), semanticSettings))
                        yield return new ModelErrorEvent("semantic_refusal", "The model response contained refusal boilerplate.", IsTransient: true);
                    else if (bufferedSemanticText.Length > 0)
                        yield return new TextDeltaEvent(bufferedSemanticText.ToString());
                }
                if (finishReason is not null) yield return new ModelCompletedEvent(finishReason);
            }
            break;
        }
        LogResponse(logger, request.Connection.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(request.Connection.BaseUrl, "chat/completions");
        using var timeout = CreateTimeout(request.Connection, cancellationToken);
        var keys = await LoadApiKeysAsync(request.Connection, timeout.Token).ConfigureAwait(false);
        var state = new ChatRequestState(request, false);
        var sent = await SendChatRequestAsync(state, endpoint, keys, timeout.Token).ConfigureAwait(false);
        using var response = sent.Response;
        if (sent.Error is { } error) throw new LlmProviderException((int)response.StatusCode, error, sent.IsTransient);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) throw new InvalidDataException("Provider response contains no choices.");
        var choice = choices[0]; var responseMessage = choice.GetProperty("message");
        var content = responseMessage.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String ? contentElement.GetString() ?? string.Empty : string.Empty;
        content = (semanticResponseTransformer ?? DefaultResponseTransformer).Transform(
            content,
            state.EffectiveRequest.Generation?.SemanticTransform ?? new SemanticTransformationSettings());
        var toolCalls = ParseToolCalls(responseMessage);
        var usage = root.TryGetProperty("usage", out var usageElement) ? ParseUsage(usageElement) : null;
        var finishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() ?? "stop" : "stop";
        return new(content, toolCalls, usage, finishReason, state.HistoryRecovered);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(connection.BaseUrl, "models");
        using var timeout = CreateTimeout(connection, cancellationToken);
        var keys = await LoadApiKeysAsync(connection, timeout.Token).ConfigureAwait(false);
        var sent = await SendWithApiKeysAsync(connection, endpoint, HttpMethod.Get, null, keys, false, false, timeout.Token).ConfigureAwait(false);
        using var response = sent.Response;
        if (sent.Error is { } error) throw new LlmProviderException((int)response.StatusCode, error, sent.IsTransient);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
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

    private static Dictionary<string, object?> CreatePayload(LlmRequest request, bool stream)
    {
        var generation = request.Generation?.Normalize();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Connection.Model,
            ["messages"] = request.Messages.Select(ToWireMessage).ToArray(),
            ["temperature"] = generation?.Temperature ?? request.Connection.Temperature,
            ["top_p"] = generation?.TopP ?? 1,
            ["max_tokens"] = generation?.MaxTokens ?? request.Connection.MaxTokens,
            ["stream"] = stream
        };
        if (generation is not null)
        {
            // top_k and repetition_penalty are optional extensions supported by
            // many local/open-model endpoints. Omit them when unset so strict
            // OpenAI-compatible servers continue to accept the payload.
            if (generation.TopK is { } topK) payload["top_k"] = topK;
            if (generation.RepetitionPenalty is { } repetitionPenalty) payload["repetition_penalty"] = repetitionPenalty;
            if (generation.PresencePenalty != 0) payload["presence_penalty"] = generation.PresencePenalty;
            if (generation.FrequencyPenalty != 0) payload["frequency_penalty"] = generation.FrequencyPenalty;
            if (generation.Seed is { } seed) payload["seed"] = seed;
            if (generation.StopSequences.Count > 0) payload["stop"] = generation.StopSequences;
        }
        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters } }).ToArray();
            if (request.RequiredToolName is { } requiredTool)
            {
                if (!request.Tools.Any(tool => tool.Name.Equals(requiredTool, StringComparison.Ordinal)))
                    throw new ArgumentException("The required tool must be present in the request's tools.", nameof(request));
                payload["tool_choice"] = new { type = "function", function = new { name = requiredTool } };
                payload["parallel_tool_calls"] = false;
            }
            else payload["tool_choice"] = "auto";
        }
        else if (request.RequiredToolName is not null)
            throw new ArgumentException("A required tool cannot be used with an empty tool list.", nameof(request));
        if (!string.IsNullOrWhiteSpace(request.ResponseFormat)) payload["response_format"] = new { type = request.ResponseFormat };
        if (request.ReasoningEffort is { } reasoningEffort) payload["reasoning_effort"] = ToWireReasoningEffort(reasoningEffort);
        return payload;
    }

    private async Task<ApiSendResult> SendChatRequestAsync(ChatRequestState state, Uri endpoint, ApiKeyAttempts keys, CancellationToken cancellationToken)
    {
        while(true)
        {
            var effectiveRequest = state.EffectiveRequest;
            var sent = await SendWithApiKeysAsync(effectiveRequest.Connection, endpoint, HttpMethod.Post,
                state.Payload, keys, state.Stream, true, cancellationToken).ConfigureAwait(false);
            if (sent.Error is not { } error) return sent;
            var response = sent.Response;
            if (!state.HistoryRecovered && IsToolHistoryError(response.StatusCode, error) &&
                effectiveRequest.Messages.Any(LlmToolHistory.HasToolProtocol))
            {
                LogHistoryRecovery(logger, effectiveRequest.Connection.Name, (int)response.StatusCode);
                response.Dispose();
                state.UpdateRequest(effectiveRequest with { Messages = LlmToolHistory.ToTranscript(effectiveRequest.Messages) });
                state.HistoryRecovered = true;
                continue;
            }
            if(TryGetReasoningFallback(effectiveRequest.ReasoningEffort,response.StatusCode,error,out var fallback))
            {
                LogReasoningFallback(
                    logger,
                    effectiveRequest.Connection.Name,
                    ToWireReasoningEffort(effectiveRequest.ReasoningEffort!.Value),
                    fallback is { } next?ToWireReasoningEffort(next):"none");
                response.Dispose();
                state.UpdateRequest(effectiveRequest with { ReasoningEffort = fallback });
                continue;
            }
            return sent;
        }
    }

    private static bool IsToolHistoryError(HttpStatusCode status, string error)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)) return false;
        return error.Contains("No tool output found for function call", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("No tool call found for function call output", StringComparison.OrdinalIgnoreCase) ||
               (error.Contains("tool_calls", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("must be followed", StringComparison.OrdinalIgnoreCase)) ||
               ((error.Contains("tool_call_id", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("function_call_output", StringComparison.OrdinalIgnoreCase)) &&
                (error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("without", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("did not have", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("must be a response", StringComparison.OrdinalIgnoreCase))) ||
               (error.Contains("role 'tool'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("must be a response", StringComparison.OrdinalIgnoreCase));
    }

    private static object ToWireMessage(LlmMessage message)
    {
        var value = new Dictionary<string, object?> { ["role"] = message.Role.ToString().ToLowerInvariant(), ["content"] = BuildContent(message) };
        if (!string.IsNullOrWhiteSpace(message.Name)) value["name"] = message.Name;
        if (!string.IsNullOrWhiteSpace(message.ToolCallId)) value["tool_call_id"] = message.ToolCallId;
        if (message.ToolCalls is { Count: > 0 }) value["tool_calls"] = message.ToolCalls.Select(x => new { id = x.Id, type = "function", function = new { name = x.Name, arguments = x.ArgumentsJson } }).ToArray();
        return value;
    }

    private static object BuildContent(LlmMessage message)
    {
        var images = message.Attachments?.Where(x => x.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && File.Exists(x.LocalPath) && x.SizeBytes <= 10L * 1024 * 1024).ToArray() ?? [];
        if (images.Length == 0) return message.Content;
        var parts = new List<object> { new { type = "text", text = message.Content } };
        foreach (var image in images)
        {
            var data = Convert.ToBase64String(File.ReadAllBytes(image.LocalPath));
            parts.Add(new { type = "image_url", image_url = new { url = $"data:{image.MediaType};base64,{data}" } });
        }
        return parts;
    }

    private static List<LlmToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return [];
        var result = new List<LlmToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var function)) continue;
            result.Add(new(call.GetProperty("id").GetString() ?? $"call_{Guid.NewGuid():N}", function.GetProperty("name").GetString() ?? "unknown_tool", function.GetProperty("arguments").GetString() ?? "{}"));
        }
        return result;
    }

    private static TokenUsage? ParseUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        var input = GetInt(usage, "prompt_tokens", "input_tokens"); var output = GetInt(usage, "completion_tokens", "output_tokens");
        var total = GetInt(usage, "total_tokens"); if (total == 0) total = input + output;
        return new(input, output, total);
    }

    private static int GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names) if (element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)) return result;
        return 0;
    }

    private static CancellationTokenSource CreateTimeout(ConnectionProfile connection, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 5, 3600)));
        return source;
    }

    private static Uri BuildEndpoint(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https")) throw new ArgumentException("Base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        var normalized = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + '/');
        return new Uri(normalized, path);
    }

    private static bool IsTransient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;
    private static bool TryGetReasoningFallback(ReasoningEffort? current,HttpStatusCode statusCode,string error,out ReasoningEffort? fallback)
    {
        fallback=null;
        if(current is null||statusCode!=HttpStatusCode.BadRequest||!MentionsUnsupportedReasoning(error))return false;
        fallback=current switch
        {
            ReasoningEffort.Max=>ReasoningEffort.XHigh,
            ReasoningEffort.XHigh=>ReasoningEffort.High,
            ReasoningEffort.Medium=>ReasoningEffort.Low,
            ReasoningEffort.High or ReasoningEffort.Low=>null,
            _=>null
        };
        return true;
    }
    private static bool MentionsUnsupportedReasoning(string error)=>
        error.Contains("reasoning",StringComparison.OrdinalIgnoreCase)&&
        (error.Contains("unsupported",StringComparison.OrdinalIgnoreCase)||
         error.Contains("not support",StringComparison.OrdinalIgnoreCase)||
         error.Contains("invalid",StringComparison.OrdinalIgnoreCase)||
         error.Contains("unknown",StringComparison.OrdinalIgnoreCase)||
         error.Contains("unrecognized",StringComparison.OrdinalIgnoreCase)||
         error.Contains("not allowed",StringComparison.OrdinalIgnoreCase));
    private static string ToWireReasoningEffort(ReasoningEffort effort)=>effort switch
    {
        ReasoningEffort.Low=>"low",
        ReasoningEffort.Medium=>"medium",
        ReasoningEffort.High=>"high",
        ReasoningEffort.XHigh=>"xhigh",
        ReasoningEffort.Max=>"max",
        _=>"medium"
    };
    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(350 * Math.Pow(2, attempt) + Random.Shared.Next(50, 200));
    private static bool TryParseJson(string data, out JsonDocument document) { try { document = JsonDocument.Parse(data); return true; } catch (JsonException) { document = null!; return false; } }
    private static string ExtractError(JsonElement error) => error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() ?? "Provider error" : error.ToString();

    [LoggerMessage(LogLevel.Information, "LLM request connection={ConnectionName} model={Model} host={Host} streaming={Streaming}")]
    private static partial void LogRequest(ILogger logger, string connectionName, string model, string host, bool streaming);
    [LoggerMessage(LogLevel.Information, "LLM response connection={ConnectionName} latencyMs={LatencyMs}")]
    private static partial void LogResponse(ILogger logger, string connectionName, double latencyMs);
    [LoggerMessage(LogLevel.Warning, "LLM request failed connection={ConnectionName} status={StatusCode} error={Error}")]
    private static partial void LogFailure(ILogger logger, string connectionName, int statusCode, string error);
    [LoggerMessage(LogLevel.Information, "LLM reasoning effort fallback connection={ConnectionName} from={FromEffort} to={ToEffort}")]
    private static partial void LogReasoningFallback(ILogger logger,string connectionName,string fromEffort,string toEffort);

    [LoggerMessage(LogLevel.Warning, "LLM tool history recovery connection={ConnectionName} status={StatusCode}; retrying with saved tool reports as text")]
    private static partial void LogHistoryRecovery(ILogger logger, string connectionName, int statusCode);

    private sealed class ChatRequestState
    {
        public ChatRequestState(LlmRequest request, bool stream)
        {
            Stream = stream;
            EffectiveRequest = request with { Messages = LlmToolHistory.Normalize(request.Messages) };
            Payload = JsonSerializer.SerializeToUtf8Bytes(CreatePayload(EffectiveRequest, stream));
        }
        public LlmRequest EffectiveRequest { get; private set; }
        public byte[] Payload { get; private set; }
        public bool Stream { get; }
        public bool HistoryRecovered { get; set; }
        public void UpdateRequest(LlmRequest request)
        {
            EffectiveRequest = request;
            Payload = JsonSerializer.SerializeToUtf8Bytes(CreatePayload(request, Stream));
        }
    }
}

public sealed class OpenAiCompatibleProviderFactory(OpenAiCompatibleProvider provider) : ILLMProviderFactory
{
    public ILLMProvider Create(ConnectionProfile connection) => provider;
}

public sealed class LlmProviderException(int statusCode, string message, bool isTransient) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public bool IsTransient { get; } = isTransient;
}
