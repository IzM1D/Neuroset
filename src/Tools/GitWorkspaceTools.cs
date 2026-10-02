using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed record GitWorkspaceOptions(string CheckpointRoot, long MaximumCheckpointBytes = 256L * 1024 * 1024)
{
    public static GitWorkspaceOptions CreateDefault() => new(
        Path.Combine(Neuroset.Application.ApplicationPaths.DataDirectory, "Checkpoints"));
}

public sealed class GitInspectTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "git_inspect",
        "Inspect the selected coding repository with structured, read-only Git operations: status, diff, log, branches, or worktrees. No shell command is accepted.",
        JsonDocument.Parse("""{"type":"object","properties":{"action":{"type":"string","enum":["status","diff","log","branches","worktrees"]},"staged":{"type":"boolean"},"format":{"type":"string","enum":["patch","stat","name_status"]},"path":{"type":"string"},"max_count":{"type":"integer","minimum":1,"maximum":200}},"required":["action"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Safe,
        ["git.read"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess) return new(false, string.Empty, "Git inspection is available only in explicitly authorized coding chats.");
        var repositoryRoot = await GitWorkspace.FindRepositoryRootAsync(context.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        var action = arguments.GetProperty("action").GetString()!;
        var gitArguments = new List<string>();
        switch (action)
        {
            case "status":
                gitArguments.AddRange(["status", "--short", "--branch", "--untracked-files=all"]);
                break;
            case "diff":
                gitArguments.Add("diff");
                gitArguments.Add("--no-ext-diff");
                if (arguments.TryGetProperty("staged", out var stagedElement) && stagedElement.GetBoolean()) gitArguments.Add("--cached");
                var format = arguments.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : "patch";
                if (format == "stat") gitArguments.Add("--stat");
                else if (format == "name_status") gitArguments.Add("--name-status");
                if (arguments.TryGetProperty("path", out var pathElement) && !string.IsNullOrWhiteSpace(pathElement.GetString()))
                {
                    var relative = GitWorkspace.ResolveRepositoryRelativePath(repositoryRoot, context.WorkingDirectory, pathElement.GetString()!);
                    gitArguments.Add("--");
                    gitArguments.Add(relative);
                }
                break;
            case "log":
                var maxCount = arguments.TryGetProperty("max_count", out var countElement) ? countElement.GetInt32() : 30;
                gitArguments.AddRange(["log", "--oneline", "--decorate", $"--max-count={maxCount}"]);
                break;
            case "branches":
                gitArguments.AddRange(["branch", "--all", "--verbose", "--no-abbrev"]);
                break;
            case "worktrees":
                gitArguments.AddRange(["worktree", "list", "--porcelain"]);
                break;
            default:
                return new(false, string.Empty, $"Unsupported Git inspection action: {action}");
        }

        var result = await GitWorkspace.RunGitAsync(repositoryRoot, gitArguments, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        var output = JsonSerializer.Serialize(new
        {
            action,
            repositoryRoot,
            exitCode = result.ExitCode,
            stdout = result.StandardOutput,
            stderr = result.StandardError,
            duration_ms = result.Duration.TotalMilliseconds
        }, DomainJson.Options);
        return new(result.ExitCode == 0, output, result.ExitCode == 0 ? null : $"Git {action} failed with exit code {result.ExitCode}.", new Dictionary<string, string>
        {
            ["repository_root"] = repositoryRoot,
            ["git_action"] = action
        });
    }
}

public sealed class GitWorktreeTool(GitWorkspaceOptions options) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "git_worktree",
        "Create or remove managed Git worktrees for isolated coding tasks. Worktrees are stored under Neuroset local data; removal is refused while the target worktree has uncommitted changes.",
        JsonDocument.Parse("""{"type":"object","properties":{"action":{"type":"string","enum":["create","remove"]},"name":{"type":"string","minLength":1,"maxLength":80,"pattern":"^[A-Za-z0-9._-]+$"},"branch":{"type":"string","maxLength":200},"start_point":{"type":"string","maxLength":200}},"required":["action","name"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Write,
        ["git.worktree"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess) return new(false, string.Empty, "Managed Git worktrees are available only in explicitly authorized coding chats.");
        var repositoryRoot = await GitWorkspace.FindRepositoryRootAsync(context.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        var action = arguments.GetProperty("action").GetString()!;
        var name = arguments.GetProperty("name").GetString()!.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.ContainsAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            return new(false, string.Empty, "Invalid managed worktree name.");
        var localRoot = Path.GetDirectoryName(options.CheckpointRoot) ?? options.CheckpointRoot;
        var managedRoot = Path.Combine(localRoot, "Worktrees", context.ConversationId.ToString("N"));
        Directory.CreateDirectory(managedRoot);
        var destination = Path.GetFullPath(Path.Combine(managedRoot, name));
        if (!destination.StartsWith(Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new(false, string.Empty, "Managed worktree path escaped its storage root.");

        if (action == "create")
        {
            if (Directory.Exists(destination)) return new(false, string.Empty, $"Managed worktree already exists: {name}");
            var branch = arguments.TryGetProperty("branch", out var branchElement) && !string.IsNullOrWhiteSpace(branchElement.GetString())
                ? branchElement.GetString()!.Trim()
                : $"neuroset/{name}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var startPoint = arguments.TryGetProperty("start_point", out var startElement) && !string.IsNullOrWhiteSpace(startElement.GetString()) ? startElement.GetString()!.Trim() : "HEAD";
            if (branch.StartsWith('-') || startPoint.StartsWith('-')) return new(false, string.Empty, "Branch and start point cannot begin with an option prefix.");
            var branchCheck = await GitWorkspace.RunGitAsync(repositoryRoot, ["check-ref-format", "--branch", branch], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (branchCheck.ExitCode != 0) return new(false, branchCheck.StandardOutput, "Invalid worktree branch name: " + branchCheck.StandardError.Trim());
            var startCheck = await GitWorkspace.RunGitAsync(repositoryRoot, ["rev-parse", "--verify", startPoint + "^{commit}"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (startCheck.ExitCode != 0) return new(false, startCheck.StandardOutput, "Worktree start point does not resolve to a commit: " + startCheck.StandardError.Trim());
            var created = await GitWorkspace.RunGitAsync(repositoryRoot, ["worktree", "add", "-b", branch, destination, startPoint], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (created.ExitCode != 0) return new(false, created.StandardOutput, "Could not create managed worktree: " + created.StandardError.Trim());
            return Success(new { action, name, path = destination, branch, startPoint, repositoryRoot });
        }

        if (action == "remove")
        {
            if (!Directory.Exists(destination)) return new(false, string.Empty, $"Managed worktree does not exist: {name}");
            var status = await GitWorkspace.RunGitAsync(destination, ["status", "--porcelain", "--untracked-files=all"], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (status.ExitCode != 0) return new(false, status.StandardOutput, "Could not inspect the managed worktree before removal: " + status.StandardError.Trim());
            if (!string.IsNullOrWhiteSpace(status.StandardOutput)) return new(false, status.StandardOutput, "Managed worktree removal was refused because it contains uncommitted or untracked changes.");
            var removed = await GitWorkspace.RunGitAsync(repositoryRoot, ["worktree", "remove", destination], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (removed.ExitCode != 0) return new(false, removed.StandardOutput, "Could not remove managed worktree: " + removed.StandardError.Trim());
            return Success(new { action, name, path = destination, repositoryRoot });
        }
        return new(false, string.Empty, $"Unsupported managed worktree action: {action}");

        ToolExecutionResult Success<T>(T value) => new(true, JsonSerializer.Serialize(value, DomainJson.Options), Metadata: new Dictionary<string, string>
        {
            ["repository_root"] = repositoryRoot,
            ["worktree_path"] = destination,
            ["worktree_name"] = name
        });
    }
}

public sealed class WorkspaceCheckpointTool(GitWorkspaceOptions options) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "workspace_checkpoint",
        "Create, list, or restore recoverable snapshots of tracked changes and non-ignored untracked files in the selected Git repository. Restore automatically creates a safety checkpoint first.",
        JsonDocument.Parse("""{"type":"object","properties":{"action":{"type":"string","enum":["create","list","restore"]},"name":{"type":"string","maxLength":160},"checkpoint_id":{"type":"string","maxLength":120}},"required":["action"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Write,
        ["git.checkpoint"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess) return new(false, string.Empty, "Workspace checkpoints are available only in explicitly authorized coding chats.");
        var repositoryRoot = await GitWorkspace.FindRepositoryRootAsync(context.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        var checkpointDirectory = Path.Combine(options.CheckpointRoot, context.ConversationId.ToString("N"));
        Directory.CreateDirectory(checkpointDirectory);
        var action = arguments.GetProperty("action").GetString()!;
        try
        {
            return action switch
            {
                "create" => Success(await CreateSnapshotAsync(repositoryRoot, checkpointDirectory, ReadString(arguments, "name"), cancellationToken).ConfigureAwait(false), repositoryRoot),
                "list" => Success(new { repositoryRoot, checkpoints = ReadSnapshotSummaries(checkpointDirectory, repositoryRoot) }, repositoryRoot),
                "restore" => await RestoreAsync(arguments, repositoryRoot, checkpointDirectory, cancellationToken).ConfigureAwait(false),
                _ => new(false, string.Empty, $"Unsupported checkpoint action: {action}")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, string.Empty, exception.Message, new Dictionary<string, string> { ["repository_root"] = repositoryRoot });
        }
    }

    private async Task<ToolExecutionResult> RestoreAsync(JsonElement arguments, string repositoryRoot, string checkpointDirectory, CancellationToken cancellationToken)
    {
        var checkpointId = ReadString(arguments, "checkpoint_id");
        if (string.IsNullOrWhiteSpace(checkpointId)) return new(false, string.Empty, "checkpoint_id is required for restore.");
        var snapshotPath = ResolveCheckpointPath(checkpointDirectory, checkpointId);
        if (!File.Exists(snapshotPath)) return new(false, string.Empty, $"Checkpoint was not found: {checkpointId}");

        var safety = await CreateSnapshotAsync(repositoryRoot, checkpointDirectory, $"automatic safety snapshot before restoring {checkpointId}", cancellationToken).ConfigureAwait(false);
        try
        {
            var restored = await RestoreSnapshotCoreAsync(repositoryRoot, snapshotPath, cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                restoredCheckpoint = restored,
                safetyCheckpoint = safety,
                message = "Checkpoint restored. The automatic safety checkpoint can restore the state that existed immediately before this operation."
            }, repositoryRoot);
        }
        catch (Exception restoreError) when (restoreError is not OperationCanceledException)
        {
            var rollbackMessage = string.Empty;
            try
            {
                await RestoreSnapshotCoreAsync(repositoryRoot, safety.Path, CancellationToken.None).ConfigureAwait(false);
                rollbackMessage = " The pre-restore state was restored automatically from the safety checkpoint.";
            }
            catch (Exception rollbackError)
            {
                rollbackMessage = $" Automatic rollback also failed: {rollbackError.Message}. Safety checkpoint: {safety.Id}.";
            }
            return new(false, string.Empty, $"Checkpoint restore failed: {restoreError.Message}.{rollbackMessage}", new Dictionary<string, string>
            {
                ["repository_root"] = repositoryRoot,
                ["safety_checkpoint"] = safety.Id
            });
        }
    }

    private async Task<CheckpointSummary> CreateSnapshotAsync(string repositoryRoot, string checkpointDirectory, string? name, CancellationToken cancellationToken)
    {
        var headResult = await GitWorkspace.RunGitAsync(repositoryRoot, ["rev-parse", "HEAD"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (headResult.ExitCode != 0) throw new InvalidOperationException("A checkpoint requires a Git repository with at least one commit.");
        var head = headResult.StandardOutput.Trim();
        var patchResult = await GitWorkspace.RunGitAsync(repositoryRoot, ["diff", "--binary", "--full-index", "HEAD", "--", "."], TimeSpan.FromMinutes(2), cancellationToken, maximumCharacters: 70_000_000).ConfigureAwait(false);
        if (patchResult.ExitCode != 0) throw new InvalidOperationException("Could not capture tracked changes: " + patchResult.StandardError.Trim());
        var patchBytes = Encoding.UTF8.GetBytes(patchResult.StandardOutput);
        if (patchBytes.LongLength > options.MaximumCheckpointBytes) throw new InvalidOperationException("Tracked changes exceed the configured checkpoint size limit.");

        var untrackedResult = await GitWorkspace.RunGitAsync(repositoryRoot, ["ls-files", "--others", "--exclude-standard", "-z"], TimeSpan.FromMinutes(1), cancellationToken, maximumCharacters: 8_000_000).ConfigureAwait(false);
        if (untrackedResult.ExitCode != 0) throw new InvalidOperationException("Could not enumerate untracked files: " + untrackedResult.StandardError.Trim());
        var untracked = untrackedResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = new List<CheckpointFile>();
        long totalBytes = patchBytes.LongLength;
        foreach (var relative in untracked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = GitWorkspace.ResolveWithinRepository(repositoryRoot, relative);
            if (!File.Exists(fullPath) || GitWorkspace.IsReparsePoint(fullPath)) continue;
            var length = new FileInfo(fullPath).Length;
            totalBytes = checked(totalBytes + length);
            if (totalBytes > options.MaximumCheckpointBytes) throw new InvalidOperationException("Checkpoint exceeds the configured size limit because of untracked files.");
            files.Add(new(relative.Replace('\\', '/'), length));
        }

        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var path = Path.Combine(checkpointDirectory, id + ".zip");
        var manifest = new CheckpointManifest(1, id, string.IsNullOrWhiteSpace(name) ? "Coding checkpoint" : name.Trim(), DateTimeOffset.UtcNow, repositoryRoot, head, files);
        await using (var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, false, Encoding.UTF8))
        {
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var stream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(stream, manifest, DomainJson.Options, cancellationToken).ConfigureAwait(false);
            var patchEntry = archive.CreateEntry("changes.patch", CompressionLevel.Optimal);
            await using (var stream = patchEntry.Open())
                await stream.WriteAsync(patchBytes, cancellationToken).ConfigureAwait(false);
            foreach (var checkpointFile in files)
            {
                var entry = archive.CreateEntry("untracked/" + checkpointFile.RelativePath, CompressionLevel.Optimal);
                await using var source = new FileStream(GitWorkspace.ResolveWithinRepository(repositoryRoot, checkpointFile.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var destination = entry.Open();
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
        return new(id, manifest.Name, manifest.CreatedAt, head, files.Count, totalBytes, path);
    }

    private static List<CheckpointSummary> ReadSnapshotSummaries(string checkpointDirectory, string repositoryRoot)
    {
        var result = new List<CheckpointSummary>();
        foreach (var path in Directory.GetFiles(checkpointDirectory, "*.zip").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifest = ReadManifest(path);
                if (!Path.GetFullPath(manifest.RepositoryRoot).Equals(Path.GetFullPath(repositoryRoot), StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new(manifest.Id, manifest.Name, manifest.CreatedAt, manifest.Head, manifest.UntrackedFiles.Count, new FileInfo(path).Length, path));
            }
            catch (Exception) { }
        }
        return result;
    }

    private static async Task<CheckpointSummary> RestoreSnapshotCoreAsync(string repositoryRoot, string snapshotPath, CancellationToken cancellationToken)
    {
        CheckpointManifest manifest;
        string patch;
        using (var archive = ZipFile.OpenRead(snapshotPath))
        {
            manifest = ReadManifest(archive);
            if (!Path.GetFullPath(manifest.RepositoryRoot).Equals(Path.GetFullPath(repositoryRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Checkpoint belongs to a different repository.");
            var patchEntry = archive.GetEntry("changes.patch") ?? throw new InvalidDataException("Checkpoint patch is missing.");
            using var reader = new StreamReader(patchEntry.Open(), Encoding.UTF8, true);
            patch = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var currentUntracked = await GitWorkspace.RunGitAsync(repositoryRoot, ["ls-files", "--others", "--exclude-standard", "-z"], TimeSpan.FromMinutes(1), cancellationToken, maximumCharacters: 8_000_000).ConfigureAwait(false);
        if (currentUntracked.ExitCode != 0) throw new InvalidOperationException("Could not enumerate the current untracked files.");
        var reset = await GitWorkspace.RunGitAsync(repositoryRoot, ["reset", "--hard", manifest.Head], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (reset.ExitCode != 0) throw new InvalidOperationException("Git reset failed: " + reset.StandardError.Trim());
        foreach (var relative in currentUntracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = GitWorkspace.ResolveWithinRepository(repositoryRoot, relative);
            if (File.Exists(path) && !GitWorkspace.IsReparsePoint(path)) File.Delete(path);
        }

        if (!string.IsNullOrWhiteSpace(patch))
        {
            var patchPath = Path.Combine(Path.GetDirectoryName(snapshotPath)!, $"restore-{Guid.NewGuid():N}.patch");
            try
            {
                await File.WriteAllTextAsync(patchPath, patch, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                var apply = await GitWorkspace.RunGitAsync(repositoryRoot, ["apply", "--binary", "--whitespace=nowarn", patchPath], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                if (apply.ExitCode != 0) throw new InvalidOperationException("Git could not apply the checkpoint patch: " + apply.StandardError.Trim());
            }
            finally
            {
                try { if (File.Exists(patchPath)) File.Delete(patchPath); }
                catch (Exception) { }
            }
        }

        using (var archive = ZipFile.OpenRead(snapshotPath))
        {
            foreach (var checkpointFile in manifest.UntrackedFiles)
            {
                var entry = archive.GetEntry("untracked/" + checkpointFile.RelativePath) ?? throw new InvalidDataException($"Checkpoint file is missing: {checkpointFile.RelativePath}");
                var destination = GitWorkspace.ResolveWithinRepository(repositoryRoot, checkpointFile.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? repositoryRoot);
                await using var source = entry.Open();
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        return new(manifest.Id, manifest.Name, manifest.CreatedAt, manifest.Head, manifest.UntrackedFiles.Count, new FileInfo(snapshotPath).Length, snapshotPath);
    }

    private static string ResolveCheckpointPath(string directory, string checkpointId)
    {
        var raw = checkpointId.Trim();
        if (raw.ContainsAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) throw new InvalidOperationException("Invalid checkpoint identifier.");
        var safeId = raw.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
        if (safeId.Length == 0 || safeId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("Invalid checkpoint identifier.");
        return Path.Combine(directory, safeId + ".zip");
    }

    private static CheckpointManifest ReadManifest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return ReadManifest(archive);
    }

    private static CheckpointManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Checkpoint manifest is missing.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<CheckpointManifest>(stream, DomainJson.Options) ?? throw new InvalidDataException("Checkpoint manifest is invalid.");
    }

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static ToolExecutionResult Success<T>(T value, string repositoryRoot) => new(
        true,
        JsonSerializer.Serialize(value, DomainJson.Options),
        Metadata: new Dictionary<string, string> { ["repository_root"] = repositoryRoot });

    private sealed record CheckpointManifest(int Version, string Id, string Name, DateTimeOffset CreatedAt, string RepositoryRoot, string Head, IReadOnlyList<CheckpointFile> UntrackedFiles);
    private sealed record CheckpointFile(string RelativePath, long Length);
    private sealed record CheckpointSummary(string Id, string Name, DateTimeOffset CreatedAt, string Head, int UntrackedFileCount, long ApproximateBytes, string Path);
}

internal static class GitWorkspace
{
    private const int DefaultMaximumCharacters = 2_000_000;

    public static async Task<string> FindRepositoryRootAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(Path.GetFullPath(workingDirectory), ["rev-parse", "--show-toplevel"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new InvalidOperationException("The selected coding workspace is not inside a Git repository.");
        return Path.GetFullPath(result.StandardOutput.Trim());
    }

    public static string ResolveRepositoryRelativePath(string repositoryRoot, string workingDirectory, string rawPath)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(workingDirectory, rawPath));
        ResolveWithinRepository(repositoryRoot, Path.GetRelativePath(repositoryRoot, fullPath));
        return Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
    }

    public static string ResolveWithinRepository(string repositoryRoot, string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Checkpoint path escapes the Git repository.");
        return path;
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static async Task<GitProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maximumCharacters = DefaultMaximumCharacters)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException("Could not start Git.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
            throw new TimeoutException($"Git operation exceeded {timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
            throw;
        }
        stopwatch.Stop();
        return new(process.ExitCode, Clip(await standardOutput.ConfigureAwait(false), maximumCharacters), Clip(await standardError.ConfigureAwait(false), maximumCharacters), stopwatch.Elapsed);
    }

    private static string Clip(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters) return value;
        var tail = Math.Min(80_000, maximumCharacters / 4);
        return value[..(maximumCharacters - tail)] + "\n[git output truncated]\n" + value[^tail..];
    }
}

internal sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

internal static class StringExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) => value.IndexOfAny(characters) >= 0;
}
