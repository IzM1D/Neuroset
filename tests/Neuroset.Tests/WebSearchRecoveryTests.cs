using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class WebSearchRecoveryTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    public async Task Five_Failed_Or_Empty_Searches_Force_An_Answer_And_Close_Queued_Calls(bool empty, int batchSize)
    {
        var provider = new SearchingModel(batchSize);
        var search = new ScriptedSearch(empty: empty);

        var (events, conversations) = await RunAsync(provider, new WebSearchTool(search));

        search.Calls.Should().Be(5);
        provider.Requests.Should().HaveCount(batchSize == 1 ? 6 : 2);
        AssertFinalAnswer(provider, events, conversations);
        var completed = events.OfType<ToolExecutionCompletedEvent>().ToArray();
        completed.Should().HaveCount(batchSize == 1 ? 5 : batchSize);
        completed.Take(5).Should().OnlyContain(result => result.Succeeded == empty);
        if (batchSize > 5)
            completed.Skip(5).Should().OnlyContain(result => !result.Succeeded && result.Error!.Contains("not sent", StringComparison.Ordinal));
        provider.Requests[^1].Messages.Where(message => message.Role == MessageRole.Tool).Should().HaveCount(completed.Length);
        provider.Requests[^1].Messages.Should().Contain(message => message.Role == MessageRole.System && message.Content.Contains("5/5", StringComparison.Ordinal));
        conversations.Messages.SelectMany(message => message.ToolCalls).Should().HaveCount(completed.Length)
            .And.OnlyContain(call => call.Execution != null && call.Execution.CompletedAt != null);
    }

    [Fact]
    public async Task Successful_Searches_Are_Preserved_But_Do_Not_Reset_The_Failure_Limit()
    {
        var provider = new SearchingModel(9);
        var search = new ScriptedSearch(successfulCalls: [2, 4]);

        var (events, conversations) = await RunAsync(provider, new WebSearchTool(search));

        search.Calls.Should().Be(7);
        events.OfType<ToolExecutionCompletedEvent>().Count(result => result.Succeeded).Should().Be(2);
        provider.Requests[^1].Messages.Should().Contain(message => message.Role == MessageRole.Tool && message.Content.Contains("Verified forecast", StringComparison.Ordinal));
        AssertFinalAnswer(provider, events, conversations);
    }

    [Fact]
    public async Task Model_Sees_Search_Error_And_Can_Retry_Successfully()
    {
        var provider = new SearchingModel(1, answerAfterRounds: 2);
        var search = new ScriptedSearch(successfulCalls: [2]);

        var (events, conversations) = await RunAsync(provider, new WebSearchTool(search));

        search.Calls.Should().Be(2);
        provider.Requests[1].Messages.Should().Contain(message => message.Role == MessageRole.Tool && message.Content.StartsWith("Tool error:", StringComparison.Ordinal));
        provider.Requests[1].Messages.Should().Contain(message => message.Role == MessageRole.System && message.Content.Contains("1/5", StringComparison.Ordinal));
        events.OfType<ToolExecutionCompletedEvent>().Select(result => result.Succeeded).Should().Equal(false, true);
        events.OfType<ModelErrorEvent>().Should().BeEmpty();
        conversations.Messages.Should().Contain(message => message.Content == "Answer from available evidence.");
    }

    [Fact]
    public async Task Hanging_Search_Provider_Cannot_Keep_The_Agent_From_Finalizing()
    {
        var provider = new SearchingModel(7);
        var pending = new TaskCompletionSource<SearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var (events, conversations) = await RunAsync(provider, new WebSearchTool(new HangingSearch(pending.Task), TimeSpan.FromMilliseconds(100)));

            events.OfType<ToolExecutionCompletedEvent>().Take(5).Should().OnlyContain(result => !result.Succeeded && result.Output.Contains("search_timeout", StringComparison.Ordinal));
            AssertFinalAnswer(provider, events, conversations);
        }
        finally { pending.TrySetCanceled(); }
    }

    [Fact]
    public async Task Stream_Timeout_During_Preparing_Returns_Tool_Errors_And_Still_Finalizes()
    {
        var provider = new SearchingModel(1, interruptPreparation: true);
        var search = new ScriptedSearch();

        var (events, conversations) = await RunAsync(provider, new WebSearchTool(search));

        search.Calls.Should().Be(0);
        events.OfType<ToolCallStartedEvent>().Should().HaveCount(5);
        events.OfType<ToolExecutionCompletedEvent>().Should().HaveCount(5)
            .And.OnlyContain(result => !result.Succeeded && result.Error!.Contains("not sent", StringComparison.Ordinal));
        AssertFinalAnswer(provider, events, conversations);
    }

    [Fact]
    public async Task Caller_Cancellation_Still_Stops_The_Agent_Without_A_Synthesis_Retry()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new SearchingModel(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunAsync(provider, new WebSearchTool(new CancellableSearch(started)), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();
        var act = () => run;

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.Requests.Should().ContainSingle();
    }

    private static void AssertFinalAnswer(SearchingModel provider, List<ModelEvent> events, InMemoryConversationRepository conversations)
    {
        provider.Requests[^1].Tools.Should().BeEmpty();
        events.OfType<ModelErrorEvent>().Should().BeEmpty();
        events.OfType<TextDeltaEvent>().Should().Contain(delta => delta.Delta == "Answer from available evidence.");
        events.OfType<AgentProgressEvent>().Last().IsCompleted.Should().BeTrue();
        conversations.Messages.Should().Contain(message => message.Content == "Answer from available evidence." && message.Status == MessageStatus.Completed);
        events.OfType<ToolCallStartedEvent>().Select(call => call.CallId).Should()
            .BeEquivalentTo(events.OfType<ToolExecutionCompletedEvent>().Select(call => call.CallId));
    }

    private static async Task<(List<ModelEvent> Events, InMemoryConversationRepository Conversations)> RunAsync(SearchingModel provider, ITool tool, CancellationToken cancellationToken = default)
    {
        var conversations = new InMemoryConversationRepository();
        var registry = new ToolRegistry([tool]);
        var runtime = new AgentRuntime(new ProviderFactory(provider), registry,
            new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AllowOnce), NullLogger<ToolExecutor>.Instance),
            conversations, new InMemoryMemoryStore(), new InMemorySettingsRepository(new AppSettings { LongTermMemoryEnabled = false }), NullLogger<AgentRuntime>.Instance);
        var conversation = new Conversation();
        var user = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Search for DRAM forecasts" };
        var request = new AgentRunRequest(conversation, [user], new ConnectionProfile { Name = "test", BaseUrl = "https://example.test/v1", Model = "test" },
            new AgentRunOptions(20, HardMaximumIterations: 30, Reasoning: ReasoningPreference.High));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var events = new List<ModelEvent>();
        await foreach (var item in runtime.RunAsync(request, deadline.Token)) events.Add(item);
        return (events, conversations);
    }

    private sealed class ProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider Create(ConnectionProfile connection) => provider;
    }

    private sealed class SearchingModel(int batchSize, int answerAfterRounds = int.MaxValue, bool interruptPreparation = false) : ILLMProvider
    {
        public string ProviderType => "test";
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() });
            if (request.Tools.Count == 0 || Requests.Count > answerAfterRounds)
            {
                yield return new TextDeltaEvent("Answer from available evidence.");
                yield return new ModelCompletedEvent("stop");
                yield break;
            }
            for (var index = 0; index < batchSize; index++)
            {
                var id = $"search_{Requests.Count}_{index}";
                yield return new ToolCallStartedEvent(id, "web_search");
                yield return new ToolCallArgumentsEvent(id, JsonSerializer.Serialize(new { query = $"forecast {id}", limit = 8 }));
            }
            await Task.CompletedTask;
            if (interruptPreparation) throw new TaskCanceledException("LLM connection timed out");
            yield return new ModelCompletedEvent("tool_calls");
        }
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "ok", TimeSpan.Zero));
    }

    private sealed class ScriptedSearch(bool empty = false, int[]? successfulCalls = null) : ISearchProvider
    {
        public int Calls { get; private set; }
        public Task<SearchResponse> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (successfulCalls?.Contains(Calls) == true)
                return Task.FromResult(new SearchResponse([new("Verified forecast", "https://example.org/forecast", "Evidence", "test")], [new("test", "success", 1)]));
            return Task.FromResult(new SearchResponse([], [new("test", empty ? "no_results" : "failed", 0, Detail: "Search unavailable")]));
        }
    }
    private sealed class HangingSearch(Task<SearchResponse> pending) : ISearchProvider
    {
        public Task<SearchResponse> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default) => pending;
    }
    private sealed class CancellableSearch(TaskCompletionSource started) : ISearchProvider
    {
        public async Task<SearchResponse> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new([], []);
        }
    }
}
