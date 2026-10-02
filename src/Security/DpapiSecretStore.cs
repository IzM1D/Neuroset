using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Neuroset.Application;

namespace Neuroset.Security;

public sealed record SecretStoreOptions(string FilePath)
{
    public static SecretStoreOptions CreateDefault()
    {
        var directory = Neuroset.Application.ApplicationPaths.DataDirectory;
        return new(Path.Combine(directory, "secrets.dat"));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(SecretStoreOptions options) : ISecretStore, IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Neuroset.LocalSecretStore.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> StoreAsync(string secret, string? secretId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        secretId ??= Guid.NewGuid().ToString("N");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            entries[secretId] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            return secretId;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetAsync(string secretId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!entries.TryGetValue(secretId, out var protectedValue)) return null;
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string secretId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (entries.Remove(secretId)) await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.FilePath)) return [];
        await using var stream = new FileStream(options.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task SaveAsync(Dictionary<string, string> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(options.FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"secrets-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, options.FilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void Dispose() => _gate.Dispose();
}
