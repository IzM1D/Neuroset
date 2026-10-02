using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Storage;

public sealed class SqliteConnectionRepository(SqliteDatabase database) : IConnectionRepository
{
    public async Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ConnectionProfile>();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,BaseUrl,ApiKeySecretId,Model,Organization,TimeoutSeconds,Temperature,MaxTokens,IsActive,CreatedAt,UpdatedAt,BackupApiKeySecretIdsJson FROM Connections ORDER BY IsActive DESC, Name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Read(reader));
        return result;
    }

    public async Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,BaseUrl,ApiKeySecretId,Model,Organization,TimeoutSeconds,Temperature,MaxTokens,IsActive,CreatedAt,UpdatedAt,BackupApiKeySecretIdsJson FROM Connections WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (profile.IsActive)
        {
            await using var reset = connection.CreateCommand(); reset.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            reset.CommandText = "UPDATE Connections SET IsActive=0;"; await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand(); command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Connections(Id,Name,BaseUrl,ApiKeySecretId,Model,Organization,TimeoutSeconds,Temperature,MaxTokens,IsActive,CreatedAt,UpdatedAt,BackupApiKeySecretIdsJson)
            VALUES($id,$name,$url,$secret,$model,$organization,$timeout,$temperature,$tokens,$active,$created,$updated,$backupSecrets)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,BaseUrl=excluded.BaseUrl,ApiKeySecretId=excluded.ApiKeySecretId,
            Model=excluded.Model,Organization=excluded.Organization,TimeoutSeconds=excluded.TimeoutSeconds,Temperature=excluded.Temperature,
            MaxTokens=excluded.MaxTokens,IsActive=excluded.IsActive,UpdatedAt=excluded.UpdatedAt,BackupApiKeySecretIdsJson=excluded.BackupApiKeySecretIdsJson;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString()); command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$url", profile.BaseUrl); command.Parameters.AddWithValue("$secret", profile.ApiKeySecretId);
        command.Parameters.AddWithValue("$backupSecrets", JsonSerializer.Serialize(profile.BackupApiKeySecretIds, DomainJson.Options));
        command.Parameters.AddWithValue("$model", profile.Model); command.Parameters.AddWithValue("$organization", (object?)profile.Organization ?? DBNull.Value);
        command.Parameters.AddWithValue("$timeout", profile.TimeoutSeconds); command.Parameters.AddWithValue("$temperature", profile.Temperature);
        command.Parameters.AddWithValue("$tokens", profile.MaxTokens); command.Parameters.AddWithValue("$active", profile.IsActive);
        command.Parameters.AddWithValue("$created", profile.CreatedAt.ToString("O")); command.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM Connections WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString()); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ConnectionProfile Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id=Guid.Parse(r.GetString(0)),Name=r.GetString(1),BaseUrl=r.GetString(2),ApiKeySecretId=r.GetString(3),Model=r.GetString(4),
        Organization=r.IsDBNull(5)?null:r.GetString(5),TimeoutSeconds=r.GetInt32(6),Temperature=r.GetDouble(7),MaxTokens=r.GetInt32(8),
        IsActive=r.GetBoolean(9),CreatedAt=DateTimeOffset.Parse(r.GetString(10)),UpdatedAt=DateTimeOffset.Parse(r.GetString(11)),
        BackupApiKeySecretIds=JsonSerializer.Deserialize<List<string>>(r.GetString(12), DomainJson.Options) ?? []
    };
}

public sealed class SqliteSettingsRepository(SqliteDatabase database) : ISettingsRepository
{
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT ValueJson FROM Settings WHERE Key='app';";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(value, DomainJson.Options) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings(Key,ValueJson,UpdatedAt) VALUES('app',$json,$at) ON CONFLICT(Key) DO UPDATE SET ValueJson=excluded.ValueJson,UpdatedAt=excluded.UpdatedAt;";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, DomainJson.Options)); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
