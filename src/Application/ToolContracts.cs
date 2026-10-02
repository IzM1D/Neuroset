using System.Text.Json;
using Neuroset.Domain;

namespace Neuroset.Application;

public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonElement Schema,
    ToolRisk Risk,
    IReadOnlyList<string> Permissions);

public sealed record ToolExecutionContext(
    Guid ConversationId,
    string ToolCallId,
    string WorkingDirectory,
    bool FullAccess,
    IReadOnlyDictionary<string, string> Metadata,
    ImageGenerationAuthorization? ImageGeneration = null,
    PresentationTurnContext? Presentation = null);

public sealed record ToolExecutionResult(
    bool Success,
    string Output,
    string? Error = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface ITool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    IReadOnlyCollection<ITool> GetAll();
    bool TryGet(string name, out ITool? tool);
    void Register(ITool tool);
}

public interface IToolExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default);
}

public sealed record PermissionRequest(
    Guid ConversationId,
    string ToolName,
    ToolRisk Risk,
    string Summary,
    string ArgumentsJson);

public interface IPermissionPrompt
{
    ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default);
}

public interface IPermissionService
{
    Task<PermissionDecision> AuthorizeAsync(PermissionRequest request, CancellationToken cancellationToken = default);
}
