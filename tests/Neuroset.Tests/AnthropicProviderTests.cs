using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Providers;

namespace Neuroset.Tests;

public sealed class AnthropicProviderTests
{
    private static ConnectionProfile Connection => new() { Name = "claude", BaseUrl = "https://gateway.test/v1", ApiKeySecretId = "secret", Model = "claude-opus-4-8", TimeoutSeconds = 10 };

    private static AnthropicMessagesProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new HttpClient(new DelegateHttpHandler(handler)), new MemorySecretStore(), NullLogger<AnthropicMessagesProvider>.Instance);

    [Fact]
    public async Task Streaming_Parses_Text_ToolCalls_And_Usage()
    {
        var sse = string.Join("\n",
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi \"}}",
            "",
            "event: content_block_start",
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"echo\"}}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"value\\\":\\\"x\\\"}\"}}",
            "",
            "event: message_delta",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}",
            "",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}",
            "") + "\n";
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var events = new List<ModelEvent>();
        await foreach (var item in provider.StreamAsync(new(Connection, [new(MessageRole.User, "hello")], [], true))) events.Add(item);

        events.OfType<TextDeltaEvent>().Select(x => x.Delta).Should().Contain("Hi ");
        events.OfType<ToolCallStartedEvent>().Should().ContainSingle(x => x.CallId == "toolu_1" && x.Name == "echo");
        events.OfType<ToolCallArgumentsEvent>().Should().ContainSingle(x => x.CallId == "toolu_1" && x.ArgumentsDelta.Contains("value"));
        events.OfType<UsageEvent>().Select(x => x.Usage.OutputTokens).Should().Contain(5);
        events.OfType<ModelCompletedEvent>().Should().ContainSingle(x => x.FinishReason == "tool_calls");
    }

    [Fact]
    public async Task Complete_Parses_Text_And_ToolCalls()
    {
        var json = """{"content":[{"type":"text","text":"done"},{"type":"tool_use","id":"toolu_2","name":"lookup","input":{"q":"x"}}],"stop_reason":"tool_use","usage":{"input_tokens":3,"output_tokens":4}}""";
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var result = await provider.CompleteAsync(new(Connection, [new(MessageRole.User, "hello")], [], false));

        result.Content.Should().Be("done");
        result.FinishReason.Should().Be("tool_calls");
        result.ToolCalls.Should().ContainSingle(x => x.Id == "toolu_2" && x.Name == "lookup" && x.ArgumentsJson.Contains("\"q\":\"x\""));
        result.Usage!.TotalTokens.Should().Be(7);
    }

    [Fact]
    public async Task Request_Targets_Messages_Endpoint_With_System_And_MaxTokens()
    {
        string? body = null;
        Uri? uri = null;
        var json = """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""";
        var provider = Provider(message =>
        {
            uri = message.RequestUri;
            body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var generation = new GenerationSettings { SystemPrompt = "ignored", MaxTokens = 555, Temperature = 1.7 };
        await provider.CompleteAsync(new(Connection, [new(MessageRole.System, "be terse"), new(MessageRole.User, "marker")], [], false, Generation: generation));

        uri!.AbsoluteUri.Should().Be("https://gateway.test/v1/messages");
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-opus-4-8");
        root.GetProperty("system").GetString().Should().Be("be terse");
        root.GetProperty("max_tokens").GetInt32().Should().Be(555);
        root.GetProperty("temperature").GetDouble().Should().BeLessThanOrEqualTo(1);
        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task Request_Emits_ToolUse_And_ToolResult_Blocks()
    {
        string? body = null;
        var json = """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""";
        var provider = Provider(message =>
        {
            body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        LlmMessage[] history =
        [
            new(MessageRole.User, "weather?"),
            new(MessageRole.Assistant, "checking", ToolCalls: [new("toolu_1", "get_weather", "{\"city\":\"Paris\"}")]),
            new(MessageRole.Tool, "20C", Name: "get_weather", ToolCallId: "toolu_1"),
        ];
        await provider.CompleteAsync(new(Connection, history, [], false));

        using var document = JsonDocument.Parse(body!);
        var messages = document.RootElement.GetProperty("messages");
        var assistant = messages.EnumerateArray().First(x => x.GetProperty("role").GetString() == "assistant");
        assistant.GetProperty("content").EnumerateArray().Should().Contain(block => block.GetProperty("type").GetString() == "tool_use");
        var toolResult = messages.EnumerateArray().SelectMany(x => x.GetProperty("content").EnumerateArray())
            .FirstOrDefault(block => block.TryGetProperty("type", out var t) && t.GetString() == "tool_result");
        toolResult.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        toolResult.GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
    }

    [Fact]
    public async Task Http_Error_Yields_Model_Error_Without_Throwing()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"bad model"}}""", Encoding.UTF8, "application/json") });
        var events = new List<ModelEvent>();
        await foreach (var item in provider.StreamAsync(new(Connection, [new(MessageRole.User, "hi")], [], true))) events.Add(item);
        events.OfType<ModelErrorEvent>().Should().ContainSingle(x => x.Code == "http_400" && x.Message.Contains("bad model"));
    }

    [Theory]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-opus-4.8", true)]
    [InlineData("anthropic/claude-3-5-sonnet", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("openai/gpt-4o", false)]
    [InlineData("", false)]
    public void Routing_Selects_Anthropic_For_Claude_Models(string model, bool expected) =>
        RoutingLlmProviderFactory.IsAnthropicModel(model).Should().Be(expected);
}
