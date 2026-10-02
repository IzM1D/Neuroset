using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class CodingAgentParityTests
{
    [Fact]
    public void Semantic_Coding_Markup_Uses_Only_The_Fixed_Palette()
    {
        var segments=CodingSemanticMarkup.Parse("Open [[cyan|src/App.xaml]] then [[green|build → test → review]] and [[pink|leave this literal]].");
        segments.Should().Contain(item=>item.Text=="src/App.xaml"&&item.Color=="cyan");
        segments.Should().Contain(item=>item.Text=="build → test → review"&&item.Color=="green");
        CodingSemanticMarkup.Strip("[[purple|decision]]: [[red|failed]]").Should().Be("decision: failed");
        CodingSemanticMarkup.Strip("[[pink|unsupported]]").Should().Be("[[pink|unsupported]]");
    }

    [Fact]
    public async Task Coding_Context_Loads_Scoped_Instructions_Skills_And_A_Compact_Map()
    {
        var root=CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root,".git"));
            Directory.CreateDirectory(Path.Combine(root,"src","feature"));
            Directory.CreateDirectory(Path.Combine(root,"docs"));
            Directory.CreateDirectory(Path.Combine(root,"bin"));
            Directory.CreateDirectory(Path.Combine(root,".agents","skills","review"));
            await File.WriteAllTextAsync(Path.Combine(root,"AGENTS.md"),"Always run focused tests.");
            await File.WriteAllTextAsync(Path.Combine(root,"src","CLAUDE.md"),"Use the source conventions.");
            await File.WriteAllTextAsync(Path.Combine(root,"docs","AGENTS.md"),"Documentation scope.");
            await File.WriteAllTextAsync(Path.Combine(root,"src","feature","Feature.cs"),"class Feature {}");
            await File.WriteAllTextAsync(Path.Combine(root,"bin","ignored.dll"),"ignored");
            await File.WriteAllTextAsync(Path.Combine(root,".agents","skills","review","SKILL.md"),"---\nname: focused-review\ndescription: Review a risky patch\n---\nRead the diff.");

            var provider=new CodingContextProvider();
            var snapshot=await provider.InspectAsync(root,Path.Combine("src","feature","Feature.cs"),4);
            snapshot.RepositoryRoot.Should().Be(Path.GetFullPath(root));
            snapshot.EffectiveInstructions.Should().Contain(item=>item.Path.EndsWith("AGENTS.md")&&item.Content.Contains("focused tests"));
            snapshot.EffectiveInstructions.Should().Contain(item=>item.Path.EndsWith("CLAUDE.md")&&item.Content.Contains("source conventions"));
            snapshot.NestedInstructions.Should().Contain(item=>item.Path.EndsWith(Path.Combine("docs","AGENTS.md")));
            snapshot.Skills.Should().ContainSingle(item=>item.Name=="focused-review"&&item.Description=="Review a risky patch");
            snapshot.RepositoryMap.Should().Contain("Feature.cs").And.NotContain("ignored.dll");
            var skill=await provider.ReadSkillAsync(root,"focused-review");
            skill.Should().NotBeNull();
            skill!.Content.Should().Contain("Read the diff");
        }
        finally{DeleteTemporaryDirectory(root);}
    }

    [Fact]
    public async Task Atomic_Workspace_Patch_Validates_All_Changes_Before_Writing()
    {
        var root=CreateTemporaryDirectory();
        try
        {
            var first=Path.Combine(root,"first.txt");
            var second=Path.Combine(root,"second.txt");
            await File.WriteAllTextAsync(first,"alpha beta alpha");
            await File.WriteAllTextAsync(second,"stable");
            var tool=new WorkspacePatchTool();
            using var invalid=JsonDocument.Parse("""{"title":"Atomic edit","changes":[{"path":"first.txt","operation":"replace","old_text":"alpha","new_text":"omega","expected_occurrences":2},{"path":"second.txt","operation":"replace","old_text":"missing","new_text":"changed","expected_occurrences":1}]}""");
            var failed=await tool.ExecuteAsync(invalid.RootElement,new(Guid.NewGuid(),"patch",root,true,new Dictionary<string,string>()));
            failed.Success.Should().BeFalse();
            (await File.ReadAllTextAsync(first)).Should().Be("alpha beta alpha","validation happens before any file is committed");
            (await File.ReadAllTextAsync(second)).Should().Be("stable");

            using var valid=JsonDocument.Parse("""{"title":"Atomic edit","changes":[{"path":"first.txt","operation":"replace","old_text":"alpha","new_text":"omega","expected_occurrences":2},{"path":"created.txt","operation":"create","content":"new file"}]}""");
            var completed=await tool.ExecuteAsync(valid.RootElement,new(Guid.NewGuid(),"patch",root,true,new Dictionary<string,string>()));
            completed.Success.Should().BeTrue(completed.Error);
            (await File.ReadAllTextAsync(first)).Should().Be("omega beta omega");
            (await File.ReadAllTextAsync(Path.Combine(root,"created.txt"))).Should().Be("new file");
            completed.Output.Should().Contain("\"changedFileCount\":2").And.Contain("\"replacementCount\":2");
        }
        finally{DeleteTemporaryDirectory(root);}
    }

    [Fact]
    public async Task Mcp_Project_Configuration_Is_Discovered_Without_Exposing_Environment_Values()
    {
        var root=CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,".mcp.json"),"""{"mcpServers":{"demo":{"command":"demo.exe","args":["--stdio"],"env":{"DEMO_TOKEN":"super-secret-value"}}}}""");
            var tool=new McpDiscoveryTool();
            using var arguments=JsonDocument.Parse("{}");
            var result=await tool.ExecuteAsync(arguments.RootElement,new(Guid.NewGuid(),"mcp",root,true,new Dictionary<string,string>()));
            result.Success.Should().BeTrue(result.Error);
            result.Output.Should().Contain("DEMO_TOKEN").And.NotContain("super-secret-value");
        }
        finally{DeleteTemporaryDirectory(root);}
    }

    [Fact]
    public async Task Mcp_Bridge_Initializes_Lists_And_Calls_A_Stdio_Server()
    {
        var root=CreateTemporaryDirectory();
        try
        {
            var powershell=OperatingSystem.IsWindows()
                ?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")
                :"pwsh";
            if(OperatingSystem.IsWindows())File.Exists(powershell).Should().BeTrue();
            var script=Path.Combine(root,"mcp-server.ps1");
            await File.WriteAllTextAsync(script,"""
                $ErrorActionPreference = 'Stop'
                while (($line = [Console]::In.ReadLine()) -ne $null) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $message = $line | ConvertFrom-Json
                    $response = $null
                    if ($message.method -eq 'initialize') {
                        $response = @{ jsonrpc='2.0'; id=$message.id; result=@{ protocolVersion='2025-06-18'; capabilities=@{ tools=@{} }; serverInfo=@{ name='test'; version='1.0' } } }
                    } elseif ($message.method -eq 'tools/list') {
                        $response = @{ jsonrpc='2.0'; id=$message.id; result=@{ tools=@(@{ name='echo'; description='Echo'; inputSchema=@{ type='object' } }) } }
                    } elseif ($message.method -eq 'tools/call') {
                        $response = @{ jsonrpc='2.0'; id=$message.id; result=@{ content=@(@{ type='text'; text=('echo:' + $message.params.arguments.value) }); isError=$false } }
                    }
                    if ($null -ne $response) {
                        [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 20))
                        [Console]::Out.Flush()
                    }
                }
                """);
            var configuration=new
            {
                mcpServers=new
                {
                    test=new
                    {
                        command=powershell,
                        args=new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script}
                    }
                }
            };
            await File.WriteAllTextAsync(Path.Combine(root,".mcp.json"),JsonSerializer.Serialize(configuration));
            var tool=new McpCallTool();
            using var listArguments=JsonDocument.Parse("""{"server":"test","action":"list_tools","timeout_seconds":20}""");
            var listed=await tool.ExecuteAsync(listArguments.RootElement,new(Guid.NewGuid(),"mcp-list",root,true,new Dictionary<string,string>()));
            listed.Success.Should().BeTrue(listed.Error);
            listed.Output.Should().Contain("\"name\":\"echo\"");
            using var callArguments=JsonDocument.Parse("""{"server":"test","action":"call_tool","tool_name":"echo","arguments":{"value":"hello"},"timeout_seconds":20}""");
            var called=await tool.ExecuteAsync(callArguments.RootElement,new(Guid.NewGuid(),"mcp-call",root,true,new Dictionary<string,string>()));
            called.Success.Should().BeTrue(called.Error);
            called.Output.Should().Contain("echo:hello");
        }
        finally{DeleteTemporaryDirectory(root);}
    }

    [Fact]
    public async Task Git_Checkpoint_Restores_Tracked_And_Untracked_Work_And_Creates_A_Safety_Snapshot()
    {
        var root=CreateTemporaryDirectory();
        var checkpoints=CreateTemporaryDirectory();
        try
        {
            RunGit(root,"init");
            RunGit(root,"config","user.email","tests@neuroset.local");
            RunGit(root,"config","user.name","Neuroset Tests");
            await File.WriteAllTextAsync(Path.Combine(root,"tracked.txt"),"baseline");
            RunGit(root,"add","tracked.txt");
            RunGit(root,"commit","-m","baseline");
            await File.WriteAllTextAsync(Path.Combine(root,"tracked.txt"),"checkpoint tracked state");
            await File.WriteAllTextAsync(Path.Combine(root,"note.txt"),"checkpoint untracked state");

            var conversationId=Guid.NewGuid();
            var tool=new WorkspaceCheckpointTool(new GitWorkspaceOptions(checkpoints));
            using var create=JsonDocument.Parse("""{"action":"create","name":"before refactor"}""");
            var created=await tool.ExecuteAsync(create.RootElement,new(conversationId,"checkpoint",root,true,new Dictionary<string,string>()));
            created.Success.Should().BeTrue(created.Error);
            using var createdJson=JsonDocument.Parse(created.Output);
            var checkpointId=createdJson.RootElement.GetProperty("id").GetString();
            checkpointId.Should().NotBeNullOrWhiteSpace();

            await File.WriteAllTextAsync(Path.Combine(root,"tracked.txt"),"later tracked state");
            await File.WriteAllTextAsync(Path.Combine(root,"note.txt"),"later untracked state");
            await File.WriteAllTextAsync(Path.Combine(root,"extra.txt"),"remove me during restore");
            using var restore=JsonDocument.Parse(JsonSerializer.Serialize(new{action="restore",checkpoint_id=checkpointId}));
            var restored=await tool.ExecuteAsync(restore.RootElement,new(conversationId,"restore",root,true,new Dictionary<string,string>()));
            restored.Success.Should().BeTrue(restored.Error);
            (await File.ReadAllTextAsync(Path.Combine(root,"tracked.txt"))).Should().Be("checkpoint tracked state");
            (await File.ReadAllTextAsync(Path.Combine(root,"note.txt"))).Should().Be("checkpoint untracked state");
            File.Exists(Path.Combine(root,"extra.txt")).Should().BeFalse();
            restored.Output.Should().Contain("safetyCheckpoint");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
            DeleteTemporaryDirectory(checkpoints);
        }
    }

    [Fact]
    public async Task Coding_Runtime_Preloads_Project_Guidance_And_Advertises_Delegation()
    {
        var root=CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root,".git"));
            await File.WriteAllTextAsync(Path.Combine(root,"AGENTS.md"),"PROJECT_GUIDANCE_MARKER");
            var provider=new CaptureFinalProvider();
            var conversations=new InMemoryConversationRepository();
            var registry=new ToolRegistry([new TestNamedTool("coding_context")]);
            var runtime=new AgentRuntime(new FixedProviderFactory(provider),registry,new ToolExecutor(registry,new FixedPermissionService(PermissionDecision.AllowOnce),NullLogger<ToolExecutor>.Instance),conversations,new InMemoryMemoryStore(),new InMemorySettingsRepository(new AppSettings{LongTermMemoryEnabled=false}),NullLogger<AgentRuntime>.Instance,new CodingContextProvider());
            var conversation=new Conversation{Mode=ConversationMode.Coding,FullAccessEnabled=true,WorkspacePath=root};
            var user=new ChatMessage{ConversationId=conversation.Id,Role=MessageRole.User,Content="inspect"};
            await foreach(var _ in runtime.RunAsync(new(conversation,[user],new ConnectionProfile{Name="x",BaseUrl="https://example.test/v1",Model="m"},new AgentRunOptions(Reasoning:ReasoningPreference.High)))){}
            provider.Request.Should().NotBeNull();
            provider.Request!.Messages.Should().Contain(message=>message.Role==MessageRole.System&&message.Content.Contains("PROJECT_GUIDANCE_MARKER"));
            provider.Request.Tools.Select(tool=>tool.Name).Should().Contain(["coding_context","request_work_budget","delegate_coding_task"]);
        }
        finally{DeleteTemporaryDirectory(root);}
    }

    [Fact]
    public async Task Long_Working_History_Is_Compacted_Into_A_Deterministic_Ledger()
    {
        var provider=new CaptureFinalProvider();
        var conversations=new InMemoryConversationRepository();
        var registry=new ToolRegistry([]);
        var runtime=new AgentRuntime(new FixedProviderFactory(provider),registry,new ToolExecutor(registry,new FixedPermissionService(PermissionDecision.AllowOnce),NullLogger<ToolExecutor>.Instance),conversations,new InMemoryMemoryStore(),new InMemorySettingsRepository(new AppSettings{LongTermMemoryEnabled=false}),NullLogger<AgentRuntime>.Instance);
        var conversation=new Conversation();
        var messages=new List<ChatMessage>();
        for(var index=0;index<30;index++)messages.Add(new ChatMessage{ConversationId=conversation.Id,Role=MessageRole.Assistant,Content=$"old-{index}: "+new string((char)('a'+index%20),12_000)});
        messages.Add(new ChatMessage{ConversationId=conversation.Id,Role=MessageRole.User,Content="current task"});
        await foreach(var _ in runtime.RunAsync(new(conversation,messages,new ConnectionProfile{Name="x",BaseUrl="https://example.test/v1",Model="m"},new AgentRunOptions(Reasoning:ReasoningPreference.High)))){}
        provider.Request.Should().NotBeNull();
        provider.Request!.Messages.Should().Contain(message=>message.Role==MessageRole.System&&message.Content.StartsWith("Compacted coding work ledger",StringComparison.Ordinal));
        provider.Request.Messages.Should().Contain(message=>message.Role==MessageRole.User&&message.Content=="current task");
        provider.Request.Messages.Sum(message=>message.Content.Length).Should().BeLessThan(190_000);
    }

    [Fact]
    public async Task Main_Agent_Can_Delegate_A_Focused_Read_Only_Coding_Pass()
    {
        var root=CreateTemporaryDirectory();
        try
        {
            var provider=new DelegationProvider();
            var conversations=new InMemoryConversationRepository();
            var registry=new ToolRegistry([new TestNamedTool("workspace_files")]);
            var runtime=new AgentRuntime(new FixedProviderFactory(provider),registry,new ToolExecutor(registry,new FixedPermissionService(PermissionDecision.AllowOnce),NullLogger<ToolExecutor>.Instance),conversations,new InMemoryMemoryStore(),new InMemorySettingsRepository(new AppSettings{LongTermMemoryEnabled=false}),NullLogger<AgentRuntime>.Instance);
            var conversation=new Conversation{Mode=ConversationMode.Coding,FullAccessEnabled=true,WorkspacePath=root};
            var user=new ChatMessage{ConversationId=conversation.Id,Role=MessageRole.User,Content="review architecture"};
            var events=new List<ModelEvent>();
            await foreach(var item in runtime.RunAsync(new(conversation,[user],new ConnectionProfile{Name="x",BaseUrl="https://example.test/v1",Model="m"},new AgentRunOptions(Reasoning:ReasoningPreference.High))))events.Add(item);
            var delegated=events.OfType<ToolExecutionCompletedEvent>().Single(item=>item.Name=="delegate_coding_task");
            delegated.Succeeded.Should().BeTrue(delegated.Error);
            delegated.Output.Should().Contain("focused evidence").And.Contain("\"role\":\"reviewer\"");
            events.OfType<TextDeltaEvent>().Should().ContainSingle(item=>item.Delta=="main final");
        }
        finally{DeleteTemporaryDirectory(root);}
    }

    private static string CreateTemporaryDirectory()
    {
        var path=Path.Combine(Path.GetTempPath(),"NeurosetCodingAgentTests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try{if(Directory.Exists(path))Directory.Delete(path,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }

    private static void RunGit(string workingDirectory,params string[] arguments)
    {
        var start=new ProcessStartInfo{FileName="git",WorkingDirectory=workingDirectory,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new InvalidOperationException("Could not start git.");
        var stdout=process.StandardOutput.ReadToEnd();
        var stderr=process.StandardError.ReadToEnd();
        process.WaitForExit();
        if(process.ExitCode!=0)throw new InvalidOperationException($"git {string.Join(' ',arguments)} failed: {stdout} {stderr}");
    }

    private sealed class FixedProviderFactory(ILLMProvider provider):ILLMProviderFactory{public ILLMProvider Create(ConnectionProfile connection)=>provider;}

    private sealed class CaptureFinalProvider:ILLMProvider
    {
        public string ProviderType=>"test";
        public LlmRequest? Request{get;private set;}
        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request,[EnumeratorCancellation]CancellationToken cancellationToken=default){Request=request;yield return new TextDeltaEvent("done");yield return new ModelCompletedEvent("stop");await Task.CompletedTask;}
        public Task<LlmResponse> CompleteAsync(LlmRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection,CancellationToken cancellationToken=default)=>Task.FromResult(new ConnectionTestResult(true,"ok",TimeSpan.Zero));
    }

    private sealed class DelegationProvider:ILLMProvider
    {
        private int _mainCalls;
        public string ProviderType=>"test";
        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request,[EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            if(request.Messages.Any(message=>message.Role==MessageRole.System&&message.Content.Contains("focused reviewer subagent",StringComparison.Ordinal)))
            {
                yield return new TextDeltaEvent("Findings: focused evidence");
                yield return new ModelCompletedEvent("stop");
                yield break;
            }
            _mainCalls++;
            if(_mainCalls==1)
            {
                yield return new ToolCallStartedEvent("delegate","delegate_coding_task");
                yield return new ToolCallArgumentsEvent("delegate","{\"title\":\"Architecture review\",\"role\":\"reviewer\",\"task\":\"Find architecture risks\",\"access\":\"read_only\",\"max_iterations\":3}");
                yield return new ModelCompletedEvent("tool_calls");
            }
            else
            {
                yield return new TextDeltaEvent("main final");
                yield return new ModelCompletedEvent("stop");
            }
            await Task.CompletedTask;
        }
        public Task<LlmResponse> CompleteAsync(LlmRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(new LlmResponse("forced delegated report",[],null,"stop"));
        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection,CancellationToken cancellationToken=default)=>Task.FromResult(new ConnectionTestResult(true,"ok",TimeSpan.Zero));
    }

    private sealed class TestNamedTool(string name):ITool
    {
        public ToolDescriptor Descriptor{get;}=new(name,name,JsonDocument.Parse("""{"type":"object","additionalProperties":true}""").RootElement.Clone(),ToolRisk.Safe,[]);
        public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments,ToolExecutionContext context,CancellationToken cancellationToken=default)=>Task.FromResult(new ToolExecutionResult(true,"ok"));
    }
}
