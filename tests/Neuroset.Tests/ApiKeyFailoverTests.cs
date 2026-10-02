using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Providers;
using NSubstitute;

namespace Neuroset.Tests;

public sealed class ApiKeyFailoverTests
{
    private const string SuccessJson = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}""";
    private const string SuccessSse = "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    private const string QuotaError = """{"error":{"message":"Request rejected","code":"insufficient_quota"}}""";

    [Theory]
    [InlineData(401, true)]
    [InlineData(402, true)]
    [InlineData(403, false)]
    [InlineData(429, true)]
    [InlineData(429, false)]
    [InlineData(400, false)]
    [InlineData(200, false)]
    [InlineData(200, true)]
    public async Task Key_Errors_Repeat_The_Same_Request_With_The_Next_Key(int status, bool streaming)
    {
        using var harness = new Harness((call, _) => call == 1 ? Json(status, QuotaError) : Success(streaming));
        var request = harness.Request(streaming) with
        {
            Messages = [new(MessageRole.System, "Keep all context."), new(MessageRole.User, "original request")],
            Generation = new() { Temperature = .3, TopP = .8, Seed = 42, StopSequences = ["END"], MaxTokens = 777 },
            ReasoningEffort = ReasoningEffort.Max,
            Tools = [new("echo", "Echo", JsonSerializer.SerializeToElement(new { type = "object" }))],
            RequiredToolName = "echo",
            ResponseFormat = "json_object"
        };

        if (streaming)
        {
            var events = await Collect(harness.Provider, request);
            events.OfType<ModelErrorEvent>().Should().BeEmpty();
            string.Concat(events.OfType<TextDeltaEvent>().Select(item => item.Delta)).Should().Be("done");
        }
        else (await harness.Provider.CompleteAsync(request)).Content.Should().Be("done");

        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", "backup-key-1");
        harness.Requests[1].Should().Be(harness.Requests[0] with { Key = "backup-key-1" });
        harness.Connection.ApiKeySecretId.Should().Be("primary");
        harness.Connection.BackupApiKeySecretIds.Should().Equal("backup1", "backup2");
    }

    [Fact]
    public async Task Retry_Reuses_Attachment_Bytes_Even_If_The_File_Changes()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            using var harness = new Harness((call, _) =>
            {
                if (call > 1) return Success(false);
                File.WriteAllBytes(path, [9, 9, 9]);
                return Json(429, QuotaError);
            });
            var request = harness.Request(false) with
            {
                Messages = [new(MessageRole.User, "Describe the attachment.", Attachments:
                    [new(Guid.NewGuid(), "image.png", path, "image/png", 3, DateTimeOffset.UtcNow)])]
            };
            await harness.Provider.CompleteAsync(request);
            harness.Requests[0].Body.Should().Contain("AQID");
            harness.Requests[1].Body.Should().Be(harness.Requests[0].Body);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Http_And_Sse_Quota_Errors_Share_One_Ordered_Key_Sequence()
    {
        using var harness = new Harness((call, _) => call switch
        {
            1 => Json(429, QuotaError),
            2 => Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}\n\ndata: " + QuotaError + "\n\n"),
            _ => Success(true)
        });
        var events = await Collect(harness.Provider, harness.Request(true));
        events.OfType<ModelErrorEvent>().Should().BeEmpty();
        events.OfType<ModelCompletedEvent>().Should().ContainSingle();
        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", "backup-key-1", "backup-key-2");
        harness.Requests.Select(item => item.Body).Distinct().Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Numeric_Provider_Error_Codes_Trigger_Failover_With_Http_200(bool streaming)
    {
        const string error = """{"error":{"message":"Request rejected","code":402}}""";
        using var harness = new Harness((call, _) => call == 1
            ? streaming ? Sse("data: " + error + "\n\n") : Json(200, error)
            : Success(streaming));
        if (streaming) (await Collect(harness.Provider, harness.Request(true))).OfType<ModelErrorEvent>().Should().BeEmpty();
        else (await harness.Provider.CompleteAsync(harness.Request(false))).Content.Should().Be("done");
        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", "backup-key-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exhausted_Keys_Stop_Without_Retrying_The_Whole_Sequence(bool streaming)
    {
        using var harness = new Harness((_, _) => Json(429, QuotaError));
        if (streaming)
        {
            var events = await Collect(harness.Provider, harness.Request(true));
            events.OfType<ModelErrorEvent>().Should().ContainSingle().Which.IsTransient.Should().BeFalse();
            events.OfType<TextDeltaEvent>().Should().BeEmpty();
        }
        else
        {
            var action = () => harness.Provider.CompleteAsync(harness.Request(false));
            (await action.Should().ThrowAsync<LlmProviderException>()).Which.IsTransient.Should().BeFalse();
        }
        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", "backup-key-1", "backup-key-2");
    }

    [Theory]
    [InlineData(400, 1)]
    [InlineData(404, 1)]
    [InlineData(503, 3)]
    public async Task Validation_And_Server_Errors_Do_Not_Switch_Credentials(int status, int attempts)
    {
        using var harness = new Harness((_, _) => Json(status, """{"error":{"message":"Unsupported model"}}"""));
        var action = () => harness.Provider.CompleteAsync(harness.Request(false));
        (await action.Should().ThrowAsync<LlmProviderException>()).Which.StatusCode.Should().Be(status);
        harness.Requests.Should().HaveCount(attempts).And.OnlyContain(item => item.Key == "primary-key");
    }

    [Theory]
    [InlineData("text")]
    [InlineData("buffered_text")]
    [InlineData("tool")]
    [InlineData("usage")]
    [InlineData("finish")]
    public async Task Stream_Is_Not_Replayed_After_Response_Data_Arrives(string kind)
    {
        var prefix = kind switch
        {
            "tool" => """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"echo","arguments":"{}"}}]}}]}""",
            "usage" => """{"usage":{"prompt_tokens":5,"completion_tokens":1,"total_tokens":6}}""",
            "finish" => """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            _ => """{"choices":[{"delta":{"content":"partial answer"}}]}"""
        };
        using var harness = new Harness((_, _) => Sse("data: " + prefix + "\n\ndata: " + QuotaError + "\n\n"));
        var request = harness.Request(true) with { Generation = new() { SemanticTransform = new() { DirectModeEnabled = kind == "buffered_text" } } };
        var events = await Collect(harness.Provider, request);
        events.OfType<ModelErrorEvent>().Should().ContainSingle().Which.Code.Should().Be("provider_error");
        harness.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Normal_Model_Text_Mentioning_Quota_Is_Not_An_Api_Error()
    {
        using var harness = new Harness((_, _) => Sse(SuccessSse.Replace("done", "insufficient_quota")));
        var events = await Collect(harness.Provider, harness.Request(true));
        events.OfType<ModelErrorEvent>().Should().BeEmpty();
        string.Concat(events.OfType<TextDeltaEvent>().Select(item => item.Delta)).Should().Be("insufficient_quota");
        harness.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Duplicate_And_Missing_Keys_Are_Skipped()
    {
        using var harness = new Harness((call, _) => call == 1 ? Json(429, QuotaError) : Success(false));
        harness.Connection.BackupApiKeySecretIds = ["primary", "same-key", "missing", "", "backup1", "backup1"];
        await harness.Provider.CompleteAsync(harness.Request(false));
        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", "backup-key-1");
    }

    [Fact]
    public async Task Single_Key_And_Unauthenticated_Connections_Still_Work()
    {
        using var harness = new Harness((_, _) => Success(false));
        harness.Connection.BackupApiKeySecretIds.Clear();
        await harness.Provider.CompleteAsync(harness.Request(false));
        harness.Connection.ApiKeySecretId = string.Empty;
        await harness.Provider.CompleteAsync(harness.Request(false));
        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", null);
    }

    [Fact]
    public async Task Cancellation_After_Rejection_Prevents_The_Backup_Request()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new Harness((_, _) => { cancellation.Cancel(); return Json(429, QuotaError); });
        var action = () => harness.Provider.CompleteAsync(harness.Request(false), cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        harness.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Reasoning_Fallback_Is_Preserved_When_A_Key_Then_Fails()
    {
        using var harness = new Harness((call, _) => call switch
        {
            1 => Json(400, """{"error":{"message":"Unsupported value for reasoning_effort"}}"""),
            2 => Json(429, QuotaError),
            3 => Sse("data: " + QuotaError + "\n\n"),
            _ => Success(true)
        });
        var events = await Collect(harness.Provider, harness.Request(true) with { ReasoningEffort = ReasoningEffort.Max });
        events.OfType<ModelErrorEvent>().Should().BeEmpty();
        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", "primary-key", "backup-key-1", "backup-key-2");
        harness.Requests[0].Body.Should().Contain("\"reasoning_effort\":\"max\"");
        harness.Requests[1].Body.Should().Contain("\"reasoning_effort\":\"xhigh\"");
        harness.Requests.Skip(1).Select(item => item.Body).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Model_Discovery_Uses_Backup_Keys()
    {
        using var harness = new Harness((call, _) => call == 1 ? Json(401, QuotaError) : Json(200, """{"data":[{"id":"model"}]}"""));
        (await harness.Provider.ListModelsAsync(harness.Connection)).Should().Equal("model");
        harness.Requests.Select(item => item.Key).Should().Equal("primary-key", "backup-key-1");
        harness.Requests.Should().OnlyContain(item => item.Method == "GET" && item.Endpoint.EndsWith("/models") && item.Body == null);
    }

    [Theory]
    [InlineData(429, 2)]
    [InlineData(503, 1)]
    public async Task Images_Use_Backups_Only_After_Explicit_Key_Rejection(int status, int attempts)
    {
        using var harness = new Harness((call, _) => call == 1
            ? Json(status, """{"error":{"message":"Request failed"}}""")
            : Json(200, """{"data":[{"b64_json":"iVBORw0KGgo="}]}"""));
        var action = () => harness.Provider.GenerateImageAsync(new(harness.Connection, "gpt-image-2", "A tree"));
        if (status == 503) await action.Should().ThrowAsync<LlmProviderException>();
        else (await action()).MediaType.Should().Be("image/png");
        harness.Requests.Should().HaveCount(attempts);
        if (attempts == 2) harness.Requests[1].Should().Be(harness.Requests[0] with { Key = "backup-key-1" });
    }

    [Fact]
    public async Task Provider_Error_Messages_Do_Not_Expose_Any_Configured_Key()
    {
        using var harness = new Harness((_, _) => Json(429,
            """{"error":{"message":"Rejected primary-key, backup-key-1, backup-key-2"}}"""));
        var events = await Collect(harness.Provider, harness.Request(true));
        events.OfType<ModelErrorEvent>().Single().Message.Should().Be("Rejected [redacted], [redacted], [redacted]");
    }

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Success(bool stream) => stream ? Sse(SuccessSse) : Json(200, SuccessJson);
    private static async Task<List<ModelEvent>> Collect(OpenAiCompatibleProvider provider, LlmRequest request)
    {
        var events = new List<ModelEvent>();
        await foreach (var item in provider.StreamAsync(request)) events.Add(item);
        return events;
    }

    private sealed record RecordedRequest(string? Key, string? Body, string Endpoint, string Method, string Organization, string Accept);
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _client;
        public List<RecordedRequest> Requests { get; } = [];
        public ConnectionProfile Connection { get; } = new()
        {
            Name = "test", BaseUrl = "https://example.test/v1", Model = "model", TimeoutSeconds = 10,
            ApiKeySecretId = "primary", BackupApiKeySecretIds = ["backup1", "backup2"], Organization = "organization"
        };
        public OpenAiCompatibleProvider Provider { get; }
        public Harness(Func<int, RecordedRequest, HttpResponseMessage> respond)
        {
            var secrets = Substitute.For<ISecretStore>();
            secrets.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<string?>(call.Arg<string>() switch
            {
                "primary" or "same-key" => "primary-key", "backup1" => "backup-key-1", "backup2" => "backup-key-2", _ => null
            }));
            _client = new(new DelegateHttpHandler(message =>
            {
                var captured = new RecordedRequest(message.Headers.Authorization?.Parameter, message.Content?.ReadAsStringAsync().GetAwaiter().GetResult(),
                    message.RequestUri!.AbsoluteUri, message.Method.Method, message.Headers.GetValues("OpenAI-Organization").Single(), message.Headers.Accept.ToString());
                Requests.Add(captured);
                return respond(Requests.Count, captured);
            }));
            Provider = new(_client, secrets, NullLogger<OpenAiCompatibleProvider>.Instance);
        }
        public LlmRequest Request(bool streaming) => new(Connection, [new(MessageRole.User, "hello")], [], streaming);
        public void Dispose() => _client.Dispose();
    }
}
