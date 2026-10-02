using Windows.Storage.Pickers;

namespace Neuroset.App.Services;

public static partial class PlatformFiles
{
    public static partial async Task<bool> OpenAsync(string path) =>
        await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));

    public static partial void InitializePicker(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));

    public static partial async Task<string?> PickWorkspaceAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
