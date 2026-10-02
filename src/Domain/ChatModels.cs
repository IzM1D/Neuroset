using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuroset.Domain;

public sealed class Conversation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "New chat";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public ConversationMode Mode { get; set; } = ConversationMode.Chat;
    public string? WorkspacePath { get; set; }
    public bool FullAccessEnabled { get; set; }
    public bool UseCustomGenerationSettings { get; set; }
    public GenerationSettings Generation { get; set; } = new();
    public RoleplaySettings Roleplay { get; set; } = new();
    public string SystemPrompt
    {
        get => Generation.SystemPrompt;
        set
        {
            Generation.SystemPrompt = value ?? string.Empty;
            UseCustomGenerationSettings = !string.IsNullOrWhiteSpace(value);
        }
    }
}

public sealed class RoleplaySettings
{
    public List<RoleplayCharacter> Characters { get; set; } = [];
    public string SceneDescription { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string PlayerDescription { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public string StartingSpeakerId { get; set; } = RoleplaySpeaker.User;
    public string PendingSpeakerId { get; set; } = RoleplaySpeaker.User;

    /// <summary>Name the characters use for the human participant in prompts and transcripts.</summary>
    [JsonIgnore]
    public string ResolvedPlayerName => string.IsNullOrWhiteSpace(PlayerName) ? RoleplaySpeaker.DefaultPlayerName : PlayerName.Trim();
}

public sealed class RoleplayCharacter
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
}

public static class RoleplaySpeaker
{
    public const string User = "user";
    public const string DefaultPlayerName = "User";
}

public static class RoleplayMessageMetadata
{
    public const string CharacterId = "roleplay_character_id";
    public const string CharacterName = "roleplay_character_name";
    public const string ResponseGroupId = "response_group_id";
}

public static class AgentRunMessageMetadata
{
    public const string StartedAt = "agent_run_started_at";
    public const string CompletedAt = "agent_run_completed_at";
    public const string DurationMilliseconds = "agent_run_duration_ms";
    public const string EstimatedRemainingMilliseconds = "agent_run_estimated_remaining_ms";
    public const string RequestedActionCount = "agent_requested_action_count";
    public const string RequestedActionCounts = "agent_requested_action_counts_json";
    public const string PlannedActions = "agent_planned_actions_json";
    public const string CompletedActionCount = "agent_completed_action_count";
    public const string CompletedActionCounts = "agent_completed_action_counts_json";
    public const string IsFinalizing = "agent_run_is_finalizing";
    public const string IsCompleted = "agent_run_is_completed";
    public const string ReasoningPreference = "agent_reasoning_preference";
    public const string ReasoningEffort = "agent_reasoning_effort";

    public static IReadOnlyList<string> AllKeys { get; } =
    [
        StartedAt,
        CompletedAt,
        DurationMilliseconds,
        EstimatedRemainingMilliseconds,
        RequestedActionCount,
        RequestedActionCounts,
        PlannedActions,
        CompletedActionCount,
        CompletedActionCounts,
        IsFinalizing,
        IsCompleted,
        ReasoningPreference,
        ReasoningEffort
    ];
}

public sealed class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConversationId { get; init; }
    public MessageRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Completed;
    public Guid? ParentMessageId { get; set; }
    public Guid VersionGroupId { get; set; }
    public int VersionIndex { get; set; }
    public bool IsActiveVersion { get; set; } = true;
    public int VersionCount { get; set; } = 1;
    public int VersionPosition { get; set; } = 1;
    public TokenUsage? Usage { get; set; }
    public List<AttachmentInfo> Attachments { get; init; } = [];
    public List<ToolCallRecord> ToolCalls { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record AttachmentInfo(
    Guid Id,
    string Name,
    string LocalPath,
    string MediaType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    Guid? KnowledgeSourceId = null);

public sealed record TokenUsage(int InputTokens, int OutputTokens, int TotalTokens);

public sealed class ToolCallRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string ArgumentsJson { get; set; } = "{}";
    public ToolExecutionRecord? Execution { get; set; }
}

public sealed class ToolExecutionRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ToolCallId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Succeeded { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? Error { get; set; }
    public PermissionDecision? PermissionDecision { get; set; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public static class DomainJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
