using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed record PythonSandboxOptions(string RootDirectory, string Launcher = "py", string Version = "3")
{
    public static PythonSandboxOptions CreateDefault() => new(Path.Combine(Neuroset.Application.ApplicationPaths.DataDirectory, "Sandbox"));
}

public sealed class PythonSandbox(PythonSandboxOptions options) : IPythonSandbox
{
    internal const int SandboxMemoryLimitMegabytes = 1024;
    private const long SandboxMemoryLimitBytes = (long)SandboxMemoryLimitMegabytes * 1024 * 1024;
    private const string Bootstrap = """
        import os, runpy, sys
        workspace = os.path.realpath(sys.argv[1])
        user_script = os.path.realpath(sys.argv[2])
        readable_roots = (workspace, os.path.realpath(sys.base_prefix), os.path.realpath(sys.prefix))
        def inside(path, root):
            try: return os.path.commonpath((path, root)) == root
            except ValueError: return False
        def audit(event, args):
            if event == 'open' and args:
                raw = args[0]
                if isinstance(raw, (str, bytes, os.PathLike)):
                    path = os.path.realpath(os.fsdecode(raw))
                    mode = str(args[1]) if len(args) > 1 else 'r'
                    writing = any(flag in mode for flag in ('w','a','x','+'))
                    if writing and not inside(path, workspace): raise PermissionError('Writes are limited to the sandbox workspace')
                    if not writing and not any(inside(path, root) for root in readable_roots): raise PermissionError('Reads outside the sandbox are blocked')
            blocked = ('socket.', 'subprocess.', 'os.system', 'os.spawn', 'os.exec', 'ctypes.dlopen', 'winreg.')
            if event.startswith(blocked): raise PermissionError('Operation blocked by Neuroset sandbox: ' + event)
        sys.addaudithook(audit)
        os.chdir(workspace)
        runpy.run_path(user_script, run_name='__main__')
        """;

    public async Task<PythonExecutionResult> ExecuteAsync(string code, TimeSpan timeout, string? workingDirectory = null, bool fullAccess = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code); timeout = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 1, 300));
        var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(options.RootDirectory);
        var root = Path.GetFullPath(options.RootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var runDirectory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(runDirectory);
        var bootstrapPath = Path.Combine(runDirectory, "bootstrap.py"); var scriptPath = Path.Combine(runDirectory, "main.py");
        if (!fullAccess) await File.WriteAllTextAsync(bootstrapPath, Bootstrap, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(scriptPath, code, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var processWorkingDirectory = fullAccess && !string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetFullPath(workingDirectory) : runDirectory;
        Directory.CreateDirectory(processWorkingDirectory);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable, WorkingDirectory = processWorkingDirectory, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }
            };
            if (fullAccess)
            {
                process.StartInfo.ArgumentList.Add(scriptPath);
            }
            else
            {
                process.StartInfo.ArgumentList.Add("-I"); process.StartInfo.ArgumentList.Add(bootstrapPath); process.StartInfo.ArgumentList.Add(runDirectory); process.StartInfo.ArgumentList.Add(scriptPath);
            }
            if (!process.Start()) throw new InvalidOperationException("Python could not be started.");
            using var job = !fullAccess && OperatingSystem.IsWindows() ? WindowsJob.TryCreate(process, SandboxMemoryLimitBytes) : null;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken); var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutSource.CancelAfter(timeout);
            var timedOut = false;
            try { await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { timedOut = true; TryKill(process); }
            catch (OperationCanceledException) { TryKill(process); throw; }
            var stdout = await stdoutTask.ConfigureAwait(false); var stderr = await stderrTask.ConfigureAwait(false);
            return new(timedOut ? -1 : process.ExitCode, stdout, stderr, stopwatch.Elapsed, timedOut);
        }
        finally
        {
            var resolved = Path.GetFullPath(runDirectory);
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    private async Task<string> ResolveExecutableAsync(CancellationToken cancellationToken)
    {
        if (!Path.GetFileNameWithoutExtension(options.Launcher).Equals("py", StringComparison.OrdinalIgnoreCase)) return options.Launcher;
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = options.Launcher, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add($"-{options.Version}"); process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("import sys; print(sys.executable)");
        if (!process.Start()) throw new InvalidOperationException("Python launcher could not be started.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false); var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var path = output.Trim(); if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException($"Python {options.Version} is unavailable. {error}".Trim());
        return path;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    private WindowsJob(SafeFileHandle handle) => _handle = handle;
    public static WindowsJob? TryCreate(Process process, long memoryLimit)
    {
        var handle = NativeMethods.CreateJobObjectW(nint.Zero, null); if (handle.IsInvalid) return null;
        var info = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation { LimitFlags = 0x2000u | 0x100u | 0x8u, ActiveProcessLimit = 1 },
            ProcessMemoryLimit = (nuint)memoryLimit
        };
        var size = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>(); var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!NativeMethods.SetInformationJobObject(handle, 9, pointer, (uint)size) || !NativeMethods.AssignProcessToJobObject(handle, process.SafeHandle)) { handle.Dispose(); return null; }
            return new(handle);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Dispose() => _handle.Dispose();
}

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] internal static partial SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, nint info, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [StructLayout(LayoutKind.Sequential)] internal struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize; public nuint MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct JobObjectExtendedLimitInformation { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit; public nuint JobMemoryLimit; public nuint PeakProcessMemoryUsed; public nuint PeakJobMemoryUsed; }
}

public sealed class PythonExecutionTool(IPythonSandbox sandbox) : ITool
{
    public ToolDescriptor Descriptor { get; } = new("run_python", "Run a named Python program in the chat working directory. Always provide a short, specific title describing what this program computes or verifies. Coding chats explicitly marked full access run without filesystem, network, subprocess or package isolation; normal chats remain sandboxed.", JsonDocument.Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":120},"code":{"type":"string","minLength":1},"timeout_seconds":{"type":"integer","minimum":1,"maximum":300}},"required":["title","code"],"additionalProperties":false}""").RootElement.Clone(), ToolRisk.CodeExecution, ["code.python.execute"]);
    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken=default)
    {
        var title=arguments.GetProperty("title").GetString()!.Trim();
        var result=await sandbox.ExecuteAsync(arguments.GetProperty("code").GetString()!,TimeSpan.FromSeconds(arguments.TryGetProperty("timeout_seconds",out var timeout)?timeout.GetInt32():30),context.WorkingDirectory,context.FullAccess,cancellationToken).ConfigureAwait(false);
        var memoryFailure=ContainsMemoryFailure(result.StandardError);
        var failureKind=memoryFailure?(context.FullAccess?"memory_exhausted":"memory_limit"):result.TimedOut?"timeout":result.ExitCode==0?string.Empty:"runtime_error";
        var memoryLimitMb=context.FullAccess?0:PythonSandbox.SandboxMemoryLimitMegabytes;
        var output=JsonSerializer.Serialize(new{title,result.ExitCode,result.StandardOutput,result.StandardError,duration_ms=result.Duration.TotalMilliseconds,result.TimedOut,failureKind,memoryLimitMb},DomainJson.Options);
        var error=result.TimedOut
            ?"Python execution timed out."
            :memoryFailure
                ?context.FullAccess
                    ?"Python ran out of available memory. Reduce peak memory with sparse, streaming, chunked, bit-packed, or analytical data structures."
                    :$"Python exceeded the sandbox memory limit ({memoryLimitMb} MB). Reduce peak memory with sparse, streaming, chunked, bit-packed, or analytical data structures."
                :result.ExitCode==0?null:$"Python exited with code {result.ExitCode}.";
        return new(result.ExitCode==0&&!result.TimedOut,output,error,new Dictionary<string,string>
        {
            ["title"]=title,
            ["access"]=context.FullAccess?"full":"sandboxed",
            ["working_directory"]=context.WorkingDirectory,
            ["failure_kind"]=failureKind,
            ["memory_limit_mb"]=memoryLimitMb.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private static bool ContainsMemoryFailure(string standardError)=>
        standardError.Contains("MemoryError",StringComparison.OrdinalIgnoreCase)||
        standardError.Contains("Unable to allocate",StringComparison.OrdinalIgnoreCase)||
        standardError.Contains("not enough memory",StringComparison.OrdinalIgnoreCase)||
        standardError.Contains("out of memory",StringComparison.OrdinalIgnoreCase);
}
