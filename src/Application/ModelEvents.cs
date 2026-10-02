using Neuroset.Domain;

namespace Neuroset.Application;

public abstract record ModelEvent(AgentEventKind Kind, DateTimeOffset Timestamp);
public sealed record TextDeltaEvent(string Delta) : ModelEvent(AgentEventKind.TextDelta, DateTimeOffset.UtcNow);
public sealed record ToolCallStartedEvent(string CallId, string Name) : ModelEvent(AgentEventKind.ToolCallStarted, DateTimeOffset.UtcNow);
public sealed record ToolCallArgumentsEvent(string CallId, string ArgumentsDelta) : ModelEvent(AgentEventKind.ToolCallArguments, DateTimeOffset.UtcNow);
public sealed record ToolExecutionStartedEvent(string CallId, string Name) : ModelEvent(AgentEventKind.ToolExecutionStarted, DateTimeOffset.UtcNow);
public sealed record ToolExecutionCompletedEvent(string CallId, string Name, bool Succeeded, string Output, TimeSpan Duration, string? Error = null, IReadOnlyDictionary<string,string>? Metadata = null) : ModelEvent(AgentEventKind.ToolExecutionCompleted, DateTimeOffset.UtcNow);
public sealed record AgentPlannedAction(string Tool, string Task, int EstimatedToolCalls);
public sealed record AgentProgressEvent(
    DateTimeOffset StartedAt,
    int RequestedActionCount,
    IReadOnlyDictionary<string, int> RequestedActionCounts,
    IReadOnlyList<AgentPlannedAction> PlannedActions,
    int CompletedActionCount,
    IReadOnlyDictionary<string, int> CompletedActionCounts,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    bool IsFinalizing,
    bool IsCompleted,
    DateTimeOffset? EstimatedCompletionAt = null) : ModelEvent(AgentEventKind.Progress, DateTimeOffset.UtcNow);
public sealed record UsageEvent(TokenUsage Usage) : ModelEvent(AgentEventKind.Usage, DateTimeOffset.UtcNow);
public sealed record ModelRecoveryEvent(string Code, string Message) : ModelEvent(AgentEventKind.Recovery, DateTimeOffset.UtcNow);
public sealed record ModelErrorEvent(string Code, string Message, bool IsTransient = false, Guid? SavedMessageId = null) : ModelEvent(AgentEventKind.Error, DateTimeOffset.UtcNow);
public sealed record ModelCompletedEvent(string FinishReason) : ModelEvent(AgentEventKind.Completed, DateTimeOffset.UtcNow);
