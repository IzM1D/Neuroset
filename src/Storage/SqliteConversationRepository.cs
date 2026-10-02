using System.Text.Json;
using Microsoft.Data.Sqlite;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Storage;

public sealed class SqliteConversationRepository(SqliteDatabase database) : IConversationRepository
{
    private const string ConversationColumns = "Id,Title,CreatedAt,UpdatedAt,IsPinned,IsArchived,Mode,WorkspacePath,FullAccessEnabled,SystemPrompt,UseCustomGenerationSettings,GenerationSettingsJson,RoleplaySettingsJson";
    private const string MessageColumns = "Id,ConversationId,Role,Content,CreatedAt,UpdatedAt,Status,ParentMessageId,AttachmentsJson,ToolCallsJson,UsageJson,MetadataJson,VersionGroupId,VersionIndex,IsActiveVersion";

    public async Task<IReadOnlyList<Conversation>> ListAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        var result = new List<Conversation>();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(search)
            ? $"SELECT {ConversationColumns} FROM Conversations ORDER BY IsPinned DESC, UpdatedAt DESC;"
            : $"SELECT {ConversationColumns} FROM Conversations WHERE Title LIKE $search OR EXISTS(SELECT 1 FROM Messages m WHERE m.ConversationId=Conversations.Id AND m.Content LIKE $search) ORDER BY IsPinned DESC, UpdatedAt DESC;";
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("$search", $"%{search.Trim()}%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ConversationColumns} FROM Conversations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConversation(reader) : null;
    }

    public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Conversations(Id,Title,CreatedAt,UpdatedAt,IsPinned,IsArchived,Mode,WorkspacePath,FullAccessEnabled,SystemPrompt,UseCustomGenerationSettings,GenerationSettingsJson,RoleplaySettingsJson)
            VALUES($id,$title,$created,$updated,$pinned,$archived,$mode,$workspace,$fullAccess,$systemPrompt,$useCustomGeneration,$generationSettings,$roleplaySettings)
            ON CONFLICT(Id) DO UPDATE SET Title=excluded.Title, UpdatedAt=excluded.UpdatedAt,
            IsPinned=excluded.IsPinned, IsArchived=excluded.IsArchived, Mode=excluded.Mode,
            WorkspacePath=excluded.WorkspacePath, FullAccessEnabled=excluded.FullAccessEnabled,
            SystemPrompt=excluded.SystemPrompt, UseCustomGenerationSettings=excluded.UseCustomGenerationSettings,
            GenerationSettingsJson=excluded.GenerationSettingsJson, RoleplaySettingsJson=excluded.RoleplaySettingsJson;
            """;
        command.Parameters.AddWithValue("$id", conversation.Id.ToString());
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$created", conversation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", conversation.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$pinned", conversation.IsPinned);
        command.Parameters.AddWithValue("$archived", conversation.IsArchived);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$workspace", (object?)conversation.WorkspacePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$fullAccess", conversation.FullAccessEnabled);
        command.Parameters.AddWithValue("$systemPrompt", conversation.SystemPrompt);
        command.Parameters.AddWithValue("$useCustomGeneration", conversation.UseCustomGenerationSettings);
        command.Parameters.AddWithValue("$generationSettings", JsonSerializer.Serialize((conversation.Generation ?? new GenerationSettings()).Normalize(), DomainJson.Options));
        command.Parameters.AddWithValue("$roleplaySettings", JsonSerializer.Serialize(conversation.Roleplay ?? new RoleplaySettings(), DomainJson.Options));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => ExecuteByIdAsync("DELETE FROM Conversations WHERE Id=$id;", id, cancellationToken);

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var all = new List<ChatMessage>();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MessageColumns} FROM Messages WHERE ConversationId=$id ORDER BY CreatedAt, rowid;";
        command.Parameters.AddWithValue("$id", conversationId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) all.Add(ReadMessage(reader));
        return BuildActivePath(all);
    }

    public async Task<ChatMessage?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MessageColumns} FROM Messages WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", messageId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    public async Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SaveMessageAsync(connection, null, message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatMessage> CreateMessageVersionAsync(Guid messageId, string content, IReadOnlyList<AttachmentInfo>? attachments = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var original = await GetMessageAsync(connection, transaction, messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The message is no longer available.");
        var groupId = original.VersionGroupId == Guid.Empty ? original.Id : original.VersionGroupId;

        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE Messages SET IsActiveVersion=0 WHERE ConversationId=$conversation AND COALESCE(VersionGroupId,Id)=$group;";
            deactivate.Parameters.AddWithValue("$conversation", original.ConversationId.ToString());
            deactivate.Parameters.AddWithValue("$group", groupId.ToString());
            await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var nextIndex = await GetNextVersionIndexAsync(connection, transaction, original.ConversationId, groupId, cancellationToken).ConfigureAwait(false);
        var version = new ChatMessage
        {
            ConversationId = original.ConversationId,
            Role = original.Role,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = MessageStatus.Completed,
            ParentMessageId = original.ParentMessageId,
            VersionGroupId = groupId,
            VersionIndex = nextIndex,
            IsActiveVersion = true,
            Attachments = attachments?.ToList() ?? original.Attachments.ToList(),
            Metadata = new Dictionary<string, string>(original.Metadata)
        };
        await SaveMessageAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return version;
    }

    public async Task SetActiveMessageVersionAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var message = await GetMessageAsync(connection, transaction, messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The message is no longer available.");
        var groupId = message.VersionGroupId == Guid.Empty ? message.Id : message.VersionGroupId;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Messages SET IsActiveVersion=CASE WHEN Id=$id THEN 1 ELSE 0 END WHERE ConversationId=$conversation AND COALESCE(VersionGroupId,Id)=$group;";
        command.Parameters.AddWithValue("$id", messageId.ToString());
        command.Parameters.AddWithValue("$conversation", message.ConversationId.ToString());
        command.Parameters.AddWithValue("$group", groupId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ActivateAdjacentMessageVersionAsync(Guid messageId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0) return false;
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetMessageAsync(connection, transaction, messageId, cancellationToken).ConfigureAwait(false);
        if (current is null) return false;
        var groupId = current.VersionGroupId == Guid.Empty ? current.Id : current.VersionGroupId;
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = direction < 0
            ? "SELECT Id FROM Messages WHERE ConversationId=$conversation AND COALESCE(VersionGroupId,Id)=$group AND VersionIndex<$version ORDER BY VersionIndex DESC LIMIT 1;"
            : "SELECT Id FROM Messages WHERE ConversationId=$conversation AND COALESCE(VersionGroupId,Id)=$group AND VersionIndex>$version ORDER BY VersionIndex LIMIT 1;";
        find.Parameters.AddWithValue("$conversation", current.ConversationId.ToString());
        find.Parameters.AddWithValue("$group", groupId.ToString());
        find.Parameters.AddWithValue("$version", current.VersionIndex);
        var adjacent = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (!Guid.TryParse(adjacent, out var adjacentId)) return false;
        await using var activate = connection.CreateCommand();
        activate.Transaction = transaction;
        activate.CommandText = "UPDATE Messages SET IsActiveVersion=CASE WHEN Id=$id THEN 1 ELSE 0 END WHERE ConversationId=$conversation AND COALESCE(VersionGroupId,Id)=$group;";
        activate.Parameters.AddWithValue("$id", adjacentId.ToString());
        activate.Parameters.AddWithValue("$conversation", current.ConversationId.ToString());
        activate.Parameters.AddWithValue("$group", groupId.ToString());
        await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var message = await GetMessageAsync(connection, transaction, messageId, cancellationToken).ConfigureAwait(false);
        if (message is null) return;
        var groupId = message.VersionGroupId == Guid.Empty ? message.Id : message.VersionGroupId;
        if (message.IsActiveVersion)
        {
            await using var activate = connection.CreateCommand();
            activate.Transaction = transaction;
            activate.CommandText = "UPDATE Messages SET IsActiveVersion=1 WHERE Id=(SELECT Id FROM Messages WHERE ConversationId=$conversation AND COALESCE(VersionGroupId,Id)=$group AND Id<>$id ORDER BY VersionIndex DESC LIMIT 1);";
            activate.Parameters.AddWithValue("$conversation", message.ConversationId.ToString());
            activate.Parameters.AddWithValue("$group", groupId.ToString());
            activate.Parameters.AddWithValue("$id", messageId.ToString());
            await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "WITH RECURSIVE subtree(Id) AS (SELECT $id UNION ALL SELECT m.Id FROM Messages m JOIN subtree s ON m.ParentMessageId=s.Id) DELETE FROM Messages WHERE Id IN (SELECT Id FROM subtree);";
        command.Parameters.AddWithValue("$id", messageId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMessagesAfterAsync(Guid conversationId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Messages WHERE ConversationId=$id AND CreatedAt>$at;";
        command.Parameters.AddWithValue("$id", conversationId.ToString());
        command.Parameters.AddWithValue("$at", timestamp.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<ChatMessage> BuildActivePath(List<ChatMessage> all)
    {
        if (all.Count == 0) return [];
        var versions = all.GroupBy(x => x.VersionGroupId == Guid.Empty ? x.Id : x.VersionGroupId)
            .ToDictionary(x => x.Key, x => x.OrderBy(m => m.VersionIndex).ThenBy(m => m.CreatedAt).ToArray());
        foreach (var message in all)
        {
            var group = versions[message.VersionGroupId == Guid.Empty ? message.Id : message.VersionGroupId];
            message.VersionCount = group.Length;
            message.VersionPosition = Array.FindIndex(group, x => x.Id == message.Id) + 1;
        }

        var children = all.GroupBy(x => x.ParentMessageId ?? Guid.Empty).ToDictionary(x => x.Key, x => x.ToArray());
        var result = new List<ChatMessage>();
        var parent = Guid.Empty;
        while (children.TryGetValue(parent, out var candidates))
        {
            var next = candidates.Where(x => x.IsActiveVersion).OrderBy(x => x.CreatedAt).FirstOrDefault();
            if (next is null) break;
            RestoreInterruptedToolHistory(next, children);
            result.Add(next);
            parent = next.Id;
        }
        return result;
    }

    private static void RestoreInterruptedToolHistory(ChatMessage message, Dictionary<Guid, ChatMessage[]> children)
    {
        if (message.Role != MessageRole.Assistant ||
            !message.Metadata.TryGetValue("interrupted", out var interrupted) || interrupted != "true" ||
            !message.Metadata.TryGetValue(AgentRunMessageMetadata.StartedAt, out var run) || string.IsNullOrWhiteSpace(run) ||
            !children.TryGetValue(message.ParentMessageId ?? Guid.Empty, out var siblings)) return;

        // Older UI error handling saved an aggregate response beside the runtime's completed
        // phases. The aggregate hid that sibling chain, although its reports remained in SQLite.
        // Reattach only reports from that exact run in memory; preserve database rows and versions.
        var pending = new Queue<ChatMessage>(siblings.Where(candidate => candidate.Id != message.Id && SameRun(candidate)));
        var visited = new HashSet<Guid> { message.Id };
        var recovered = new List<ChatMessage>();
        while (pending.TryDequeue(out var phase))
        {
            if (!visited.Add(phase.Id)) continue;
            recovered.Add(phase);
            if (children.TryGetValue(phase.Id, out var following))
                foreach (var child in following.Where(SameRun)) pending.Enqueue(child);
        }
        var calls = recovered.OrderBy(phase => phase.CreatedAt).SelectMany(phase => phase.ToolCalls).ToList();
        if (calls.Count == 0) return;
        var ids = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        calls.AddRange(message.ToolCalls.Where(call => ids.Add(call.Id)));
        message.ToolCalls.Clear();
        message.ToolCalls.AddRange(calls.DistinctBy(call => call.Id));
        message.Metadata[LlmToolHistory.RecoveredCallsMetadataKey] = "true";

        bool SameRun(ChatMessage candidate) => candidate.IsActiveVersion && candidate.Role == MessageRole.Assistant &&
            !candidate.Metadata.ContainsKey("interrupted") &&
            candidate.Metadata.TryGetValue(AgentRunMessageMetadata.StartedAt, out var candidateRun) && candidateRun == run;
    }

    private static async Task SaveMessageAsync(SqliteConnection connection, SqliteTransaction? transaction, ChatMessage message, CancellationToken cancellationToken)
    {
        var groupId = message.VersionGroupId == Guid.Empty ? message.Id : message.VersionGroupId;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Messages(Id,ConversationId,Role,Content,CreatedAt,UpdatedAt,Status,ParentMessageId,AttachmentsJson,ToolCallsJson,UsageJson,MetadataJson,VersionGroupId,VersionIndex,IsActiveVersion)
            VALUES($id,$conversation,$role,$content,$created,$updated,$status,$parent,$attachments,$tools,$usage,$metadata,$group,$version,$active)
            ON CONFLICT(Id) DO UPDATE SET Content=excluded.Content, UpdatedAt=excluded.UpdatedAt, Status=excluded.Status,
            ParentMessageId=excluded.ParentMessageId, AttachmentsJson=excluded.AttachmentsJson, ToolCallsJson=excluded.ToolCallsJson,
            UsageJson=excluded.UsageJson, MetadataJson=excluded.MetadataJson, VersionGroupId=excluded.VersionGroupId,
            VersionIndex=excluded.VersionIndex, IsActiveVersion=excluded.IsActiveVersion;
            """;
        command.Parameters.AddWithValue("$id", message.Id.ToString());
        command.Parameters.AddWithValue("$conversation", message.ConversationId.ToString());
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", (object?)message.UpdatedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)message.Status);
        command.Parameters.AddWithValue("$parent", (object?)message.ParentMessageId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(message.Attachments, DomainJson.Options));
        command.Parameters.AddWithValue("$tools", JsonSerializer.Serialize(message.ToolCalls, DomainJson.Options));
        command.Parameters.AddWithValue("$usage", message.Usage is null ? DBNull.Value : JsonSerializer.Serialize(message.Usage, DomainJson.Options));
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(message.Metadata, DomainJson.Options));
        command.Parameters.AddWithValue("$group", groupId.ToString());
        command.Parameters.AddWithValue("$version", message.VersionIndex);
        command.Parameters.AddWithValue("$active", message.IsActiveVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ChatMessage?> GetMessageAsync(SqliteConnection connection, SqliteTransaction transaction, Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {MessageColumns} FROM Messages WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", messageId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    private static async Task<int> GetNextVersionIndexAsync(SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, Guid groupId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(VersionIndex),-1)+1 FROM Messages WHERE ConversationId=$conversation AND COALESCE(VersionGroupId,Id)=$group;";
        command.Parameters.AddWithValue("$conversation", conversationId.ToString());
        command.Parameters.AddWithValue("$group", groupId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteByIdAsync(string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Conversation ReadConversation(SqliteDataReader reader)
    {
        GenerationSettings generation;
        try
        {
            generation = reader.IsDBNull(11) ? new GenerationSettings { SystemPrompt = reader.GetString(9) } : JsonSerializer.Deserialize<GenerationSettings>(reader.GetString(11), DomainJson.Options) ?? new GenerationSettings { SystemPrompt = reader.GetString(9) };
        }
        catch (JsonException)
        {
            generation = new GenerationSettings { SystemPrompt = reader.GetString(9) };
        }
        RoleplaySettings roleplay;
        try
        {
            roleplay = reader.IsDBNull(12) ? new RoleplaySettings() : JsonSerializer.Deserialize<RoleplaySettings>(reader.GetString(12), DomainJson.Options) ?? new RoleplaySettings();
        }
        catch (JsonException)
        {
            roleplay = new RoleplaySettings();
        }
        return new()
        {
            Id = Guid.Parse(reader.GetString(0)), Title = reader.GetString(1), CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(3)), IsPinned = reader.GetBoolean(4), IsArchived = reader.GetBoolean(5),
            Mode = (ConversationMode)reader.GetInt32(6), WorkspacePath = reader.IsDBNull(7) ? null : reader.GetString(7), FullAccessEnabled = reader.GetBoolean(8),
            UseCustomGenerationSettings = reader.GetBoolean(10), Generation = generation.Normalize(), Roleplay = roleplay
        };
    }

    private static ChatMessage ReadMessage(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)), ConversationId = Guid.Parse(reader.GetString(1)), Role = (MessageRole)reader.GetInt32(2),
        Content = reader.GetString(3), CreatedAt = DateTimeOffset.Parse(reader.GetString(4)), UpdatedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
        Status = (MessageStatus)reader.GetInt32(6), ParentMessageId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
        Attachments = JsonSerializer.Deserialize<List<AttachmentInfo>>(reader.GetString(8), DomainJson.Options) ?? [],
        ToolCalls = JsonSerializer.Deserialize<List<ToolCallRecord>>(reader.GetString(9), DomainJson.Options) ?? [],
        Usage = reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<TokenUsage>(reader.GetString(10), DomainJson.Options),
        Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(11), DomainJson.Options) ?? [],
        VersionGroupId = reader.IsDBNull(12) ? Guid.Parse(reader.GetString(0)) : Guid.Parse(reader.GetString(12)),
        VersionIndex = reader.GetInt32(13), IsActiveVersion = reader.GetBoolean(14)
    };
}
