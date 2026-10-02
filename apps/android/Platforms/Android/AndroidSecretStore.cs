using System.Text;
using System.Text.Json;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Neuroset.Application;
using Neuroset.Security;

namespace Neuroset.App.Droid;

/// <summary>The encryption key never leaves Android Keystore. Only ciphertext is persisted.</summary>
public sealed class AndroidSecretStore(SecretStoreOptions options) : ISecretStore, IDisposable
{
    private const string KeyAlias = "Neuroset.ApiKeys.v1";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> StoreAsync(string secret, string? secretId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            using var key = GetKey();
            cipher.Init(CipherMode.EncryptMode, key);
            var plaintext = Encoding.UTF8.GetBytes(secret);
            try
            {
                var encrypted = cipher.DoFinal(plaintext)!;
                secretId ??= Guid.NewGuid().ToString("N");
                entries[secretId] = Convert.ToBase64String(cipher.GetIV()!) + "." + Convert.ToBase64String(encrypted);
                await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
                return secretId;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetAsync(string secretId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!entries.TryGetValue(secretId, out var value)) return null;
            var parts = value.Split('.');
            if (parts.Length != 2) throw new InvalidDataException("Invalid encrypted API key.");
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            using var parameters = new GCMParameterSpec(128, Convert.FromBase64String(parts[0]));
            using var key = GetKey();
            cipher.Init(CipherMode.DecryptMode, key, parameters);
            var plaintext = cipher.DoFinal(Convert.FromBase64String(parts[1]))!;
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
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

    private static IKey GetKey()
    {
        using var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.ContainsAlias(KeyAlias)) return store.GetKey(KeyAlias, null)!;
        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        using var specification = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm!)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone!)
            .SetRandomizedEncryptionRequired(true)
            .Build();
        generator.Init(specification);
        return generator.GenerateKey()!;
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.FilePath)) return [];
        await using var stream = File.OpenRead(options.FilePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task SaveAsync(Dictionary<string, string> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.FilePath)!);
        var temporary = options.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, options.FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _gate.Dispose();
}
