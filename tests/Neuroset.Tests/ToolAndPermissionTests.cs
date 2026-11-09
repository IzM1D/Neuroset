using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Security;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class ToolAndPermissionTests
{
    [Fact]public void Registry_Resolves_Tools_And_Rejects_Duplicates(){var registry=new ToolRegistry([new EchoTool()]);registry.TryGet("ECHO",out var tool).Should().BeTrue();tool.Should().NotBeNull();var action=()=>registry.Register(new EchoTool());action.Should().Throw<InvalidOperationException>();}
    [Fact]public async Task Executor_Validates_Arguments_Before_Execution(){var registry=new ToolRegistry([new EchoTool()]);var executor=new ToolExecutor(registry,new FixedPermissionService(PermissionDecision.AllowOnce),NullLogger<ToolExecutor>.Instance);using var invalid=JsonDocument.Parse("{}");var result=await executor.ExecuteAsync("echo",invalid.RootElement,new(Guid.NewGuid(),"call",".",false,new Dictionary<string,string>()));result.Success.Should().BeFalse();result.Error.Should().Contain("validation");}
    [Fact]public async Task Executor_Honors_Denied_Permission(){var registry=new ToolRegistry([new EchoTool()]);var executor=new ToolExecutor(registry,new FixedPermissionService(PermissionDecision.Deny),NullLogger<ToolExecutor>.Instance);using var args=JsonDocument.Parse("{\"value\":\"x\"}");var result=await executor.ExecuteAsync("echo",args.RootElement,new(Guid.NewGuid(),"call",".",false,new Dictionary<string,string>()));result.Success.Should().BeFalse();result.Error.Should().Contain("denied");}
    [Theory]
    [InlineData("{\"operation\":\"calculate\",\"expression\":\"sqrt(81) + 3^2\"}", "18")]
    [InlineData("{\"operation\":\"calculate\",\"expression\":\"min(5, 2) + 1e3\"}", "1002")]
    [InlineData("{\"operation\":\"convert_units\",\"value\":100,\"from\":\"cm\",\"to\":\"m\"}", "1 m")]
    [InlineData("{\"operation\":\"convert_units\",\"value\":32,\"from\":\"f\",\"to\":\"c\"}", "0 °C")]
    public async Task Widget_Tool_Produces_Offline_Cards(string json,string expected)
    {
        var tool=new WidgetTool(new HttpClient());using var args=JsonDocument.Parse(json);var result=await tool.ExecuteAsync(args.RootElement,new(Guid.NewGuid(),"call",Path.GetTempPath(),false,new Dictionary<string,string>()));result.Success.Should().BeTrue(result.Error);using var output=JsonDocument.Parse(result.Output);output.RootElement.GetProperty("kind").GetString().Should().NotBeNullOrWhiteSpace();output.RootElement.GetProperty("primary").GetString().Should().Be(expected);
    }
    [Fact]public async Task Rich_Table_Tool_Produces_Structured_Native_Table()
    {
        var tool=new RichTableTool();using var args=JsonDocument.Parse("""{"title":"Model methods","columns":["Method","Potential"],"rows":[["Stable LatentMoE","High"],["KDA","High for long context"]],"caption":"Frontier model candidates","style":"accent"}""");var result=await tool.ExecuteAsync(args.RootElement,new(Guid.NewGuid(),"call",Path.GetTempPath(),false,new Dictionary<string,string>()));result.Success.Should().BeTrue(result.Error);using var output=JsonDocument.Parse(result.Output);output.RootElement.GetProperty("kind").GetString().Should().Be("rich_table");output.RootElement.GetProperty("columns").GetArrayLength().Should().Be(2);output.RootElement.GetProperty("rows").GetArrayLength().Should().Be(2);
    }
    [Fact]public async Task Canvas_Tool_Produces_Separate_Python_Code_Block()
    {
        var tool=new CanvasTool();using var args=JsonDocument.Parse("""{"title":"Training script","format":"code","language":"python","description":"Minimal example","content":"print('hello')"}""");var result=await tool.ExecuteAsync(args.RootElement,new(Guid.NewGuid(),"call",Path.GetTempPath(),false,new Dictionary<string,string>()));result.Success.Should().BeTrue(result.Error);using var output=JsonDocument.Parse(result.Output);output.RootElement.GetProperty("kind").GetString().Should().Be("canvas");output.RootElement.GetProperty("format").GetString().Should().Be("code");output.RootElement.GetProperty("language").GetString().Should().Be("python");
    }
    [Fact]public async Task Math_Canvas_Tool_Produces_Typeset_Html_Artifact()
    {
        var root=Path.Combine(Path.GetTempPath(),"NeurosetMathCanvasTests",Guid.NewGuid().ToString("N"));
        try
        {
            var tool=new MathCanvasTool(new HtmlArtifactOptions(root));using var args=JsonDocument.Parse("""{"title":"Objective","latex":"\\max_p \\mathbb{E}[U(p)]"}""");var result=await tool.ExecuteAsync(args.RootElement,new(Guid.NewGuid(),"call",root,false,new Dictionary<string,string>()));result.Success.Should().BeTrue(result.Error);using var output=JsonDocument.Parse(result.Output);output.RootElement.GetProperty("kind").GetString().Should().Be("math_canvas");var path=output.RootElement.GetProperty("path").GetString();File.Exists(path).Should().BeTrue();(await File.ReadAllTextAsync(path!)).Should().Contain("mathjax@3").And.Contain("\\max_p");
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Fact]public async Task Python_Tool_Requires_A_Title_And_Preserves_Exact_Output()
    {
        const string stdout="first line\nαβγ <>&\nlast line";
        const string stderr="warning:\tkeep every character";
        var tool=new PythonExecutionTool(new FixedPythonSandbox(new PythonExecutionResult(0,stdout,stderr,TimeSpan.FromMilliseconds(12.5),false)));
        var registry=new ToolRegistry([tool]);
        var executor=new ToolExecutor(registry,new FixedPermissionService(PermissionDecision.AllowOnce),NullLogger<ToolExecutor>.Instance);
        using var missingTitle=JsonDocument.Parse("""{"code":"print('x')"}""");
        var invalid=await executor.ExecuteAsync("run_python",missingTitle.RootElement,new(Guid.NewGuid(),"call",Path.GetTempPath(),false,new Dictionary<string,string>()));
        invalid.Success.Should().BeFalse();
        using var arguments=JsonDocument.Parse("""{"title":"Verify exact output","code":"print('x')","timeout_seconds":30}""");
        var result=await executor.ExecuteAsync("run_python",arguments.RootElement,new(Guid.NewGuid(),"call",Path.GetTempPath(),false,new Dictionary<string,string>()));
        result.Success.Should().BeTrue(result.Error);
        using var output=JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("title").GetString().Should().Be("Verify exact output");
        output.RootElement.GetProperty("standardOutput").GetString().Should().Be(stdout);
        output.RootElement.GetProperty("standardError").GetString().Should().Be(stderr);
    }
    [Fact]public async Task Python_Tool_Classifies_MemoryError_And_Reports_The_Bounded_Limit()
    {
        var tool=new PythonExecutionTool(new FixedPythonSandbox(new PythonExecutionResult(1,string.Empty,"Traceback\nMemoryError",TimeSpan.FromMilliseconds(8),false)));using var arguments=JsonDocument.Parse("""{"title":"Memory-heavy state table","code":"raise MemoryError()"}""");var result=await tool.ExecuteAsync(arguments.RootElement,new(Guid.NewGuid(),"call",Path.GetTempPath(),false,new Dictionary<string,string>()));result.Success.Should().BeFalse();result.Error.Should().Contain("1024 MB").And.Contain("Reduce peak memory");result.Metadata.Should().Contain("failure_kind","memory_limit").And.Contain("memory_limit_mb","1024");using var output=JsonDocument.Parse(result.Output);output.RootElement.GetProperty("failureKind").GetString().Should().Be("memory_limit");output.RootElement.GetProperty("standardError").GetString().Should().Be("Traceback\nMemoryError");
    }
    [Fact]public async Task PermissionService_Persists_AlwaysAllow(){var repository=new InMemoryPermissionRepository();var prompt=new FixedPermissionPrompt(PermissionDecision.AlwaysAllow);var service=new PermissionService(repository,prompt,new InMemorySettingsRepository());var request=new Neuroset.Application.PermissionRequest(Guid.NewGuid(),"run_python",ToolRisk.CodeExecution,"run","{}");(await service.AuthorizeAsync(request)).Should().Be(PermissionDecision.AlwaysAllow);repository.Rules.Should().ContainSingle(x=>x.Policy==PermissionPolicy.Allow);(await service.AuthorizeAsync(request)).Should().Be(PermissionDecision.AlwaysAllow);prompt.Calls.Should().Be(1);}
    [Fact]public async Task PermissionService_FullAutonomy_Bypasses_Prompts_And_Rules(){var repository=new InMemoryPermissionRepository();var prompt=new FixedPermissionPrompt(PermissionDecision.Deny);var settings=new InMemorySettingsRepository(new AppSettings{FullAutonomyEnabled=true});var service=new PermissionService(repository,prompt,settings);var request=new Neuroset.Application.PermissionRequest(Guid.NewGuid(),"run_python",ToolRisk.CodeExecution,"run","{}");(await service.AuthorizeAsync(request)).Should().Be(PermissionDecision.AlwaysAllow);prompt.Calls.Should().Be(0);repository.Rules.Should().BeEmpty();}
    [Theory][InlineData("web_search",ToolRisk.NetworkRead,PermissionPolicy.Allow)][InlineData("run_python",ToolRisk.CodeExecution,PermissionPolicy.Ask)][InlineData("render_html",ToolRisk.Write,PermissionPolicy.Ask)][InlineData("database",ToolRisk.Write,PermissionPolicy.Ask)]public void Default_Policies_Are_Safe(string tool,ToolRisk risk,PermissionPolicy expected)=>PermissionService.DefaultPolicy(tool,risk).Should().Be(expected);
    [Fact]public async Task Process_Tool_Rejects_Non_Full_Access_Chat()
    {
        var tool=new ProcessExecutionTool();using var args=JsonDocument.Parse("{\"executable\":\"whoami.exe\"}");var result=await tool.ExecuteAsync(args.RootElement,new ToolExecutionContext(Guid.NewGuid(),"call",Path.GetTempPath(),false,new Dictionary<string,string>()));result.Success.Should().BeFalse();result.Error.Should().Contain("full-access");
    }
    [Fact]public async Task Process_Tool_Runs_Direct_Executable_In_Coding_Chat()
    {
        var tool=new ProcessExecutionTool();var executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"whoami.exe");using var args=JsonDocument.Parse(JsonSerializer.Serialize(new{executable,timeout_seconds=10}));var result=await tool.ExecuteAsync(args.RootElement,new ToolExecutionContext(Guid.NewGuid(),"call",Path.GetTempPath(),true,new Dictionary<string,string>()));result.Success.Should().BeTrue(result.Error);result.Output.Should().Contain("\"exit_code\":0");
    }
    [Fact]public async Task Process_Tool_Stops_Process_When_Timeout_Expires()
    {
        var tool=new ProcessExecutionTool();var executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"ping.exe");var processArguments=new List<string>{"-n","10","127.0.0.1"};using var args=JsonDocument.Parse(JsonSerializer.Serialize(new{executable,arguments=processArguments,timeout_seconds=1}));var result=await tool.ExecuteAsync(args.RootElement,new ToolExecutionContext(Guid.NewGuid(),"call",Path.GetTempPath(),true,new Dictionary<string,string>()));result.Success.Should().BeFalse();result.Error.Should().Contain("timed out");
    }
    [Fact]public async Task Html_Artifact_Tool_Creates_A_Standalone_Network_Blocked_File()
    {
        var root=Path.Combine(Path.GetTempPath(),"NeurosetHtmlToolTests",Guid.NewGuid().ToString("N"));
        try
        {
            var tool=new HtmlArtifactTool(new HtmlArtifactOptions(root));
            tool.Descriptor.Name.Should().Be("render_html");
            using var args=JsonDocument.Parse("""{"title":"Interactive demo","file_name":"visualization.html","html":"<main><h1>Visualization</h1><script>document.body.dataset.ready='yes'</script><meta http-equiv='refresh' content='0;url=https://example.com'></main>"}""");
            var result=await tool.ExecuteAsync(args.RootElement,new(Guid.NewGuid(),"call",root,false,new Dictionary<string,string>()));
            result.Success.Should().BeTrue(result.Error);
            result.Metadata.Should().ContainKey("artifact_path");
            var path=result.Metadata!["artifact_path"];
            File.Exists(path).Should().BeTrue();
            var html=await File.ReadAllTextAsync(path);
            html.Should().Contain("Content-Security-Policy").And.Contain("connect-src 'none'").And.Contain("Visualization");
            html.Should().NotContain("http-equiv='refresh'");
            result.Output.Should().Contain("\"inline_preview\":true");
        }
        finally
        {
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    private sealed class FixedPythonSandbox(PythonExecutionResult result):IPythonSandbox
    {
        public Task<PythonExecutionResult> ExecuteAsync(string code,TimeSpan timeout,string? workingDirectory=null,bool fullAccess=false,CancellationToken cancellationToken=default)=>Task.FromResult(result);
    }
}
