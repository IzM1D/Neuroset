using System.Text.Json;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tests;

internal sealed class InMemoryConversationRepository : IConversationRepository
{
    public List<Conversation> Conversations{get;}=[];public List<ChatMessage> Messages{get;}=[];
    public Task<IReadOnlyList<Conversation>> ListAsync(string? search=null,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<Conversation>>(Conversations.Where(x=>search is null||x.Title.Contains(search,StringComparison.OrdinalIgnoreCase)).ToArray());
    public Task<Conversation?> GetAsync(Guid id,CancellationToken cancellationToken=default)=>Task.FromResult(Conversations.FirstOrDefault(x=>x.Id==id));
    public Task SaveAsync(Conversation conversation,CancellationToken cancellationToken=default){Conversations.RemoveAll(x=>x.Id==conversation.Id);Conversations.Add(conversation);return Task.CompletedTask;}
    public Task DeleteAsync(Guid id,CancellationToken cancellationToken=default){Conversations.RemoveAll(x=>x.Id==id);Messages.RemoveAll(x=>x.ConversationId==id);return Task.CompletedTask;}
    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<ChatMessage>>(Messages.Where(x=>x.ConversationId==conversationId&&x.IsActiveVersion).OrderBy(x=>x.CreatedAt).ToArray());
    public Task SaveMessageAsync(ChatMessage message,CancellationToken cancellationToken=default){if(message.VersionGroupId==Guid.Empty)message.VersionGroupId=message.Id;Messages.RemoveAll(x=>x.Id==message.Id);Messages.Add(message);return Task.CompletedTask;}
    public Task DeleteMessagesAfterAsync(Guid conversationId,DateTimeOffset timestamp,CancellationToken cancellationToken=default){Messages.RemoveAll(x=>x.ConversationId==conversationId&&x.CreatedAt>timestamp);return Task.CompletedTask;}
    public Task<ChatMessage?> GetMessageAsync(Guid messageId,CancellationToken cancellationToken=default)=>Task.FromResult(Messages.FirstOrDefault(x=>x.Id==messageId));
    public Task<ChatMessage> CreateMessageVersionAsync(Guid messageId,string content,IReadOnlyList<AttachmentInfo>? attachments=null,CancellationToken cancellationToken=default){var original=Messages.First(x=>x.Id==messageId);var group=original.VersionGroupId==Guid.Empty?original.Id:original.VersionGroupId;foreach(var item in Messages.Where(x=>(x.VersionGroupId==Guid.Empty?x.Id:x.VersionGroupId)==group))item.IsActiveVersion=false;var version=new ChatMessage{ConversationId=original.ConversationId,Role=original.Role,Content=content,ParentMessageId=original.ParentMessageId,VersionGroupId=group,VersionIndex=Messages.Where(x=>(x.VersionGroupId==Guid.Empty?x.Id:x.VersionGroupId)==group).Select(x=>x.VersionIndex).DefaultIfEmpty(-1).Max()+1,Attachments=attachments?.ToList()??original.Attachments.ToList()};Messages.Add(version);return Task.FromResult(version);}
    public Task SetActiveMessageVersionAsync(Guid messageId,CancellationToken cancellationToken=default){var target=Messages.First(x=>x.Id==messageId);var group=target.VersionGroupId==Guid.Empty?target.Id:target.VersionGroupId;foreach(var item in Messages.Where(x=>(x.VersionGroupId==Guid.Empty?x.Id:x.VersionGroupId)==group))item.IsActiveVersion=item.Id==messageId;return Task.CompletedTask;}
    public Task<bool> ActivateAdjacentMessageVersionAsync(Guid messageId,int direction,CancellationToken cancellationToken=default){var target=Messages.First(x=>x.Id==messageId);var group=target.VersionGroupId==Guid.Empty?target.Id:target.VersionGroupId;var versions=Messages.Where(x=>(x.VersionGroupId==Guid.Empty?x.Id:x.VersionGroupId)==group).OrderBy(x=>x.VersionIndex).ToArray();var index=Array.FindIndex(versions,x=>x.Id==messageId);var next=index+Math.Sign(direction);if(next<0||next>=versions.Length)return Task.FromResult(false);foreach(var item in versions)item.IsActiveVersion=item.Id==versions[next].Id;return Task.FromResult(true);}
    public Task DeleteMessageAsync(Guid messageId,CancellationToken cancellationToken=default){Messages.RemoveAll(x=>x.Id==messageId||IsDescendant(x,messageId));return Task.CompletedTask;}
    private bool IsDescendant(ChatMessage message,Guid ancestor){var parent=message.ParentMessageId;while(parent is { } id){if(id==ancestor)return true;parent=Messages.FirstOrDefault(x=>x.Id==id)?.ParentMessageId;}return false;}
}

internal sealed class InMemoryMemoryStore : IMemoryStore
{
    public List<MemoryRecord> Items{get;}=[];
    public Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope? scope=null,Guid? conversationId=null,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<MemoryRecord>>(Items.Where(x=>(scope is null||x.Scope==scope)&&(conversationId is null||x.ConversationId==conversationId)).ToArray());
    public Task SaveAsync(MemoryRecord memory,CancellationToken cancellationToken=default){Items.RemoveAll(x=>x.Id==memory.Id);Items.Add(memory);return Task.CompletedTask;}
    public Task DeleteAsync(Guid id,CancellationToken cancellationToken=default){Items.RemoveAll(x=>x.Id==id);return Task.CompletedTask;}
    public Task ClearAsync(MemoryScope? scope=null,CancellationToken cancellationToken=default){Items.RemoveAll(x=>scope is null||x.Scope==scope);return Task.CompletedTask;}
}

internal sealed class InMemorySettingsRepository(AppSettings? value=null) : ISettingsRepository
{
    public AppSettings Value{get;private set;}=value??new();public Task<AppSettings> LoadAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Value);public Task SaveAsync(AppSettings settings,CancellationToken cancellationToken=default){Value=settings;return Task.CompletedTask;}
}

internal sealed class InMemoryPermissionRepository : IToolPermissionRepository
{
    public List<ToolPermissionRule> Rules{get;}=[];
    public Task<ToolPermissionRule?> FindAsync(string toolName,ToolRisk risk,Guid? conversationId,CancellationToken cancellationToken=default)=>Task.FromResult(Rules.Where(x=>x.ToolName==toolName&&x.Risk==risk&&(x.ConversationId==conversationId||x.ConversationId is null)).OrderByDescending(x=>x.ConversationId is not null).FirstOrDefault());
    public Task<IReadOnlyList<ToolPermissionRule>> ListAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<ToolPermissionRule>>(Rules.ToArray());
    public Task SaveAsync(ToolPermissionRule rule,CancellationToken cancellationToken=default){Rules.RemoveAll(x=>x.Id==rule.Id);Rules.Add(rule);return Task.CompletedTask;}
    public Task DeleteAsync(Guid id,CancellationToken cancellationToken=default){Rules.RemoveAll(x=>x.Id==id);return Task.CompletedTask;}
}

internal sealed class FixedPermissionPrompt(PermissionDecision decision) : IPermissionPrompt{public int Calls{get;private set;}public ValueTask<PermissionDecision> RequestAsync(PermissionRequest request,CancellationToken cancellationToken=default){Calls++;return ValueTask.FromResult(decision);}}
internal sealed class FixedPermissionService(PermissionDecision decision) : IPermissionService{public Task<PermissionDecision> AuthorizeAsync(PermissionRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(decision);}
internal sealed class MemorySecretStore(string value="test-key") : ISecretStore{public Task<string> StoreAsync(string secret,string? secretId=null,CancellationToken cancellationToken=default)=>Task.FromResult(secretId??"secret");public Task<string?> GetAsync(string secretId,CancellationToken cancellationToken=default)=>Task.FromResult<string?>(value);public Task DeleteAsync(string secretId,CancellationToken cancellationToken=default)=>Task.CompletedTask;}

internal sealed class DelegateHttpHandler(Func<HttpRequestMessage,HttpResponseMessage> handler) : HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(handler(request));}

internal sealed class EchoTool : ITool
{
    public ToolDescriptor Descriptor{get;}=new("echo","Echo a value",JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""").RootElement.Clone(),ToolRisk.Safe,[]);
    public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments,ToolExecutionContext context,CancellationToken cancellationToken=default)=>Task.FromResult(new ToolExecutionResult(true,arguments.GetProperty("value").GetString()!));
}
