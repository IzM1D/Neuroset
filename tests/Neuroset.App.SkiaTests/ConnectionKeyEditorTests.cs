using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuroset.App.Localization;
using Neuroset.App.ViewModels;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.App;

public partial class App
{
    internal static bool ConnectionKeyTestsOnly { get; set; }
}

public sealed partial class MainPage
{
    internal async Task VerifyConnectionKeyEditorAsync()
    {
        await App.Initialization;
        var repository = new KeyTestRepository(App.Services.GetRequiredService<IConnectionRepository>());
        var secrets = new KeyTestSecretStore(App.Services.GetRequiredService<ISecretStore>());
        var probe = new KeyTestProvider(secrets);
        using var settings = ActivatorUtilities.CreateInstance<SettingsViewModel>(App.Services, repository, secrets, probe);
        ViewModel.IsFirstRun = false;
        ViewModel.IsSettingsOpen = true;
        SettingsOverlay.DataContext = settings;
        SettingsOverlay.NavigateTo("Connections");
        settings.NewConnectionCommand.Execute(null);
        settings.ConnectionName = "Backup key test";
        settings.BaseUrl = "https://example.invalid/v1";
        settings.Model = "test-model";
        settings.ApiKey = "test-primary";
        UpdateLayout();
        var addButton = Descendants(SettingsOverlay).OfType<Button>()
            .Single(button => Equals(button.Content, LocalizationService.Get("Add backup API key")));
        addButton.Command.Execute(addButton.CommandParameter);
        addButton.Command.Execute(addButton.CommandParameter);
        await Task.Delay(30);
        UpdateLayout();
        var boxes = Descendants(SettingsOverlay).OfType<PasswordBox>().Where(box => box.DataContext is BackupApiKeyViewModel).ToArray();
        Check(boxes.Length == 2, "The add button must create two password fields.");
        for (var i = 0; i < boxes.Length; i++) boxes[i].Password = $"test-backup-{i}";
        Check(settings.BackupApiKeys.Select(item => item.ApiKey).SequenceEqual(["test-backup-0", "test-backup-1"]), "Password bindings must update the editor.");
        await settings.SaveConnectionCommand.ExecuteAsync(null);
        var saved = (await repository.ListAsync()).Single();
        Check(await secrets.GetAsync(saved.ApiKeySecretId) == "test-primary", "The main key must be stored securely.");
        Check(saved.BackupApiKeySecretIds.Count == 2, "Both backup references must be saved.");
        Check(await secrets.GetAsync(saved.BackupApiKeySecretIds[0]) == "test-backup-0", "The first backup must keep its order.");
        Check(await secrets.GetAsync(saved.BackupApiKeySecretIds[1]) == "test-backup-1", "The second backup must keep its order.");
        Check(settings.ApiKey.Length == 0 && settings.BackupApiKeys.All(item => item.ApiKey.Length == 0), "Saving must clear plaintext input fields.");

        settings.NewConnectionCommand.Execute(null);
        settings.SelectedConnection = await repository.GetAsync(saved.Id);
        Check(settings.BackupApiKeys.Count == 2 && settings.BackupApiKeys.All(item => item.ApiKey.Length == 0), "Reload must restore masked key rows.");
        await settings.SaveConnectionCommand.ExecuteAsync(null);
        var unchanged = (await repository.GetAsync(saved.Id))!;
        Check(unchanged.ApiKeySecretId == saved.ApiKeySecretId && unchanged.BackupApiKeySecretIds.SequenceEqual(saved.BackupApiKeySecretIds), "Blank saved fields must keep all existing keys.");

        // A test or model refresh must use unsaved edits without retaining temporary secrets.
        settings.ApiKey = "probe-primary";
        settings.BackupApiKeys[0].ApiKey = "probe-backup";
        settings.AddBackupApiKeyCommand.Execute(null);
        settings.BackupApiKeys[2].ApiKey = "probe-extra";
        secrets.StoredIds.Clear();
        await settings.TestConnectionCommand.ExecuteAsync(null);
        Check(probe.Values.SequenceEqual(["probe-primary", "probe-backup", "test-backup-1", "probe-extra"]), "Connection tests must include unsaved backup keys.");
        foreach (var id in secrets.StoredIds) Check(await secrets.GetAsync(id) is null, "Connection tests must delete every temporary secret.");
        Check(await secrets.GetAsync(saved.ApiKeySecretId) == "test-primary", "A probe must preserve the saved primary key.");
        probe.Fail = true;
        secrets.StoredIds.Clear();
        await settings.RefreshModelsCommand.ExecuteAsync(null);
        foreach (var id in secrets.StoredIds) Check(await secrets.GetAsync(id) is null, "A failed model refresh must delete every temporary secret.");
        repository.FailSave = true;
        secrets.StoredIds.Clear();
        await settings.SaveConnectionCommand.ExecuteAsync(null);
        foreach (var id in secrets.StoredIds) Check(await secrets.GetAsync(id) is null, "A failed save must roll back new secrets.");
        Check(await secrets.GetAsync(saved.ApiKeySecretId) == "test-primary", "A failed save must preserve old keys.");
        Check((await repository.GetAsync(saved.Id))!.BackupApiKeySecretIds.SequenceEqual(saved.BackupApiKeySecretIds), "A failed save must preserve old references.");
        repository.FailSave = false;

        settings.SelectedConnection = await repository.GetAsync(saved.Id);
        settings.ApiKey = "replacement-primary";
        settings.BackupApiKeys[0].ApiKey = "replacement-backup";
        settings.BackupApiKeys[1].RemoveCommand.Execute(null);
        await settings.SaveConnectionCommand.ExecuteAsync(null);
        var replaced = (await repository.GetAsync(saved.Id))!;
        Check(replaced.BackupApiKeySecretIds.Count == 1 && await secrets.GetAsync(replaced.BackupApiKeySecretIds[0]) == "replacement-backup", "Replacement and removal must save correctly.");
        foreach (var id in new[] { saved.ApiKeySecretId }.Concat(saved.BackupApiKeySecretIds))
            Check(await secrets.GetAsync(id) is null, "Replaced and removed secrets must be deleted after saving.");

        var scale = XamlRoot!.RasterizationScale;
        var window = App.Window.AppWindow;
        foreach (var (name, width, height) in new[] { ("desktop", 1200, 1000), ("phone", 393, 820) })
        {
            window.Resize(new Windows.Graphics.SizeInt32
            {
                Width = (int)(width * scale) + window.Size.Width - window.ClientSize.Width,
                Height = (int)(height * scale) + window.Size.Height - window.ClientSize.Height
            });
            await Task.Delay(80);
            UpdateLayout();
            await SaveImage(ShellGrid, Path.Combine(Path.GetDirectoryName(ApplicationPaths.DataDirectory)!, $"connection-keys-{name}.png"));
        }
        await settings.DeleteConnectionCommand.ExecuteAsync(null);
        Check(await repository.GetAsync(saved.Id) is null, "Deleting a connection must delete the profile.");
        foreach (var id in new[] { replaced.ApiKeySecretId }.Concat(replaced.BackupApiKeySecretIds))
            Check(await secrets.GetAsync(id) is null, "Deleting a connection must delete its remaining keys.");
        Console.WriteLine("PASS: backup key editor bindings, encrypted storage, reload, temporary probes, failed-save rollback, replacement and deletion.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class KeyTestSecretStore(ISecretStore inner) : ISecretStore
    {
        public List<string> StoredIds { get; } = [];
        public async Task<string> StoreAsync(string secret, string? secretId = null, CancellationToken cancellationToken = default)
        {
            var id = await inner.StoreAsync(secret, secretId, cancellationToken);
            StoredIds.Add(id);
            return id;
        }
        public Task<string?> GetAsync(string secretId, CancellationToken cancellationToken = default) => inner.GetAsync(secretId, cancellationToken);
        public Task DeleteAsync(string secretId, CancellationToken cancellationToken = default) => inner.DeleteAsync(secretId, cancellationToken);
    }

    private sealed class KeyTestRepository(IConnectionRepository inner) : IConnectionRepository
    {
        public bool FailSave { get; set; }
        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default) => FailSave ? throw new IOException("Simulated save failure") : inner.SaveAsync(profile, cancellationToken);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
    }

    private sealed class KeyTestProvider(ISecretStore secrets) : ILLMProvider, ILLMProviderFactory
    {
        public List<string?> Values { get; } = [];
        public bool Fail { get; set; }
        public string ProviderType => "test";
        public ILLMProvider Create(ConnectionProfile connection) => this;
        public IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default)
        {
            Values.Clear();
            foreach (var id in new[] { connection.ApiKeySecretId }.Concat(connection.BackupApiKeySecretIds))
                Values.Add(await secrets.GetAsync(id, cancellationToken));
            if (Fail) throw new IOException("Simulated probe failure");
            return ["test-model"];
        }
        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default)
        {
            await ListModelsAsync(connection, cancellationToken);
            return new(true, "Test succeeded", TimeSpan.Zero);
        }
    }
}
