using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Neuroset.Domain;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class SerializationAndSandboxTests
{
    [Fact]public void Message_Serialization_RoundTrips_Tool_History(){var message=new ChatMessage{ConversationId=Guid.NewGuid(),Role=MessageRole.Assistant,Content="answer",ToolCalls=[new ToolCallRecord{Id="c1",Name="echo",ArgumentsJson="{\"value\":\"x\"}",Execution=new ToolExecutionRecord{Succeeded=true,Output="x"}}]};var json=JsonSerializer.Serialize(message,DomainJson.Options);var loaded=JsonSerializer.Deserialize<ChatMessage>(json,DomainJson.Options)!;loaded.Should().BeEquivalentTo(message);}
    [Fact]public async Task Python_Sandbox_Executes_Code_And_Blocks_Network(){if(!HasPythonLauncher())return;var root=Path.Combine(Path.GetTempPath(),"NeurosetSandboxTests",Guid.NewGuid().ToString("N"));try{var sandbox=new PythonSandbox(new PythonSandboxOptions(root,"py","3.14"));var simple=await sandbox.ExecuteAsync("print(2 + 2)",TimeSpan.FromSeconds(10));simple.ExitCode.Should().Be(0,simple.StandardError);simple.StandardOutput.Trim().Should().Be("4");var blocked=await sandbox.ExecuteAsync("import socket\nsocket.socket()",TimeSpan.FromSeconds(10));blocked.ExitCode.Should().NotBe(0);blocked.StandardError.Should().Contain("blocked by Neuroset sandbox");}finally{if(Directory.Exists(root))Directory.Delete(root,true);}}
    [Fact]public async Task Python_Full_Access_Can_Read_Outside_Internal_Sandbox(){if(!HasPythonLauncher())return;var root=Path.Combine(Path.GetTempPath(),"NeurosetFullAccessTests",Guid.NewGuid().ToString("N"));var workspace=Path.Combine(root,"workspace");var external=Path.Combine(root,"external.txt");try{Directory.CreateDirectory(workspace);await File.WriteAllTextAsync(external,"full-access-ok");var sandbox=new PythonSandbox(new PythonSandboxOptions(Path.Combine(root,"internal"),"py","3.14"));var pathLiteral=JsonSerializer.Serialize(external);var result=await sandbox.ExecuteAsync($"print(open({pathLiteral}, encoding='utf-8').read())",TimeSpan.FromSeconds(10),workspace,true);result.ExitCode.Should().Be(0,result.StandardError);result.StandardOutput.Trim().Should().Be("full-access-ok");}finally{if(Directory.Exists(root))Directory.Delete(root,true);}}
    private static bool HasPythonLauncher(){try{using var process=Process.Start(new ProcessStartInfo("py","-3.14 --version"){UseShellExecute=false,CreateNoWindow=true});process!.WaitForExit(5000);return process.ExitCode==0;}catch{return false;}}
}
