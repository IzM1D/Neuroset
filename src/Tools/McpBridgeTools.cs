using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class McpDiscoveryTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "mcp_discover",
        "Discover MCP servers configured by the selected coding project without starting them or exposing environment-variable values.",
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.Safe,
        ["mcp.read"]);

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess) return Task.FromResult(new ToolExecutionResult(false, string.Empty, "MCP discovery is available only in explicitly authorized coding chats."));
        var servers = McpConfiguration.Load(context.WorkingDirectory);
        var output = JsonSerializer.Serialize(new
        {
            workspace = Path.GetFullPath(context.WorkingDirectory),
            servers = servers.Select(server => new
            {
                server.Name,
                server.Command,
                server.Arguments,
                server.WorkingDirectory,
                environmentKeys = server.Environment.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                server.SourcePath
            }).ToArray()
        }, DomainJson.Options);
        return Task.FromResult(new ToolExecutionResult(true, output, Metadata: new Dictionary<string, string>
        {
            ["server_count"] = servers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["working_directory"] = Path.GetFullPath(context.WorkingDirectory)
        }));
    }
}

public sealed class McpCallTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "mcp_call",
        "Start one configured project MCP server, list its tools, or call one MCP tool through JSON-RPC stdio. The command comes only from .mcp.json or .neuroset/mcp.json; arbitrary commands are not accepted.",
        JsonDocument.Parse("""{"type":"object","properties":{"server":{"type":"string","minLength":1,"maxLength":120},"action":{"type":"string","enum":["list_tools","call_tool"]},"tool_name":{"type":"string","maxLength":240},"arguments":{"type":"object"},"timeout_seconds":{"type":"integer","minimum":1,"maximum":600}},"required":["server","action"],"additionalProperties":false}""").RootElement.Clone(),
        ToolRisk.CodeExecution,
        ["mcp.execute"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.FullAccess) return new(false, string.Empty, "MCP calls are available only in explicitly authorized coding chats.");
        var serverName = arguments.GetProperty("server").GetString()!.Trim();
        var action = arguments.GetProperty("action").GetString()!;
        var server = McpConfiguration.Load(context.WorkingDirectory).FirstOrDefault(item => item.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (server is null) return new(false, string.Empty, $"Configured MCP server was not found: {serverName}");
        var requestedToolName = arguments.TryGetProperty("tool_name", out var toolNameElement) && toolNameElement.ValueKind == JsonValueKind.String ? toolNameElement.GetString() : null;
        if (action == "call_tool" && string.IsNullOrWhiteSpace(requestedToolName))
            return new(false, string.Empty, "tool_name is required for call_tool.");
        var timeoutSeconds = arguments.TryGetProperty("timeout_seconds", out var timeoutElement) ? timeoutElement.GetInt32() : 120;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var session = await McpStdioSession.StartAsync(server, context.WorkingDirectory, timeout.Token).ConfigureAwait(false);
            await session.InitializeAsync(timeout.Token).ConfigureAwait(false);
            JsonElement result;
            if (action == "list_tools")
            {
                result = await session.RequestAsync("tools/list", new { }, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                var toolName = requestedToolName!.Trim();
                var toolArguments = arguments.TryGetProperty("arguments", out var toolArgumentsElement) && toolArgumentsElement.ValueKind == JsonValueKind.Object
                    ? toolArgumentsElement.Clone()
                    : JsonSerializer.SerializeToElement(new { }, DomainJson.Options);
                result = await session.RequestAsync("tools/call", new { name = toolName, arguments = toolArguments }, timeout.Token).ConfigureAwait(false);
            }
            stopwatch.Stop();
            var output = JsonSerializer.Serialize(new
            {
                server = server.Name,
                action,
                toolName = action == "call_tool" ? requestedToolName : null,
                result,
                stderr = session.StandardError,
                duration_ms = stopwatch.Elapsed.TotalMilliseconds
            }, DomainJson.Options);
            if (output.Length > 4_000_000) output = output[..3_900_000] + "\n[full MCP result exceeded the report limit]";
            return new(true, output, Metadata: new Dictionary<string, string>
            {
                ["server"] = server.Name,
                ["action"] = action,
                ["duration_ms"] = stopwatch.Elapsed.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, string.Empty, $"MCP server '{server.Name}' exceeded the {timeoutSeconds}-second timeout.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, string.Empty, $"MCP server '{server.Name}' failed: {exception.Message}");
        }
    }
}

internal sealed record McpServerConfiguration(
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    string SourcePath);

internal static class McpConfiguration
{
    public static IReadOnlyList<McpServerConfiguration> Load(string workspacePath)
    {
        var workspace = Path.GetFullPath(workspacePath);
        var configurations = new Dictionary<string, McpServerConfiguration>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { Path.Combine(workspace, ".mcp.json"), Path.Combine(workspace, ".neuroset", "mcp.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!TryGetProperty(root, "mcpServers", out var servers) && !TryGetProperty(root, "servers", out servers)) continue;
                if (servers.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in servers.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object) continue;
                    if (TryGetProperty(property.Value, "enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
                    var command = GetString(property.Value, "command");
                    if (string.IsNullOrWhiteSpace(command)) continue;
                    var arguments = TryGetProperty(property.Value, "args", out var args) && args.ValueKind == JsonValueKind.Array
                        ? args.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).ToArray()
                        : [];
                    var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (TryGetProperty(property.Value, "env", out var env) && env.ValueKind == JsonValueKind.Object)
                        foreach (var variable in env.EnumerateObject())
                            if (variable.Value.ValueKind == JsonValueKind.String) environment[variable.Name] = variable.Value.GetString() ?? string.Empty;
                    var configuredDirectory = GetString(property.Value, "workingDirectory");
                    if (string.IsNullOrWhiteSpace(configuredDirectory)) configuredDirectory = GetString(property.Value, "cwd");
                    var workingDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
                        ? workspace
                        : ResolveWithinWorkspace(workspace, configuredDirectory);
                    configurations[property.Name] = new(property.Name, command.Trim(), arguments, environment, workingDirectory, path);
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return configurations.Values.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolveWithinWorkspace(string workspace, string value)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(workspace, value));
        var root = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("MCP workingDirectory must stay inside the selected coding workspace.");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"MCP working directory does not exist: {path}");
        return path;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}

internal sealed class McpStdioSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly Task<string> _standardErrorTask;
    private readonly string _workspace;
    private long _requestId;
    private string? _standardError;

    private McpStdioSession(Process process, string workspace)
    {
        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _standardErrorTask = process.StandardError.ReadToEndAsync();
        _workspace = workspace;
    }

    public string StandardError => _standardError ?? (_standardErrorTask.IsCompletedSuccessfully ? _standardErrorTask.Result : string.Empty);

    public static Task<McpStdioSession> StartAsync(McpServerConfiguration configuration, string workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = configuration.Command,
            WorkingDirectory = configuration.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in configuration.Arguments) start.ArgumentList.Add(argument);
        RestrictEnvironment(start.Environment);
        foreach (var variable in configuration.Environment) start.Environment[variable.Key] = variable.Value;
        var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"Could not start MCP command: {configuration.Command}");
        return Task.FromResult(new McpStdioSession(process, workspace));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { roots = new { listChanged = false } },
            clientInfo = new { name = "Neuroset", version = "1.0.0" }
        }, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MCP initialize returned an invalid result.");
        await WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await _output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                await CaptureErrorAsync().ConfigureAwait(false);
                throw new EndOfStreamException($"MCP server exited before replying. {StandardError}".Trim());
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument message;
            try { message = JsonDocument.Parse(line); }
            catch (JsonException) { throw new InvalidDataException("MCP server wrote non-JSON data to stdout: " + Clip(line, 1000)); }
            using (message)
            {
                var root = message.RootElement;
                if (TryReadId(root, out var responseId) && responseId == id)
                {
                    if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException("MCP JSON-RPC error: " + error.GetRawText());
                    if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException("MCP response contains neither result nor error.");
                    return result.Clone();
                }
                if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String && root.TryGetProperty("id", out var serverRequestId))
                    await ReplyToServerRequestAsync(serverRequestId.Clone(), methodElement.GetString()!, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReplyToServerRequestAsync(JsonElement id, string method, CancellationToken cancellationToken)
    {
        if (method == "ping")
        {
            await WriteAsync(new { jsonrpc = "2.0", id, result = new { } }, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "roots/list")
        {
            var uri = new Uri(Path.GetFullPath(_workspace) + Path.DirectorySeparatorChar).AbsoluteUri;
            await WriteAsync(new { jsonrpc = "2.0", id, result = new { roots = new[] { new { uri, name = Path.GetFileName(_workspace) } } } }, cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Neuroset does not provide the MCP client method '{method}'." } }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, DomainJson.Options);
        await _input.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureErrorAsync()
    {
        if (_standardError is not null) return;
        try { _standardError = Clip(await _standardErrorTask.ConfigureAwait(false), 120_000); }
        catch (Exception) { _standardError = string.Empty; }
    }

    private static bool TryReadId(JsonElement element, out long id)
    {
        id = 0;
        if (!element.TryGetProperty("id", out var idElement)) return false;
        if (idElement.ValueKind == JsonValueKind.Number) return idElement.TryGetInt64(out id);
        return idElement.ValueKind == JsonValueKind.String && long.TryParse(idElement.GetString(), out id);
    }

    private static void RestrictEnvironment(IDictionary<string, string?> environment)
    {
        var preserved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA", "COMSPEC" })
            if (environment.TryGetValue(key, out var value)) preserved[key] = value;
        environment.Clear();
        foreach (var item in preserved) environment[item.Key] = item.Value;
    }

    private static string Clip(string value, int maximumCharacters) => value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "\n[truncated]";

    public async ValueTask DisposeAsync()
    {
        try { _input.Close(); } catch (Exception) { }
        try { if (!_process.HasExited) _process.Kill(true); } catch (Exception) { }
        try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        await CaptureErrorAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
