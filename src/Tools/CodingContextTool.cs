using System.Text;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class CodingContextProvider : ICodingContextProvider
{
    private const int MaximumInstructionCharacters = 48_000;
    private const int MaximumSkillCharacters = 160_000;
    private const int MaximumMapEntries = 900;
    private const int MaximumNestedInstructionFiles = 96;
    private const int MaximumSkills = 128;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".idea", ".vs", ".vscode-test",
        "bin", "obj", "node_modules", "packages", "artifacts", "dist", "build",
        ".next", ".nuxt", ".cache", ".venv", "venv", "__pycache__", "coverage"
    };

    private static readonly string[] ScopedInstructionNames = ["AGENTS.md", "CLAUDE.md", "GEMINI.md"];

    public async Task<CodingContextSnapshot> InspectAsync(
        string workspacePath,
        string? targetPath = null,
        int repositoryMapDepth = 3,
        CancellationToken cancellationToken = default)
    {
        var workspace = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"Coding workspace does not exist: {workspace}");

        var repositoryRoot = FindRepositoryRoot(workspace);
        var targetDirectory = ResolveTargetDirectory(workspace, repositoryRoot, targetPath);
        var effectivePaths = DiscoverEffectiveInstructionPaths(repositoryRoot, targetDirectory);
        var effectiveInstructions = new List<CodingInstructionFile>();
        foreach (var candidate in effectivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate.Path)) continue;
            var content = await ReadClippedAsync(candidate.Path, MaximumInstructionCharacters, cancellationToken).ConfigureAwait(false);
            effectiveInstructions.Add(new(candidate.Path, candidate.Scope, content, candidate.IsGlobal));
        }

        var effectiveSet = effectiveInstructions.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nestedInstructions = DiscoverNestedInstructions(repositoryRoot)
            .Where(item => !effectiveSet.Contains(item.Path))
            .Take(MaximumNestedInstructionFiles)
            .ToArray();
        var skills = DiscoverSkills(repositoryRoot).Take(MaximumSkills).ToArray();
        var map = BuildRepositoryMap(repositoryRoot, Math.Clamp(repositoryMapDepth, 1, 8));
        return new(workspace, repositoryRoot, effectiveInstructions, nestedInstructions, skills, map);
    }

    public async Task<CodingSkillDocument?> ReadSkillAsync(
        string workspacePath,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return null;
        var workspace = Path.GetFullPath(workspacePath);
        var repositoryRoot = FindRepositoryRoot(workspace);
        var skills = DiscoverSkills(repositoryRoot).Take(MaximumSkills).ToArray();
        var exact = skills.FirstOrDefault(item => item.Id.Equals(skillId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is null)
        {
            var byName = skills.Where(item => item.Name.Equals(skillId.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (byName.Length == 1) exact = byName[0];
        }
        if (exact is null || !File.Exists(exact.Path)) return null;
        var content = await ReadClippedAsync(exact.Path, MaximumSkillCharacters, cancellationToken).ConfigureAwait(false);
        return new(exact, content);
    }

    private static string FindRepositoryRoot(string workspace)
    {
        var current = new DirectoryInfo(workspace);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        return workspace;
    }

    private static string ResolveTargetDirectory(string workspace, string repositoryRoot, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return workspace;
        var candidate = Path.GetFullPath(Path.IsPathRooted(targetPath) ? targetPath : Path.Combine(workspace, targetPath));
        if (File.Exists(candidate)) candidate = Path.GetDirectoryName(candidate) ?? workspace;
        if (!IsWithin(candidate, repositoryRoot)) return workspace;
        return Directory.Exists(candidate) ? candidate : Path.GetDirectoryName(candidate) ?? workspace;
    }

    private static (string Path, string Scope, bool IsGlobal)[] DiscoverEffectiveInstructionPaths(string repositoryRoot, string targetDirectory)
    {
        var result = new List<(string Path, string Scope, bool IsGlobal)>();
        foreach (var path in GlobalInstructionCandidates())
            result.Add((path, "global", true));

        var directories = new List<string> { repositoryRoot };
        if (IsWithin(targetDirectory, repositoryRoot))
        {
            var relative = Path.GetRelativePath(repositoryRoot, targetDirectory);
            if (relative != ".")
            {
                var cursor = repositoryRoot;
                foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    cursor = Path.Combine(cursor, segment);
                    directories.Add(cursor);
                }
            }
        }

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var scope = Path.GetRelativePath(repositoryRoot, directory).Replace('\\', '/');
            if (scope == ".") scope = "repository root";
            foreach (var name in ScopedInstructionNames)
                result.Add((Path.Combine(directory, name), scope, false));
            result.Add((Path.Combine(directory, ".neuroset", "instructions.md"), scope, false));
        }
        result.Add((Path.Combine(repositoryRoot, ".github", "copilot-instructions.md"), "repository root", false));
        return result.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> GlobalInstructionCandidates()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            yield return Path.Combine(profile, ".neuroset", "AGENTS.md");
            yield return Path.Combine(profile, ".codex", "AGENTS.md");
            yield return Path.Combine(profile, ".claude", "CLAUDE.md");
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "Neuroset", "AGENTS.md");
    }

    private static IEnumerable<CodingInstructionReference> DiscoverNestedInstructions(string repositoryRoot)
    {
        foreach (var path in EnumerateFiles(repositoryRoot, MaximumNestedInstructionFiles * 3, 8, fileName =>
                     ScopedInstructionNames.Contains(fileName, StringComparer.OrdinalIgnoreCase) ||
                     fileName.Equals("copilot-instructions.md", StringComparison.OrdinalIgnoreCase) ||
                     fileName.Equals("instructions.md", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(path);
            var parentName = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
            if (fileName.Equals("instructions.md", StringComparison.OrdinalIgnoreCase) && !parentName.Equals(".neuroset", StringComparison.OrdinalIgnoreCase)) continue;
            if (fileName.Equals("copilot-instructions.md", StringComparison.OrdinalIgnoreCase) && !parentName.Equals(".github", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            yield return new(path, Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "repository root");
        }
    }

    private static IEnumerable<CodingSkillDescriptor> DiscoverSkills(string repositoryRoot)
    {
        var roots = new List<(string Path, bool IsGlobal, string Prefix)>
        {
            (Path.Combine(repositoryRoot, ".agents", "skills"), false, "repo:.agents/skills"),
            (Path.Combine(repositoryRoot, ".claude", "skills"), false, "repo:.claude/skills"),
            (Path.Combine(repositoryRoot, ".neuroset", "skills"), false, "repo:.neuroset/skills")
        };
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            roots.Add((Path.Combine(profile, ".agents", "skills"), true, "global:.agents/skills"));
            roots.Add((Path.Combine(profile, ".claude", "skills"), true, "global:.claude/skills"));
            roots.Add((Path.Combine(profile, ".neuroset", "skills"), true, "global:.neuroset/skills"));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            roots.Add((Path.Combine(local, "Neuroset", "Skills"), true, "global:Neuroset/Skills"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) continue;
            foreach (var skillPath in EnumerateFiles(root.Path, MaximumSkills * 2, 5, name => name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            {
                if (!seen.Add(skillPath)) continue;
                var relativeDirectory = Path.GetRelativePath(root.Path, Path.GetDirectoryName(skillPath) ?? root.Path).Replace('\\', '/');
                if (relativeDirectory == ".") relativeDirectory = Path.GetFileName(root.Path);
                var metadata = ReadSkillMetadata(skillPath, relativeDirectory);
                yield return new($"{root.Prefix}/{relativeDirectory}", metadata.Name, metadata.Description, skillPath, root.IsGlobal);
            }
        }
    }

    private static (string Name, string Description) ReadSkillMetadata(string path, string fallbackName)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            var first = reader.ReadLine();
            if (!string.Equals(first?.Trim(), "---", StringComparison.Ordinal))
                return (fallbackName, string.Empty);
            var name = string.Empty;
            var description = string.Empty;
            for (var index = 0; index < 80; index++)
            {
                var line = reader.ReadLine();
                if (line is null || line.Trim() == "---") break;
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"', '\'');
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            }
            return (string.IsNullOrWhiteSpace(name) ? fallbackName : name, description);
        }
        catch (IOException) { return (fallbackName, string.Empty); }
        catch (UnauthorizedAccessException) { return (fallbackName, string.Empty); }
    }

    private static string BuildRepositoryMap(string repositoryRoot, int maximumDepth)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Path.GetFileName(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/");
        var entries = 0;
        AppendDirectory(repositoryRoot, 0);
        if (entries >= MaximumMapEntries) builder.AppendLine("  … repository map truncated …");
        return builder.ToString().TrimEnd();

        void AppendDirectory(string directory, int depth)
        {
            if (depth >= maximumDepth || entries >= MaximumMapEntries) return;
            var children = GetFileSystemEntries(directory)
                .OrderByDescending(Directory.Exists)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var child in children)
            {
                if (entries >= MaximumMapEntries) return;
                var name = Path.GetFileName(child);
                if (Directory.Exists(child))
                {
                    if (IgnoredDirectories.Contains(name) || IsReparsePoint(child)) continue;
                    builder.Append(' ', (depth + 1) * 2).Append(name).AppendLine("/");
                    entries++;
                    AppendDirectory(child, depth + 1);
                }
                else
                {
                    builder.Append(' ', (depth + 1) * 2).AppendLine(name);
                    entries++;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, int maximumFiles, int maximumDepth, Func<string, bool> predicate)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((root, 0));
        var yielded = 0;
        while (stack.Count > 0 && yielded < maximumFiles)
        {
            var (directory, depth) = stack.Pop();
            foreach (var file in GetFiles(directory))
            {
                if (!predicate(Path.GetFileName(file))) continue;
                yield return file;
                yielded++;
                if (yielded >= maximumFiles) yield break;
            }
            if (depth >= maximumDepth) continue;
            foreach (var child in GetDirectories(directory).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(child);
                if (IgnoredDirectories.Contains(name) || IsReparsePoint(child)) continue;
                stack.Push((child, depth + 1));
            }
        }
    }

    private static string[] GetFileSystemEntries(string directory)
    {
        try { return Directory.GetFileSystemEntries(directory); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string[] GetFiles(string directory)
    {
        try { return Directory.GetFiles(directory); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string[] GetDirectories(string directory)
    {
        try { return Directory.GetDirectories(directory); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadClippedAsync(string path, int maximumCharacters, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return content.Length <= maximumCharacters ? content : content[..maximumCharacters] + "\n[truncated by Neuroset]";
    }
}

public sealed class CodingContextTool(ICodingContextProvider provider) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "coding_context",
        "Inspect coding-project guidance and structure. Load applicable AGENTS.md/CLAUDE.md instructions, discover reusable SKILL.md workflows, read one selected skill, or request a compact repository map.",
        JsonDocument.Parse("""{"type":"object","properties":{"action":{"type":"string","enum":["overview","instructions","list_skills","read_skill","repo_map"]},"path":{"type":"string"},"skill_id":{"type":"string"},"depth":{"type":"integer","minimum":1,"maximum":8}},"required":["action"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Safe,
        ["coding.context"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess)
            return new(false, string.Empty, "Coding context is available only in explicitly authorized coding chats.");
        var action = arguments.GetProperty("action").GetString()!;
        if (action.Equals("read_skill", StringComparison.OrdinalIgnoreCase))
        {
            var skillId = arguments.TryGetProperty("skill_id", out var skillElement) ? skillElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(skillId)) return new(false, string.Empty, "skill_id is required for read_skill.");
            var skill = await provider.ReadSkillAsync(context.WorkingDirectory, skillId, cancellationToken).ConfigureAwait(false);
            return skill is null
                ? new(false, string.Empty, $"Coding skill was not found or was ambiguous: {skillId}")
                : Success(skill, context);
        }

        var targetPath = arguments.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var depth = arguments.TryGetProperty("depth", out var depthElement) ? depthElement.GetInt32() : 3;
        var snapshot = await provider.InspectAsync(context.WorkingDirectory, targetPath, depth, cancellationToken).ConfigureAwait(false);
        object output = action switch
        {
            "overview" => snapshot,
            "instructions" => new { snapshot.RepositoryRoot, snapshot.EffectiveInstructions, snapshot.NestedInstructions },
            "list_skills" => new { snapshot.RepositoryRoot, snapshot.Skills },
            "repo_map" => new { snapshot.RepositoryRoot, snapshot.RepositoryMap },
            _ => throw new InvalidOperationException($"Unsupported coding context action: {action}")
        };
        return Success(output, context);
    }

    private static ToolExecutionResult Success<T>(T value, ToolExecutionContext context) => new(
        true,
        JsonSerializer.Serialize(value, DomainJson.Options),
        Metadata: new Dictionary<string, string>
        {
            ["working_directory"] = context.WorkingDirectory,
            ["access"] = "coding"
        });
}
