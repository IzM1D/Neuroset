using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class WebSearchFailureTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(3);
    private const string Query = "TrendForce DRAM shortage ease 2027 2028 forecast & цены";
    private const string Rss = "<rss><channel><item><title>Forecast</title><link>https://example.org/forecast</link><description>Evidence</description></item></channel></rss>";
    private static ToolExecutionContext Context => new(Guid.NewGuid(), "search", Path.GetTempPath(), false, new Dictionary<string, string>());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stalled_Primary_Request_Or_Body_Times_Out_And_Falls_Back(bool stallBody)
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<Uri>();
        CancellationToken primaryToken = default;
        using var client = new HttpClient(new AsyncHandler((request, token) =>
        {
            requests.Add(request.RequestUri!);
            if (!request.RequestUri!.Host.Contains("duckduckgo", StringComparison.Ordinal))
                return Task.FromResult(RssResponse());
            primaryToken = token;
            return stallBody
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent(body.Task) })
                : pending.Task;
        }));
        try
        {
            var response = await new DuckDuckGoSearchProvider(client, ShortTimeout).SearchAsync(Query, 8).WaitAsync(TestDeadline);

            response.Results.Should().ContainSingle(result => result.Source == "Bing");
            response.Attempts.Select(attempt => attempt.Status).Should().Equal("timeout", "success");
            response.Attempts[0].Detail.Should().Contain("timed out");
            response.Attempts[0].DurationMilliseconds.Should().BeGreaterThan(0);
            primaryToken.IsCancellationRequested.Should().BeTrue();
            requests.Should().HaveCount(2);
            requests.Should().OnlyContain(uri => Uri.UnescapeDataString(uri.Query).Contains(Query, StringComparison.Ordinal));
        }
        finally
        {
            pending.TrySetResult(RssResponse());
            body.TrySetResult();
        }
    }

    [Theory]
    [InlineData("http_timeout", "timeout")]
    [InlineData("network", "failed")]
    [InlineData("http_429", "failed")]
    public async Task Primary_Failure_Preserves_Diagnostics_And_Uses_Fallback(string failure, string status)
    {
        using var client = new HttpClient(new DelegateHttpHandler(request =>
        {
            if (!request.RequestUri!.Host.Contains("duckduckgo", StringComparison.Ordinal)) return RssResponse();
            return failure switch
            {
                "http_timeout" => throw new TaskCanceledException("HTTP client timeout"),
                "network" => throw new HttpRequestException("DNS unavailable"),
                _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") }
            };
        }));

        var response = await new DuckDuckGoSearchProvider(client).SearchAsync(Query);

        response.Results.Should().ContainSingle();
        response.Attempts.Select(attempt => attempt.Status).Should().Equal(status, "success");
        response.Attempts[0].Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Both_Providers_Stall_And_Tool_Returns_A_Failed_Structured_Report()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHandler((_, _) => pending.Task));
        var tool = new WebSearchTool(new DuckDuckGoSearchProvider(client, ShortTimeout));
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { query = Query, limit = 8 }));
        try
        {
            var result = await tool.ExecuteAsync(arguments.RootElement, Context).WaitAsync(TestDeadline);

            result.Success.Should().BeFalse();
            result.Error.Should().NotBeNullOrWhiteSpace();
            using var report = JsonDocument.Parse(result.Output);
            report.RootElement.GetProperty("code").GetString().Should().Be("search_timeout");
            report.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
            report.RootElement.GetProperty("attempts").EnumerateArray().Should().HaveCount(2)
                .And.OnlyContain(attempt => attempt.GetProperty("status").GetString() == "timeout");
        }
        finally { pending.TrySetCanceled(); }
    }

    [Fact]
    public async Task Tool_Deadline_Does_Not_Depend_On_Search_Provider_Cooperation()
    {
        var pending = new TaskCompletionSource<SearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        var tool = new WebSearchTool(new SearchProvider(token => { providerToken = token; return pending.Task; }), ShortTimeout);
        using var arguments = JsonDocument.Parse("""{"query":"forecast"}""");
        try
        {
            var result = await tool.ExecuteAsync(arguments.RootElement, Context).WaitAsync(TestDeadline);

            result.Success.Should().BeFalse();
            result.Metadata.Should().Contain("code", "search_timeout");
            providerToken.IsCancellationRequested.Should().BeTrue();
            result.Output.Should().Contain("timed out");
        }
        finally { pending.TrySetResult(new([], [])); }
    }

    [Fact]
    public async Task Unexpected_Provider_Exception_Is_Reported_To_The_Model()
    {
        var tool = new WebSearchTool(new SearchProvider(_ => throw new InvalidDataException("Invalid search response")));
        using var arguments = JsonDocument.Parse("""{"query":"forecast"}""");

        var result = await tool.ExecuteAsync(arguments.RootElement, Context);

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Invalid search response");
        result.Metadata.Should().Contain("code", "search_unavailable");
    }

    [Fact]
    public async Task Caller_Cancellation_Stops_Search_Without_Fallback_Or_Error_Conversion()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new AsyncHandler((_, _) => { calls++; started.TrySetResult(); return pending.Task; }));
        var tool = new WebSearchTool(new DuckDuckGoSearchProvider(client));
        using var arguments = JsonDocument.Parse("""{"query":"forecast"}""");
        try
        {
            var execution = tool.ExecuteAsync(arguments.RootElement, Context, cancellation.Token);
            await started.Task.WaitAsync(TestDeadline);
            await cancellation.CancelAsync();
            var act = () => execution.WaitAsync(TestDeadline);

            await act.Should().ThrowAsync<OperationCanceledException>();
            calls.Should().Be(1);
        }
        finally { pending.TrySetCanceled(); }
    }

    [Fact]
    public async Task Fetch_Deadline_Covers_Providers_That_Ignore_Cancellation()
    {
        var pending = new TaskCompletionSource<FetchedPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new WebFetchTool(new Fetcher(pending.Task), ShortTimeout);
        using var arguments = JsonDocument.Parse("""{"url":"https://example.org"}""");
        try
        {
            var result = await tool.ExecuteAsync(arguments.RootElement, Context).WaitAsync(TestDeadline);
            result.Success.Should().BeFalse();
            result.Error.Should().Contain("timed out");
        }
        finally { pending.TrySetCanceled(); }
    }

    [Fact]
    public async Task Executor_Converts_Provider_Cancellation_Into_A_Tool_Error()
    {
        var registry = new ToolRegistry([new CancelledTool()]);
        var executor = new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AllowOnce), NullLogger<ToolExecutor>.Instance);
        using var arguments = JsonDocument.Parse("{}");

        var result = await executor.ExecuteAsync("cancelled", arguments.RootElement, Context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("timed out or was cancelled by its provider");
    }

    private static HttpResponseMessage RssResponse() => new(HttpStatusCode.OK) { Content = new StringContent(Rss, Encoding.UTF8, "application/rss+xml") };
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
    private sealed class StalledContent(Task pending) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => pending;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class SearchProvider(Func<CancellationToken, Task<SearchResponse>> search) : ISearchProvider
    {
        public Task<SearchResponse> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default) => search(cancellationToken);
    }
    private sealed class Fetcher(Task<FetchedPage> pending) : IWebContentFetcher
    {
        public Task<FetchedPage> FetchAsync(Uri uri, CancellationToken cancellationToken = default) => pending;
    }
    private sealed class CancelledTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new("cancelled", "test", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(), ToolRisk.Safe, []);
        public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }
}
