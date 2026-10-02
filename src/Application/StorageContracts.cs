using Neuroset.Domain;

namespace Neuroset.Application;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IConversationRepository
{
    Task<IReadOnlyList<Conversation>> ListAsync(string? search = null, CancellationToken cancellationToken = default);
    Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task DeleteMessagesAfterAsync(Guid conversationId, DateTimeOffset timestamp, CancellationToken cancellationToken = default);
    Task<ChatMessage?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task<ChatMessage> CreateMessageVersionAsync(Guid messageId, string content, IReadOnlyList<AttachmentInfo>? attachments = null, CancellationToken cancellationToken = default);
    Task SetActiveMessageVersionAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task<bool> ActivateAdjacentMessageVersionAsync(Guid messageId, int direction, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
}

public interface IConnectionRepository
{
    Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(ConnectionProfile connection, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IKnowledgeRepository
{
    Task SaveSourceAsync(KnowledgeSource source, CancellationToken cancellationToken = default);
    Task SaveChunksAsync(Guid sourceId, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeSource>> ListSourcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeChunk>> GetChunksAsync(CancellationToken cancellationToken = default);
    Task DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
}

public interface IMemoryStore
{
    Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope? scope = null, Guid? conversationId = null, CancellationToken cancellationToken = default);
    Task SaveAsync(MemoryRecord memory, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAsync(MemoryScope? scope = null, CancellationToken cancellationToken = default);
}

public interface IScheduledTaskRepository
{
    Task<IReadOnlyList<ScheduledTaskDefinition>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ScheduledTaskDefinition task, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IToolPermissionRepository
{
    Task<ToolPermissionRule?> FindAsync(string toolName, ToolRisk risk, Guid? conversationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolPermissionRule>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ToolPermissionRule rule, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<string> StoreAsync(string secret, string? secretId = null, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string secretId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string secretId, CancellationToken cancellationToken = default);
}
