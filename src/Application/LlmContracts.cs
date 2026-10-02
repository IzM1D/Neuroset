using System.Text.Json;
using Neuroset.Domain;

namespace Neuroset.Application;

public sealed record LlmMessage(
    MessageRole Role,
    string Content,
    string? Name = null,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    IReadOnlyList<AttachmentInfo>? Attachments = null);

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);
public sealed record LlmToolDefinition(string Name, string Description, JsonElement Parameters);

public sealed record LlmRequest(
    ConnectionProfile Connection,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    bool Stream = true,
    string? ResponseFormat = null,
    GenerationSettings? Generation = null,
    ReasoningEffort? ReasoningEffort = null,
    string? RequiredToolName = null);

public sealed record LlmResponse(
    string Content,
    IReadOnlyList<LlmToolCall> ToolCalls,
    TokenUsage? Usage,
    string FinishReason,
    bool ToolHistoryRecovered = false);

public sealed record ConnectionTestResult(bool Success, string Message, TimeSpan Latency);

public interface ILLMProvider
{
    string ProviderType { get; }
    IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default);
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default);
}

public interface ILLMProviderFactory
{
    ILLMProvider Create(ConnectionProfile connection);
}
