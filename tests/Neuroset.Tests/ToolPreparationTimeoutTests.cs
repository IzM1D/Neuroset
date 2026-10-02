using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Providers;

namespace Neuroset.Tests;

public sealed class ToolPreparationTimeoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stalled_Tool_Stream_Either_Reports_Timeout_Or_Accepts_An_Already_Finished_Call(bool finished)
    {
        const string call = """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"search","function":{"name":"web_search","arguments":"{\"query\":\"DRAM forecast\"}"}}]}}]}""";
        const string finish = """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}""";
        var sse = call + "\n\n" + (finished ? finish + "\n\n" : string.Empty);
        using var client = new HttpClient(new DelegateHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream(sse))
        }));
        var provider = new OpenAiCompatibleProvider(client, new MemorySecretStore(), NullLogger<OpenAiCompatibleProvider>.Instance,
            toolStreamIdleTimeout: TimeSpan.FromMilliseconds(150));
        var request = new LlmRequest(new ConnectionProfile { Name = "test", BaseUrl = "https://example.test/v1", Model = "test" }, [new(MessageRole.User, "Search")], []);
        var events = new List<ModelEvent>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var collect = async () =>
        {
            await foreach (var item in provider.StreamAsync(request, deadline.Token)) events.Add(item);
        };

        if (finished)
        {
            await collect();
            events.OfType<ModelCompletedEvent>().Should().ContainSingle(item => item.FinishReason == "tool_calls");
            events.OfType<UsageEvent>().Should().ContainSingle(item => item.Usage.TotalTokens == 5);
        }
        else
        {
            await collect.Should().ThrowAsync<TimeoutException>().WithMessage("*preparation timed out*");
        }
        events.OfType<ToolCallArgumentsEvent>().Should().ContainSingle();
    }

    private sealed class StalledStream(string content) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(content));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Position < _prefix.Length) return await _prefix.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _prefix.Dispose(); base.Dispose(disposing); }
    }
}
