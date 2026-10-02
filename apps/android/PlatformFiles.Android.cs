using Windows.Storage;
using Windows.Storage.Pickers;

namespace Neuroset.App.Services;

public static partial class PlatformFiles
{
    public static partial async Task<bool> OpenAsync(string path)
    {
        var context = global::Android.App.Application.Context;
        var directory = Path.Combine(context.CacheDir!.AbsolutePath, "exports");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        await using (var source = File.OpenRead(path))
        await using (var output = File.Create(target)) await source.CopyToAsync(output);
        using var file = new Java.IO.File(target);
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".files", file);
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".odp" => "application/vnd.oasis.opendocument.presentation", ".zip" => "application/zip", _ => "application/octet-stream"
        };
        using var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
        intent.SetDataAndType(uri, type);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission | global::Android.Content.ActivityFlags.NewTask);
        try { context.StartActivity(intent); return true; }
        catch (global::Android.Content.ActivityNotFoundException) { return false; }
    }

    public static partial void InitializePicker(object picker) { }

    public static partial async Task<string?> PickWorkspaceAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return null;
        if (Directory.Exists(folder.Path)) return folder.Path;
        // SAF folders cannot be opened by System.IO. Create a private, editable project
        // copy; never claim to have modified the user's original provider folder.
        var root = Path.Combine(Neuroset.Application.ApplicationPaths.DataDirectory, "Workspaces", Guid.NewGuid().ToString("N"), Path.GetFileName(folder.Name));
        Directory.CreateDirectory(root);
        long bytes = 0;
        var count = 0;
        async Task CopyFolderAsync(StorageFolder source, string destination, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("Workspace folders are limited to 32 levels.");
            Directory.CreateDirectory(destination);
            foreach (var item in await source.GetItemsAsync())
            {
                if (++count > 10000) throw new InvalidOperationException("Workspace imports are limited to 10,000 files and folders.");
                var path = Path.Combine(destination, Path.GetFileName(item.Name));
                if (item is StorageFolder child) await CopyFolderAsync(child, path, depth + 1);
                else if (item is StorageFile file)
                {
                    using var input = await file.OpenStreamForReadAsync();
                    await using var output = File.Create(path);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        bytes += read;
                        if (bytes > 256L * 1024 * 1024) throw new InvalidOperationException("Workspace imports are limited to 256 MB.");
                        await output.WriteAsync(buffer.AsMemory(0, read));
                    }
                }
            }
        }
        try { await CopyFolderAsync(folder, root, 0); return root; }
        catch { Directory.Delete(root, recursive: true); throw; }
    }
}
