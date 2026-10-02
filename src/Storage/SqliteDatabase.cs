using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Neuroset.Application;

namespace Neuroset.Storage;

public sealed record DatabaseOptions(string DatabasePath)
{
    public static DatabaseOptions CreateDefault()
    {
        var directory = Neuroset.Application.ApplicationPaths.DataDirectory;
        return new(Path.Combine(directory, "neuroset.db"));
    }
}

public sealed class SqliteDatabase(DatabaseOptions options, ILogger<SqliteDatabase> logger) : IDatabaseInitializer
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true,
        Pooling = true
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);

        var version = await ScalarAsync<long>(connection, "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;", cancellationToken).ConfigureAwait(false);
        foreach (var migration in Migrations.All.Where(x => x.Version > version).OrderBy(x => x.Version))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, migration.Sql, cancellationToken, transaction).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT INTO SchemaMigrations (Version, AppliedAt) VALUES ($version, $at);";
                command.Parameters.AddWithValue("$version", migration.Version);
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Applied database migration {MigrationVersion}", migration.Version);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, System.Data.Common.DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction as SqliteTransaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SQLite scalar query returned null.");
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed record Migration(int Version, string Sql);

internal static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, """
            CREATE TABLE Conversations (
                Id TEXT PRIMARY KEY, Title TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                IsPinned INTEGER NOT NULL DEFAULT 0, IsArchived INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IX_Conversations_UpdatedAt ON Conversations(UpdatedAt DESC);

            CREATE TABLE Messages (
                Id TEXT PRIMARY KEY, ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE,
                Role INTEGER NOT NULL, Content TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NULL,
                Status INTEGER NOT NULL, ParentMessageId TEXT NULL, AttachmentsJson TEXT NOT NULL DEFAULT '[]',
                ToolCallsJson TEXT NOT NULL DEFAULT '[]', UsageJson TEXT NULL, MetadataJson TEXT NOT NULL DEFAULT '{}');
            CREATE INDEX IX_Messages_Conversation_Created ON Messages(ConversationId, CreatedAt);

            CREATE TABLE Attachments (
                Id TEXT PRIMARY KEY, MessageId TEXT NULL REFERENCES Messages(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, LocalPath TEXT NOT NULL, MediaType TEXT NOT NULL, SizeBytes INTEGER NOT NULL,
                KnowledgeSourceId TEXT NULL, CreatedAt TEXT NOT NULL);

            CREATE TABLE Settings (Key TEXT PRIMARY KEY, ValueJson TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
            CREATE TABLE Themes (Name TEXT PRIMARY KEY, DefinitionJson TEXT NOT NULL, IsCustom INTEGER NOT NULL DEFAULT 0, UpdatedAt TEXT NOT NULL);

            CREATE TABLE Connections (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL, BaseUrl TEXT NOT NULL, ApiKeySecretId TEXT NOT NULL,
                Model TEXT NOT NULL, Organization TEXT NULL, TimeoutSeconds INTEGER NOT NULL,
                Temperature REAL NOT NULL, MaxTokens INTEGER NOT NULL, IsActive INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);

            CREATE TABLE Memory (
                Id TEXT PRIMARY KEY, Scope INTEGER NOT NULL, ConversationId TEXT NULL, Content TEXT NOT NULL,
                Source TEXT NOT NULL, Importance REAL NOT NULL, CreatedAt TEXT NOT NULL, LastAccessedAt TEXT NOT NULL);
            CREATE INDEX IX_Memory_Scope_Conversation ON Memory(Scope, ConversationId);

            CREATE TABLE ScheduledTasks (
                Id TEXT PRIMARY KEY, DefinitionJson TEXT NOT NULL, NextRunAt TEXT NULL, IsEnabled INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL);
            CREATE INDEX IX_ScheduledTasks_NextRun ON ScheduledTasks(IsEnabled, NextRunAt);

            CREATE TABLE ToolPermissions (
                Id TEXT PRIMARY KEY, ToolName TEXT NOT NULL, Risk INTEGER NOT NULL, Policy INTEGER NOT NULL,
                ConversationId TEXT NULL, ConstraintJson TEXT NULL, UpdatedAt TEXT NOT NULL);
            CREATE INDEX IX_ToolPermissions_Lookup ON ToolPermissions(ToolName, Risk, ConversationId);

            CREATE TABLE KnowledgeSources (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL, LocalPath TEXT NOT NULL, MediaType TEXT NOT NULL,
                Sha256 TEXT NOT NULL, SizeBytes INTEGER NOT NULL, ChunkCount INTEGER NOT NULL, Status TEXT NOT NULL,
                Error TEXT NULL, CreatedAt TEXT NOT NULL, IndexedAt TEXT NULL);
            CREATE TABLE KnowledgeChunks (
                Id TEXT PRIMARY KEY, SourceId TEXT NOT NULL REFERENCES KnowledgeSources(Id) ON DELETE CASCADE,
                Ordinal INTEGER NOT NULL, Content TEXT NOT NULL, Embedding BLOB NOT NULL, MetadataJson TEXT NOT NULL DEFAULT '{}');
            CREATE INDEX IX_KnowledgeChunks_Source ON KnowledgeChunks(SourceId, Ordinal);

            CREATE TABLE ToolExecutions (
                Id TEXT PRIMARY KEY, ConversationId TEXT NOT NULL, MessageId TEXT NULL, ToolCallId TEXT NOT NULL,
                ToolName TEXT NOT NULL, StartedAt TEXT NOT NULL, CompletedAt TEXT NULL, Succeeded INTEGER NOT NULL,
                Output TEXT NOT NULL, Error TEXT NULL, PermissionDecision INTEGER NULL, MetadataJson TEXT NOT NULL DEFAULT '{}');
            """),
        new(2, """
            ALTER TABLE Conversations ADD COLUMN Mode INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE Conversations ADD COLUMN WorkspacePath TEXT NULL;
            ALTER TABLE Conversations ADD COLUMN FullAccessEnabled INTEGER NOT NULL DEFAULT 0;

            ALTER TABLE Messages ADD COLUMN VersionGroupId TEXT NULL;
            ALTER TABLE Messages ADD COLUMN VersionIndex INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE Messages ADD COLUMN IsActiveVersion INTEGER NOT NULL DEFAULT 1;
            UPDATE Messages SET VersionGroupId=Id WHERE VersionGroupId IS NULL;
            UPDATE Messages AS current
            SET ParentMessageId=(
                SELECT previous.Id FROM Messages AS previous
                WHERE previous.ConversationId=current.ConversationId
                  AND (previous.CreatedAt<current.CreatedAt OR (previous.CreatedAt=current.CreatedAt AND previous.rowid<current.rowid))
                ORDER BY previous.CreatedAt DESC, previous.rowid DESC LIMIT 1)
            WHERE current.ParentMessageId IS NULL;
            CREATE INDEX IX_Messages_Parent ON Messages(ConversationId, ParentMessageId);
            CREATE INDEX IX_Messages_VersionGroup ON Messages(ConversationId, VersionGroupId, VersionIndex);
            """),
        new(3, """
            ALTER TABLE Conversations ADD COLUMN SystemPrompt TEXT NOT NULL DEFAULT '';
            """),
        new(4, """
            ALTER TABLE Conversations ADD COLUMN UseCustomGenerationSettings INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE Conversations ADD COLUMN GenerationSettingsJson TEXT NULL;
            UPDATE Conversations SET UseCustomGenerationSettings=1 WHERE length(trim(SystemPrompt))>0;
            """),
        new(5, """
            ALTER TABLE Conversations ADD COLUMN RoleplaySettingsJson TEXT NULL;
            """),
        new(6, """
            ALTER TABLE Connections ADD COLUMN BackupApiKeySecretIdsJson TEXT NOT NULL DEFAULT '[]';
            """)
    ];
}
