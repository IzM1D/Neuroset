using Neuroset.Domain;

namespace Neuroset.Application;

public static class ConversationMessageGrouping
{
    public static IReadOnlyList<IReadOnlyList<ChatMessage>> GroupVisibleResponses(IEnumerable<ChatMessage> messages)
    {
        var groups = new List<IReadOnlyList<ChatMessage>>();
        List<ChatMessage>? assistantGroup = null;
        string? assistantGroupKey = null;

        foreach (var message in messages)
        {
            if (message.Role == MessageRole.Assistant)
            {
                var key = GetAssistantGroupKey(message);
                if (assistantGroup is { Count: > 0 } && !string.Equals(assistantGroupKey, key, StringComparison.Ordinal))
                    FlushAssistantGroup();
                assistantGroup ??= [];
                assistantGroupKey = key;
                assistantGroup.Add(message);
                continue;
            }

            FlushAssistantGroup();
            groups.Add([message]);
        }

        FlushAssistantGroup();
        return groups;

        void FlushAssistantGroup()
        {
            if (assistantGroup is not { Count: > 0 }) return;
            groups.Add(assistantGroup);
            assistantGroup = null;
            assistantGroupKey = null;
        }

        static string? GetAssistantGroupKey(ChatMessage message)
        {
            if (message.Metadata.TryGetValue(RoleplayMessageMetadata.ResponseGroupId, out var responseGroupId) && !string.IsNullOrWhiteSpace(responseGroupId))
                return responseGroupId;
            if (message.Metadata.ContainsKey(RoleplayMessageMetadata.CharacterId))
                return message.Id.ToString("D");
            return null;
        }
    }
}
