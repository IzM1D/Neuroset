using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.Logging;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class ToolRegistry(IEnumerable<ITool> tools) : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(tools.ToDictionary(x => x.Descriptor.Name, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<ITool> GetAll() => _tools.Values.OrderBy(x => x.Descriptor.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public bool TryGet(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);
    public void Register(ITool tool)
    {
        if (!_tools.TryAdd(tool.Descriptor.Name, tool)) throw new InvalidOperationException($"A tool named '{tool.Descriptor.Name}' is already registered.");
    }
}

public sealed partial class ToolExecutor(IToolRegistry registry, IPermissionService permissions, ILogger<ToolExecutor> logger) : IToolExecutor
{
    public async Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (toolName.Equals(ImageGenerationPolicy.ToolName, StringComparison.OrdinalIgnoreCase) && context.ImageGeneration is null)
            return new(false, string.Empty, "Image generation requires the image button for this message.");
        if (!registry.TryGet(toolName, out var tool) || tool is null) return new(false, string.Empty, $"Unknown tool: {toolName}");
        var validationError = Validate(tool.Descriptor.Schema, arguments);
        if (validationError is not null) return new(false, string.Empty, validationError);

        var decision = await permissions.AuthorizeAsync(new PermissionRequest(context.ConversationId, tool.Descriptor.Name, tool.Descriptor.Risk, tool.Descriptor.Description, arguments.GetRawText()), cancellationToken).ConfigureAwait(false);
        if (decision == PermissionDecision.Deny) return new(false, string.Empty, "The user denied permission for this tool call.");

        var started = Stopwatch.GetTimestamp();
        LogStarted(logger, tool.Descriptor.Name, context.ConversationId, decision.ToString());
        try
        {
            var result = await tool.ExecuteAsync(arguments, context, cancellationToken).ConfigureAwait(false);
            LogCompleted(logger, tool.Descriptor.Name, result.Success, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var error = $"Tool '{toolName}' timed out or was cancelled by its provider. Retry the call or continue with the available results.";
            LogFailed(logger, tool.Descriptor.Name, error);
            return new(false, string.Empty, error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogFailed(logger, tool.Descriptor.Name, ex.Message);
            return new(false, string.Empty, ex.Message);
        }
    }

    private static string? Validate(JsonElement schemaElement, JsonElement arguments)
    {
        try
        {
            var schema = JsonSchema.FromText(schemaElement.GetRawText());
            var result = schema.Evaluate(arguments, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid) return null;
            var details = result.Details?.Where(x => !x.IsValid).Select(x => x.EvaluationPath.ToString()).Take(5).ToArray() ?? [];
            return details.Length == 0 ? "Tool arguments do not match the JSON schema." : "Tool argument validation failed at: " + string.Join(", ", details);
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException)
        {
            return "Invalid tool schema: " + ex.Message;
        }
    }

    [LoggerMessage(LogLevel.Information, "Tool started tool={ToolName} conversation={ConversationId} permission={Permission}")]
    private static partial void LogStarted(ILogger logger, string toolName, Guid conversationId, string permission);
    [LoggerMessage(LogLevel.Information, "Tool completed tool={ToolName} success={Success} latencyMs={LatencyMs}")]
    private static partial void LogCompleted(ILogger logger, string toolName, bool success, double latencyMs);
    [LoggerMessage(LogLevel.Warning, "Tool failed tool={ToolName} error={Error}")]
    private static partial void LogFailed(ILogger logger, string toolName, string error);
}
