using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuroset.App.Localization;

namespace Neuroset.App.ViewModels;

public partial class BackupApiKeyViewModel(string? secretId, Action<BackupApiKeyViewModel> remove) : ObservableObject
{
    public string? SecretId { get; } = secretId;
    public string Header => LocalizationService.Get(SecretId is null ? "New backup API key" : "Saved backup API key (leave blank to keep)");
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;

    [RelayCommand]
    private void Remove()
    {
        ApiKey = string.Empty;
        remove(this);
    }
}
