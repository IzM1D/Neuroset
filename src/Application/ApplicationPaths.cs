namespace Neuroset.Application;

public static class ApplicationPaths
{
    // Existing Windows installations keep exactly the same data and DPAPI file.
    // The override allows isolated QA sessions without touching personal chats.
    public static string DataDirectory { get; } = ResolveDataDirectory(
        Environment.GetEnvironmentVariable("NEUROSET_DATA_DIRECTORY"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string ResolveDataDirectory(string? configuredPath, string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
                throw new ArgumentException("NEUROSET_DATA_DIRECTORY must be an absolute path.", nameof(configuredPath));
            return Path.GetFullPath(configuredPath);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        return Path.Combine(localApplicationData, "Neuroset");
    }
}
