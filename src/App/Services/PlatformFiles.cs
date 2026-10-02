using Windows.Storage;

namespace Neuroset.App.Services;

public static partial class PlatformFiles
{
    public static partial void InitializePicker(object picker);
    public static partial Task<string?> PickWorkspaceAsync();
    public static partial Task<bool> OpenAsync(string path);

    public static async Task<bool> ExportWorkspaceAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("The workspace must be an existing directory, not a filesystem link.");
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + ".zip" };
        picker.FileTypeChoices.Add("ZIP", [".zip"]);
        InitializePicker(picker);
        if (await picker.PickSaveFileAsync() is not { } target) return false;
        var temporary = Path.Combine(Path.GetTempPath(), "Neuroset-workspace-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var output = File.Create(temporary))
            using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create))
            {
                long total = 0;
                var count = 0;
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = System.IO.FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (var path in Directory.EnumerateFiles(root, "*", options))
                {
                    if (++count > 10000) throw new IOException("Workspace exports are limited to 10,000 files.");
                    total += new FileInfo(path).Length;
                    if (total > 256L * 1024 * 1024) throw new IOException("Workspace exports are limited to 256 MB.");
                    var name = Path.GetRelativePath(root, path).Replace('\\', '/');
                    await using var input = File.OpenRead(path);
                    using var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest).Open();
                    await input.CopyToAsync(entry);
                }
            }
            await CopyToAsync(await StorageFile.GetFileFromPathAsync(temporary), target);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Android's document picker returns content URIs. Read through StorageFile instead
    // of assuming Path is a path that System.IO can open (also handles cloud providers).
    public static async Task ImportAsync(IEnumerable<StorageFile> files, Func<IEnumerable<string>, Task> import)
    {
        var staging = Path.Combine(Path.GetTempPath(), "Neuroset-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var paths = new List<string>();
            foreach (var file in files)
            {
                var directory = Path.Combine(staging, paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, Path.GetFileName(file.Name));
                using var source = await file.OpenStreamForReadAsync();
                await using var destination = File.Create(path);
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer)) != 0)
                {
                    total += count;
                    if (total > 100L * 1024 * 1024) throw new InvalidOperationException("Attachments are limited to 100 MB.");
                    await destination.WriteAsync(buffer.AsMemory(0, count));
                }
                paths.Add(path);
            }
            await import(paths);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    public static async Task CopyToAsync(StorageFile source, StorageFile target)
    {
        if (source.Path.Equals(target.Path, StringComparison.Ordinal)) return;
        using var input = await source.OpenStreamForReadAsync();
        using var output = await target.OpenStreamForWriteAsync();
        output.SetLength(0);
        await input.CopyToAsync(output);
        await output.FlushAsync();
    }
}
