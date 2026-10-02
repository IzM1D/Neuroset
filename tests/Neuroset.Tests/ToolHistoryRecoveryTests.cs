using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Providers;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class ToolHistoryRecoveryTests
{
    private static ConnectionProfile Connection => new() { Name = "test", BaseUrl = "https://example.test/v1", Model = "model", TimeoutSeconds = 20 };
    private const string MissingOutputError = "No tool output found for function call call_saved.";

    [Fact]
    public async Task Compaction_Keeps_Entire_Tool_Batches_With_All_Their_Results()
    {
        var conversation = new Conversation();
        var history = new List<ChatMessage> { new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Create a presentation from this research." } };
        for (var batch = 0; batch < 12; batch++)
        {
            var message = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.Assistant };
            for (var call = 0; call < 3; call++)
            {
                var id = $"batch-{batch}-call-{call}";
                message.ToolCalls.Add(new ToolCallRecord
                {
                    Id = id, Name = "echo", ArgumentsJson = "{\"value\":\"research\"}",
                    Execution = new ToolExecutionRecord { ToolCallId = id, ToolName = "echo", Succeeded = true, Output = $"Evidence {id}: " + new string('x', 14_000) }
                });
            }
            history.Add(message);
        }
        history.Add(new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Continue from the saved results." });
        var provider = new CaptureProvider();
        var repository = new InMemoryConversationRepository();
        await Run(CreateRuntime(provider, repository), conversation, history);
        var messages = provider.Requests.Single().Messages;
        AssertCompleteBatches(messages);
        messages.Should().Contain(m => m.Content.StartsWith("Compacted coding work ledger", StringComparison.Ordinal));
        var latestRequest = history[^1].Content;
        messages.Should().Contain(m => m.Role == MessageRole.User && m.Content == latestRequest);
        messages.Where(m => m.ToolCalls is { Count: > 0 }).Should().OnlyContain(m => m.ToolCalls!.Count == 3);
        history.SelectMany(m => m.ToolCalls).Should().HaveCount(36);
    }

    [Fact]
    public void Normalization_Preserves_Results_And_Defers_Guidance_Until_The_Batch_Is_Complete()
    {
        LlmMessage[] history = [
            new(MessageRole.User, "Make the deck."),
            new(MessageRole.Assistant, "Research", ToolCalls: [new("a", "echo", "{}"), new("b", "echo", "{}")]),
            new(MessageRole.Tool, "first result", "echo", "a"),
            new(MessageRole.System, "Continue with the existing evidence."),
            new(MessageRole.Tool, "second result", "echo", "b"),
            new(MessageRole.User, "Continue.")
        ];
        var normalized = LlmToolHistory.Normalize(history);
        AssertCompleteBatches(normalized);
        normalized.Select(message => message.Content).Should().Equal(
            "Make the deck.", "Research", "first result", "second result", "Continue with the existing evidence.", "Continue.");
        history[3].Role.Should().Be(MessageRole.System);
        LlmToolHistory.Normalize(normalized).Should().Equal(normalized);
    }

    [Fact]
    public async Task Interrupted_Historical_Call_Gets_An_Honest_Unavailable_Result_Without_Execution()
    {
        var conversation = new Conversation();
        var interrupted = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.Assistant, Status = MessageStatus.Cancelled,
            ToolCalls = [new ToolCallRecord { Id = "interrupted", Name = "echo", ArgumentsJson = "{}" }] };
        var provider = new CaptureProvider();
        var tool = new CountingTool();
        await Run(CreateRuntime(provider, new(), tool), conversation,
            [interrupted, new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Continue from saved work." }]);
        var messages = provider.Requests.Single().Messages;
        AssertCompleteBatches(messages);
        var output = messages.Single(message => message.ToolCallId == "interrupted");
        using var status = JsonDocument.Parse(output.Content);
        status.RootElement.GetProperty("status").GetString().Should().Be("unavailable");
        output.Content.Should().Contain("success is unknown");
        interrupted.ToolCalls.Single().Execution.Should().BeNull();
        tool.Values.Should().BeEmpty();
    }

    [Fact]
    public void Orphan_And_Duplicate_Reports_Keep_Their_Evidence_Without_Invalid_Protocol()
    {
        var original = CompletedHistory().ToList();
        original.Insert(0, new(MessageRole.Tool, "orphan evidence", "echo", "orphan"));
        original.Insert(3, new(MessageRole.Tool, "duplicate evidence", "echo", "call_saved"));
        original.Add(new(MessageRole.Assistant, "duplicate request", ToolCalls: [new("call_saved", "echo", "{}")]));
        original.Add(new(MessageRole.Tool, "later report", "echo", "call_saved"));
        var normalized = LlmToolHistory.Normalize(original);
        AssertCompleteBatches(normalized);
        normalized.Where(message => message.Role == MessageRole.Tool).Should().ContainSingle();
        foreach (var evidence in new[] { "orphan evidence", "duplicate evidence", "duplicate request", "later report" })
            normalized.Should().Contain(message => message.Role == MessageRole.Assistant && message.Content.Contains(evidence));
        LlmToolHistory.Normalize(normalized).Should().Equal(normalized);
    }

    [Fact]
    public void Missing_Output_Is_Not_Borrowed_From_A_Later_User_Turn()
    {
        LlmMessage[] history = [
            new(MessageRole.Assistant, "", ToolCalls: [new("old", "echo", "{}")]),
            new(MessageRole.User, "Stop and change the topic."),
            new(MessageRole.Tool, "late evidence", "echo", "old")
        ];
        var normalized = LlmToolHistory.Normalize(history);
        AssertCompleteBatches(normalized);
        normalized[1].Content.Should().Contain("unavailable");
        normalized[2].Should().Be(history[1]);
        normalized[3].Role.Should().Be(MessageRole.Assistant);
        normalized[3].Content.Should().Contain("late evidence");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_Normalizes_Outgoing_History_Before_The_First_Request(bool stream)
    {
        using var handler = new RecordingHandler((_, _) => Success(stream));
        using var client = new HttpClient(handler);
        var provider = Provider(client);
        var history = CompletedHistory().Where(message => message.Role != MessageRole.Tool).ToArray();
        var request = new LlmRequest(Connection, history, [], Stream: stream);
        if (stream) (await Collect(provider.StreamAsync(request))).OfType<ModelErrorEvent>().Should().BeEmpty();
        else (await provider.CompleteAsync(request)).ToolHistoryRecovered.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
        AssertCompleteBatches(WireMessages(handler.Requests[0]));
        WireMessages(handler.Requests[0]).Should().Contain(message => message.Role == MessageRole.Tool && message.Content.Contains("unavailable"));
        history.Should().NotContain(message => message.Role == MessageRole.Tool);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task History_Error_Retries_Once_With_Saved_Reports_And_Unchanged_Request_Settings(bool stream)
    {
        using var handler = new RecordingHandler((attempt, _) => attempt == 1 ? Error(MissingOutputError) : Success(stream));
        using var client = new HttpClient(handler);
        var provider = Provider(client);
        var history = CompletedHistory();
        var tool = new EchoTool().Descriptor;
        var request = new LlmRequest(Connection, history, [new(tool.Name, tool.Description, tool.Schema)], Stream: stream,
            Generation: new GenerationSettings { Temperature = .7, MaxTokens = 789 }, ReasoningEffort: ReasoningEffort.High, RequiredToolName: "echo");
        if (stream)
        {
            var events = await Collect(provider.StreamAsync(request));
            events.OfType<ModelErrorEvent>().Should().BeEmpty();
            events.OfType<ModelRecoveryEvent>().Should().ContainSingle();
            events.First().Should().BeOfType<ModelRecoveryEvent>();
            events.OfType<TextDeltaEvent>().Select(item => item.Delta).Should().Contain("done");
        }
        else
        {
            var response = await provider.CompleteAsync(request);
            response.Content.Should().Be("done");
            response.ToolHistoryRecovered.Should().BeTrue();
        }
        handler.Requests.Should().HaveCount(2);
        var second = handler.Requests[1];
        foreach (var key in new[] { "model", "stream", "tools", "tool_choice", "parallel_tool_calls", "temperature", "max_tokens", "reasoning_effort" })
            second.GetProperty(key).GetRawText().Should().Be(handler.Requests[0].GetProperty(key).GetRawText());
        var repaired = WireMessages(second);
        repaired.Should().NotContain(message => LlmToolHistory.HasToolProtocol(message));
        repaired.Should().Contain(message => message.Role == MessageRole.User && message.Content == "Continue the saved presentation.");
        var report = repaired.Single(message => message.Content.Contains("Saved report"));
        using var record = JsonDocument.Parse(report.Content[(report.Content.IndexOf('\n') + 1)..]);
        record.RootElement.GetProperty("content").GetString().Should().Be(history[1].Content);
        history[1].Role.Should().Be(MessageRole.Tool);
    }

    [Theory]
    [InlineData("An assistant message with 'tool_calls' must be followed by tool messages responding to each 'tool_call_id'.", 400)]
    [InlineData("Invalid parameter: messages with role 'tool' must be a response to a preceding message with 'tool_calls'.", 400)]
    [InlineData("No tool call found for function call output with call_id old.", 422)]
    public async Task Compatible_History_Errors_Also_Recover(string error, int status)
    {
        using var handler = new RecordingHandler((attempt, _) => attempt == 1 ? Error(error, (HttpStatusCode)status) : Success(false));
        using var client = new HttpClient(handler);
        (await Provider(client).CompleteAsync(new(Connection, CompletedHistory(), [], Stream: false))).ToolHistoryRecovered.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(400, "Unknown model")]
    [InlineData(401, "Unauthorized")]
    [InlineData(400, "Invalid tool schema: tool_call_id must be a string")]
    [InlineData(403, "No tool output found for function call old.")]
    public async Task Unrelated_Errors_Do_Not_Trigger_History_Recovery(int status, string message)
    {
        using var handler = new RecordingHandler((_, _) => Error(message, (HttpStatusCode)status));
        using var client = new HttpClient(handler);
        var events = await Collect(Provider(client).StreamAsync(new(Connection, CompletedHistory(), [])));
        handler.Requests.Should().ContainSingle();
        events.OfType<ModelErrorEvent>().Should().ContainSingle();
        events.OfType<ModelRecoveryEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Runtime_Continues_After_Recovery_And_Executes_Each_New_Call_Only_Once()
    {
        using var handler = new RecordingHandler((attempt, _) => attempt switch
        {
            1 => ToolResponse("call_saved", "create"),
            2 => Error(MissingOutputError),
            3 => ToolResponse("call_verify", "verify"),
            _ => Success(true)
        });
        using var client = new HttpClient(handler);
        var tool = new CountingTool();
        var repository = new InMemoryConversationRepository();
        var conversation = new Conversation();
        var events = await Run(CreateRuntime(Provider(client), repository, tool), conversation,
            [new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Create a presentation." }]);
        events.OfType<ModelErrorEvent>().Should().BeEmpty();
        events.OfType<ModelRecoveryEvent>().Should().ContainSingle();
        tool.Values.Should().Equal("create", "verify");
        handler.Requests.Should().HaveCount(4);
        var next = WireMessages(handler.Requests[3]);
        AssertCompleteBatches(next);
        next.SelectMany(message => message.ToolCalls ?? []).Should().NotContain(call => call.Id == "call_saved");
        next.Should().Contain(message => message.Content.Contains("Saved report for create"));
        repository.Messages.SelectMany(message => message.ToolCalls).Should().HaveCount(2);
        repository.Messages.Should().OnlyContain(message => message.Status == MessageStatus.Completed);
    }

    [Fact]
    public async Task Repeated_History_Error_Is_Bounded_And_Preserves_Completed_Work_With_Failed_Status()
    {
        using var handler = new RecordingHandler((attempt, _) => attempt == 1 ? ToolResponse("call_saved", "create") : Error(MissingOutputError));
        using var client = new HttpClient(handler);
        var tool = new CountingTool();
        var repository = new InMemoryConversationRepository();
        var conversation = new Conversation();
        var events = await Run(CreateRuntime(Provider(client), repository, tool), conversation,
            [new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Create a presentation." }]);
        handler.Requests.Should().HaveCount(3);
        tool.Values.Should().Equal("create");
        events.OfType<ModelRecoveryEvent>().Should().BeEmpty();
        var error = events.OfType<ModelErrorEvent>().Should().ContainSingle().Subject;
        var saved = repository.Messages.Last();
        error.SavedMessageId.Should().Be(saved.Id);
        saved.Status.Should().Be(MessageStatus.Failed);
        saved.Content.Should().Contain("сохранены").And.Contain("продолжи");
        saved.ParentMessageId.Should().Be(repository.Messages.First().Id);
        repository.Messages.SelectMany(message => message.ToolCalls).Should().ContainSingle(call =>
            call.Id == "call_saved" && call.Execution!.Succeeded && call.Execution.Output.Contains("Saved report for create"));
    }

    [Fact]
    public async Task Cancellation_During_Recovery_Stops_Without_More_Retries()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RecordingHandler((attempt, _) =>
        {
            if (attempt == 2) cancellation.Cancel();
            return Error(MissingOutputError);
        });
        using var client = new HttpClient(handler);
        Func<Task> run = () => Collect(Provider(client).StreamAsync(new(Connection, CompletedHistory(), []), cancellation.Token));
        await run.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task NonStreaming_Repeated_History_Error_Throws_After_One_Repair_Attempt()
    {
        using var handler = new RecordingHandler((_, _) => Error(MissingOutputError));
        using var client = new HttpClient(handler);
        Func<Task> run = () => Provider(client).CompleteAsync(new(Connection, CompletedHistory(), [], Stream: false));
        await run.Should().ThrowAsync<LlmProviderException>().WithMessage(MissingOutputError);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_History_Error_Without_Any_Historical_Calls_Is_Not_Retried()
    {
        using var handler = new RecordingHandler((_, _) => Error(MissingOutputError));
        using var client = new HttpClient(handler);
        var events = await Collect(Provider(client).StreamAsync(new(Connection, [new(MessageRole.User, "Hello")], [])));
        handler.Requests.Should().ContainSingle();
        events.OfType<ModelErrorEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_Partial_Answer_Is_Preserved_When_The_Stream_Fails()
    {
        using var handler = new RecordingHandler((_, _) => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""data: {"choices":[{"delta":{"content":"Saved research summary."}}]}""" + "\n\n" +
                """data: {"error":{"message":"Connection lost"}}""" + "\n\n", Encoding.UTF8, "text/event-stream")
        });
        using var client = new HttpClient(handler);
        var repository = new InMemoryConversationRepository();
        var conversation = new Conversation();
        var events = await Run(CreateRuntime(Provider(client), repository), conversation,
            [new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Continue." }]);
        var saved = repository.Messages.Should().ContainSingle().Subject;
        saved.Status.Should().Be(MessageStatus.Failed);
        saved.Content.Should().StartWith("Saved research summary.");
        events.OfType<ModelErrorEvent>().Should().ContainSingle(error => error.SavedMessageId == saved.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Final_Synthesis_Uses_Recovery_And_Records_An_Exhausted_Failure_Honestly(bool recover)
    {
        using var handler = new RecordingHandler((attempt, _) => attempt switch
        {
            1 => ToolResponse("call_saved", "create"),
            2 => Error(MissingOutputError),
            _ => recover ? Success(true) : Error(MissingOutputError)
        });
        using var client = new HttpClient(handler);
        var repository = new InMemoryConversationRepository();
        var conversation = new Conversation();
        var runtime = CreateRuntime(Provider(client), repository, new CountingTool());
        var events = await Collect(runtime.RunAsync(new(conversation,
            [new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Create a deck." }], Connection,
            new AgentRunOptions(1, RetryCount: 0, HardMaximumIterations: 1, Reasoning: ReasoningPreference.High))));
        handler.Requests.Should().HaveCount(3);
        handler.Requests[1].TryGetProperty("tools", out _).Should().BeFalse();
        repository.Messages.Last().Status.Should().Be(recover ? MessageStatus.Completed : MessageStatus.Failed);
        if (recover)
        {
            events.OfType<ModelRecoveryEvent>().Should().ContainSingle();
            events.OfType<ModelErrorEvent>().Should().BeEmpty();
        }
        else
        {
            var savedId = repository.Messages.Last().Id;
            events.OfType<ModelErrorEvent>().Should().ContainSingle(error => error.SavedMessageId == savedId);
        }
        repository.Messages.SelectMany(message => message.ToolCalls).Should().ContainSingle(call => call.Execution!.Succeeded);
    }

    [Fact]
    public async Task A_Single_Oversized_Batch_Is_Summarized_Whole_Without_Losing_The_User_Request()
    {
        var conversation = new Conversation();
        var batch = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.Assistant };
        for (var index = 0; index < 20; index++) batch.ToolCalls.Add(new()
        {
            Id = $"large-{index}", Name = "echo", ArgumentsJson = "{}",
            Execution = new() { ToolName = "echo", ToolCallId = $"large-{index}", Succeeded = true, Output = new string('x', 14_000) }
        });
        var provider = new CaptureProvider();
        await Run(CreateRuntime(provider, new()), conversation,
            [batch, new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Finish this existing deck." }]);
        var messages = provider.Requests.Single().Messages;
        AssertCompleteBatches(messages);
        messages.Should().Contain(message => message.Role == MessageRole.User && message.Content == "Finish this existing deck.");
        messages.Should().NotContain(message => LlmToolHistory.HasToolProtocol(message));
        messages.Sum(message => message.Content.Length).Should().BeLessThan(190_000);
        batch.ToolCalls.Should().HaveCount(20).And.OnlyContain(call => call.Execution!.Output.Length == 14_000);
    }

    private static LlmMessage[] CompletedHistory() => [
        new(MessageRole.Assistant, "Research completed", ToolCalls: [new("call_saved", "echo", "{\"value\":\"research\"}")]),
        new(MessageRole.Tool, "Saved report: C:\\Slides\\Switzerland.pptx\nKeep this file and the gathered evidence.", "echo", "call_saved"),
        new(MessageRole.User, "Continue the saved presentation.")
    ];

    [Fact]
    public async Task Restored_Legacy_Aggregate_Sends_Its_Existing_Reports_As_Historical_Evidence()
    {
        var conversation = new Conversation();
        var restored = new ChatMessage
        {
            ConversationId = conversation.Id, Role = MessageRole.Assistant, Content = "Saved progress", Status = MessageStatus.Failed,
            Metadata = new() { [LlmToolHistory.RecoveredCallsMetadataKey] = "true" },
            ToolCalls = [new() { Id = "saved", Name = "echo", ArgumentsJson = "{}", Execution = new() { ToolCallId = "saved", ToolName = "echo", Succeeded = true, Output = "Saved report at C:/Slides/deck.pptx" } }]
        };
        var provider = new CaptureProvider();
        var tool = new CountingTool();
        await Run(CreateRuntime(provider, new(), tool), conversation,
            [restored, new() { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Continue." }]);
        var messages = provider.Requests.Single().Messages;
        messages.Should().NotContain(message => LlmToolHistory.HasToolProtocol(message));
        messages.Should().Contain(message => message.Content.Contains("Saved report at C:/Slides/deck.pptx"));
        tool.Values.Should().BeEmpty();
    }

    private static OpenAiCompatibleProvider Provider(HttpClient client) => new(client, new MemorySecretStore(), NullLogger<OpenAiCompatibleProvider>.Instance);

    private static HttpResponseMessage Error(string message, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Success(bool stream) => stream
        ? Sse(new { content = "done" }, "stop")
        : new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}""", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ToolResponse(string id, string value) => Sse(new
    {
        tool_calls = new[] { new { index = 0, id, function = new { name = "echo", arguments = JsonSerializer.Serialize(new { value }) } } }
    }, "tool_calls");

    private static HttpResponseMessage Sse(object delta, string finishReason) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finishReason } } }) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
    };

    private static LlmMessage[] WireMessages(JsonElement body) => body.GetProperty("messages").EnumerateArray().Select(message =>
        new LlmMessage(Enum.Parse<MessageRole>(message.GetProperty("role").GetString()!, ignoreCase: true), message.GetProperty("content").GetString()!,
            ToolCallId: message.TryGetProperty("tool_call_id", out var id) ? id.GetString() : null,
            ToolCalls: message.TryGetProperty("tool_calls", out var calls) ? calls.EnumerateArray().Select(call =>
                new LlmToolCall(call.GetProperty("id").GetString()!, call.GetProperty("function").GetProperty("name").GetString()!, call.GetProperty("function").GetProperty("arguments").GetString()!)).ToArray() : null)).ToArray();

    private static async Task<List<ModelEvent>> Collect(IAsyncEnumerable<ModelEvent> stream)
    {
        var events = new List<ModelEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private sealed class RecordingHandler(Func<int, JsonElement, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            return response(Requests.Count, Requests[^1]);
        }
    }

    private sealed class CountingTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new EchoTool().Descriptor;
        public List<string> Values { get; } = [];
        public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            var value = arguments.GetProperty("value").GetString()!;
            Values.Add(value);
            return Task.FromResult(new ToolExecutionResult(true, $"Saved report for {value}: C:/Slides/Switzerland.pptx"));
        }
    }

    private static AgentRuntime CreateRuntime(ILLMProvider provider, InMemoryConversationRepository repository, params ITool[] tools)
    {
        var registry = new ToolRegistry(tools.Length == 0 ? [new EchoTool()] : tools);
        return new(new FixedProviderFactory(provider), registry,
            new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AlwaysAllow), NullLogger<ToolExecutor>.Instance),
            repository, new InMemoryMemoryStore(), new InMemorySettingsRepository(new AppSettings { LongTermMemoryEnabled = false }), NullLogger<AgentRuntime>.Instance);
    }

    private static async Task<List<ModelEvent>> Run(AgentRuntime runtime, Conversation conversation, IReadOnlyList<ChatMessage> history)
    {
        var events = new List<ModelEvent>();
        await foreach (var item in runtime.RunAsync(new(conversation, history, Connection, new AgentRunOptions(4, RetryCount: 0, Reasoning: ReasoningPreference.High)))) events.Add(item);
        return events;
    }

    private static void AssertCompleteBatches(IReadOnlyList<LlmMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.Tool)
            {
                pending.Remove(message.ToolCallId!).Should().BeTrue($"tool output {message.ToolCallId} must have a preceding call");
                continue;
            }
            pending.Should().BeEmpty("every call must receive its output before the next non-tool message");
            foreach (var call in message.ToolCalls ?? []) pending.Add(call.Id).Should().BeTrue();
        }
        pending.Should().BeEmpty("the final batch must be complete before sending a request");
    }

    private sealed class FixedProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider Create(ConnectionProfile connection) => provider;
    }

    private sealed class CaptureProvider : ILLMProvider
    {
        public string ProviderType => "test";
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() });
            yield return new TextDeltaEvent("Continued from saved research.");
            yield return new ModelCompletedEvent("stop");
            await Task.CompletedTask;
        }
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
