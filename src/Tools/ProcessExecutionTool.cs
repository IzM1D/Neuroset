using System.Diagnostics;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class ProcessExecutionTool : ITool
{
    private const int MaximumCapturedCharacters = 240_000;

    public ToolDescriptor Descriptor { get; } = new(
        "run_process",
        "Run an executable directly with an argument array in a full-access coding chat. Use this for git, dotnet, compilers, test runners, package managers, and project scripts. No command shell is inserted automatically.",
        JsonDocument.Parse("""{"type":"object","properties":{"executable":{"type":"string","minLength":1},"arguments":{"type":"array","items":{"type":"string"},"maxItems":256},"working_directory":{"type":"string"},"timeout_seconds":{"type":"integer","minimum":1,"maximum":3600}},"required":["executable"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.CodeExecution,
        ["process.execute"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess)
            return new(false, string.Empty, "Process execution is available only in explicitly authorized full-access coding chats.");

        var executable = arguments.GetProperty("executable").GetString()!.Trim();
        var workingDirectory = ResolveWorkingDirectory(arguments, context.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
            return new(false, string.Empty, $"Working directory does not exist: {workingDirectory}");

        var timeoutSeconds = arguments.TryGetProperty("timeout_seconds", out var timeoutElement)
            ? timeoutElement.GetInt32()
            : 300;
        var argumentList = arguments.TryGetProperty("arguments", out var argumentArray)
            ? argumentArray.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                return new(false, string.Empty, $"Could not start executable: {executable}");

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = Truncate(await standardOutput.ConfigureAwait(false));
            var stderr = Truncate(await standardError.ConfigureAwait(false));
            stopwatch.Stop();
            var exitCode = process.HasExited ? process.ExitCode : (int?)null;
            var output = JsonSerializer.Serialize(new
            {
                executable,
                arguments = argumentList,
                working_directory = workingDirectory,
                exit_code = exitCode,
                stdout,
                stderr,
                timed_out = timedOut,
                duration_ms = stopwatch.Elapsed.TotalMilliseconds
            }, DomainJson.Options);

            return new(
                !timedOut && exitCode == 0,
                output,
                timedOut ? $"Process timed out after {timeoutSeconds} seconds." : exitCode == 0 ? null : $"Process exited with code {exitCode}.",
                new Dictionary<string, string>
                {
                    ["access"] = "full",
                    ["working_directory"] = workingDirectory,
                    ["executable"] = executable
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new(false, string.Empty, $"Could not execute '{executable}': {ex.Message}");
        }
    }

    private static string ResolveWorkingDirectory(JsonElement arguments, string defaultDirectory)
    {
        if (!arguments.TryGetProperty("working_directory", out var directoryElement) || string.IsNullOrWhiteSpace(directoryElement.GetString()))
            return Path.GetFullPath(defaultDirectory);
        var value = directoryElement.GetString()!;
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(defaultDirectory, value));
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaximumCapturedCharacters)
            return value;
        const int tailLength = 80_000;
        var headLength = MaximumCapturedCharacters - tailLength;
        return value[..headLength] + "\n[output truncated]\n" + value[^tailLength..];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
