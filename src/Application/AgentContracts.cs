using Neuroset.Domain;

namespace Neuroset.Application;

public sealed record AgentRunOptions(
    int MaximumIterations = 12,
    int RetryCount = 2,
    TimeSpan? OverallTimeout = null,
    bool EnableTools = true,
    int? HardMaximumIterations = null,
    int AutomaticExtensionIterations = 4,
    ReasoningPreference Reasoning = ReasoningPreference.Auto)
{
    public static AgentRunOptions ForConversation(Conversation conversation, AppSettings settings)
    {
        if (conversation.Mode == ConversationMode.Coding)
        {
            var hardMaximum=Math.Clamp(settings.CodingAgentMaxIterations,16,1000);
            return new(
                Math.Min(24,hardMaximum),
                Math.Clamp(settings.AgentRetryCount, 0, 10),
                TimeSpan.FromMinutes(Math.Clamp(settings.CodingAgentTimeoutMinutes, 1, 720)),
                HardMaximumIterations:hardMaximum,
                AutomaticExtensionIterations:8,
                Reasoning:settings.ReasoningPreference);
        }

        if (conversation.Mode == ConversationMode.Roleplay)
        {
            return new(
                Math.Clamp(settings.AgentMaxIterations, 1, 1000),
                Math.Clamp(settings.AgentRetryCount, 0, 10),
                EnableTools: false,
                HardMaximumIterations:Math.Clamp(settings.AgentMaxIterations,1,1000),
                Reasoning:settings.ReasoningPreference);
        }

        var initial=Math.Clamp(settings.AgentMaxIterations,1,1000);
        var hard=Math.Clamp(settings.AgentHardMaxIterations,initial,1000);
        if (GenerationSpeedPolicy.IsFastMode(conversation.Mode, settings.ReasoningPreference))
        {
            // Low effort is a request for a fast answer, so the turn gets room for one tool round
            // and no automatic extension: a growing budget is what turns a simple ask into minutes.
            initial = GenerationSpeedPolicy.LimitIterations(initial);
            hard = initial;
        }
        return new(
            initial,
            Math.Clamp(settings.AgentRetryCount, 0, 10),
            HardMaximumIterations:hard,
            Reasoning:settings.ReasoningPreference);
    }
}

public sealed record AgentRunRequest(
    Conversation Conversation,
    IReadOnlyList<ChatMessage> Messages,
    ConnectionProfile Connection,
    AgentRunOptions Options,
    Guid? ResponseMessageId = null,
    DateTimeOffset? StartedAt = null,
    bool GenerateImage = false);

public interface IAgentRuntime
{
    IAsyncEnumerable<ModelEvent> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default);
}

public interface IRoleplayRuntime
{
    Task<string> GenerateCharacterPromptAsync(string description, ConnectionProfile connection, RoleplayPromptKind kind = RoleplayPromptKind.Character, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ModelEvent> StreamCharacterAsync(Conversation conversation, RoleplayCharacter character, IReadOnlyList<ChatMessage> messages, ConnectionProfile connection, GenerationSettings generation, ReasoningPreference reasoning = ReasoningPreference.Low, CancellationToken cancellationToken = default);
}
