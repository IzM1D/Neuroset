using System.Net;
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

public sealed class ImageGenerationTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    private const string Arguments = """
        {"title":"Лес","prompt":"A misty pine forest at dawn, eye-level wide composition, soft directional light.",
        "filters":["natural green palette","detailed pine needles"],"negative_filters":["compression artifacts","watermark"],"size":"1536x1024"}
        """;

    [Theory]
    [InlineData("gpt-image-2", true)]
    [InlineData("gpt-image-2-2026-04-21", true)]
    [InlineData("openai/gpt-image-2", true)]
    [InlineData("gpt-image-1.5", true)]
    [InlineData("gpt-image-1-mini", true)]
    [InlineData("gpt-image-2.5-sunburst", true)]
    [InlineData("chatgpt-image-latest", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("gpt-4-vision-preview", false)]
    [InlineData("fake-gpt-image-2", false)]
    [InlineData("gpt-image-", false)]
    [InlineData("", false)]
    public void Discovers_Generation_Models_Without_Mistaking_Vision_For_Generation(string name, bool expected) =>
        ImageGenerationPolicy.IsImageModel(name).Should().Be(expected);

    [Fact]
    public void Prefers_Requested_Gpt_Image_2_And_Preserves_Api_Model_Id()
    {
        ImageGenerationPolicy.SelectModel(["gpt-4o", "gpt-image-1", "openai/gpt-image-2", "gpt-image-2.5-sunburst"])
            .Should().Be("openai/gpt-image-2");
        ImageGenerationPolicy.SelectModel(["gpt-4o", "gpt-4-vision-preview"]).Should().BeNull();
    }

    [Fact]
    public async Task Button_Forces_Conversation_Model_Then_Image_Tool_And_Preserves_Artifact()
    {
        using var harness = new ImageHarness();
        var events = await harness.RunAsync(generateImage: true);

        harness.Paths.Should().Equal("/v1/models", "/v1/chat/completions", "/v1/images/generations", "/v1/chat/completions");
        var conversationRequest = harness.ChatRequests[0];
        conversationRequest.GetProperty("model").GetString().Should().Be("conversation-model");
        conversationRequest.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString().Should().Be("image_generation");
        conversationRequest.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
        conversationRequest.GetProperty("tools").GetArrayLength().Should().Be(1);
        conversationRequest.GetProperty("messages").EnumerateArray().Should().Contain(message =>
            message.GetProperty("content").ToString().Contains(ImageGenerationPolicy.PromptInstructions, StringComparison.Ordinal));

        var imageRequest = harness.ImageRequests.Should().ContainSingle().Subject;
        imageRequest.GetProperty("model").GetString().Should().Be("gpt-image-2");
        imageRequest.GetProperty("prompt").GetString().Should().Contain("soft directional light")
            .And.Contain("{natural green palette}").And.Contain("-{compression artifacts}");
        imageRequest.GetProperty("size").GetString().Should().Be("1536x1024");
        imageRequest.GetProperty("output_format").GetString().Should().Be("png");
        imageRequest.TryGetProperty("response_format", out _).Should().BeFalse();
        imageRequest.TryGetProperty("negative_prompt", out _).Should().BeFalse();

        harness.ChatRequests[1].TryGetProperty("tools", out _).Should().BeFalse();
        harness.ChatRequests[1].TryGetProperty("tool_choice", out _).Should().BeFalse();
        var completed = events.OfType<ToolExecutionCompletedEvent>().Should().ContainSingle().Subject;
        completed.Succeeded.Should().BeTrue();
        var artifact = JsonSerializer.Deserialize<GeneratedImageArtifact>(completed.Output, DomainJson.Options)!;
        (await File.ReadAllBytesAsync(artifact.Path)).Should().Equal(Convert.FromBase64String(Png));
        artifact.Title.Should().Be("Лес");
        harness.Repository.Messages.SelectMany(message => message.ToolCalls).Should().ContainSingle(call =>
            call.Execution != null && call.Execution.Output == completed.Output && call.Execution.Metadata["artifact_path"] == artifact.Path);
        completed.Output.Should().NotContain(Png);
    }

    [Theory]
    [InlineData("image_generation")]
    [InlineData("generate_image")]
    [InlineData("edit_image")]
    public async Task Without_Button_An_Unsolicited_Image_Tool_Call_Is_Never_Executed(string toolName)
    {
        using var harness = new ImageHarness { UnsolicitedTool = toolName };
        var events = await harness.RunAsync(generateImage: false);
        harness.Paths.Should().NotContain("/v1/models").And.NotContain("/v1/images/generations");
        foreach (var request in harness.ChatRequests)
            if (request.TryGetProperty("tools", out var tools))
                tools.EnumerateArray().Should().NotContain(tool => tool.GetProperty("function").GetProperty("name").GetString() == "image_generation");
        events.OfType<ToolExecutionCompletedEvent>().Should().ContainSingle(result => !result.Succeeded);
        Directory.Exists(harness.Directory).Should().BeFalse();
    }

    [Fact]
    public async Task Authorization_Does_Not_Carry_Over_To_The_Next_Message()
    {
        using var harness = new ImageHarness();
        await harness.RunAsync(generateImage: true);
        await harness.RunAsync(generateImage: false);
        harness.ImageRequests.Should().ContainSingle();
        harness.ChatRequests[^1].GetProperty("tools").EnumerateArray().Should().NotContain(tool =>
            tool.GetProperty("function").GetProperty("name").GetString() == "image_generation");
    }

    [Fact]
    public async Task Explicit_Button_Can_Generate_When_General_Tools_Are_Disabled()
    {
        using var harness = new ImageHarness { GeneralToolsEnabled = false };
        (await harness.RunAsync(generateImage: true)).OfType<ToolExecutionCompletedEvent>().Should().ContainSingle(result => result.Succeeded);
        harness.ImageRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_Image_Model_Fails_Before_Chat_Or_Image_Request()
    {
        using var harness = new ImageHarness { Models = ["conversation-model", "gpt-4-vision-preview"] };
        (await harness.RunAsync(generateImage: true)).OfType<ModelErrorEvent>().Should().ContainSingle(error => error.Code == "image_generation_unavailable");
        harness.Paths.Should().Equal("/v1/models");
    }

    [Fact]
    public async Task Ignored_Forced_Tool_Choice_Is_Reported_As_Failure()
    {
        using var harness = new ImageHarness { IgnoreToolChoice = true };
        (await harness.RunAsync(generateImage: true)).OfType<ModelErrorEvent>().Should().ContainSingle(error => error.Code == "image_generation_required");
        harness.ImageRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Repeated_Image_Calls_Only_Send_One_Paid_Request()
    {
        using var harness = new ImageHarness { DuplicateImageCall = true };
        var events = await harness.RunAsync(generateImage: true);
        harness.ImageRequests.Should().ContainSingle();
        events.OfType<ToolExecutionCompletedEvent>().Should().ContainSingle(result => result.Succeeded)
            .And.ContainSingle(result => !result.Succeeded);
        harness.ChatRequests[^1].GetProperty("messages").EnumerateArray().Should().Contain(message =>
            message.GetProperty("content").ToString().Contains("The generated image is already displayed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Image_Api_Error_Is_Preserved_And_Not_Automatically_Retried()
    {
        using var harness = new ImageHarness { ImageStatus = HttpStatusCode.TooManyRequests };
        var events = await harness.RunAsync(generateImage: true);
        events.OfType<ToolExecutionCompletedEvent>().Should().ContainSingle(result => !result.Succeeded && result.Error!.Contains("quota", StringComparison.Ordinal));
        harness.ImageRequests.Should().ContainSingle();
        Directory.Exists(harness.Directory).Should().BeFalse();
        harness.ChatRequests[^1].TryGetProperty("tools", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"not base64\"}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"SGVsbG8=\"}]}")]
    public async Task Invalid_Image_Response_Does_Not_Create_An_Artifact(string response)
    {
        using var harness = new ImageHarness { ImageResponse = response };
        (await harness.RunAsync(generateImage: true)).OfType<ToolExecutionCompletedEvent>().Should().ContainSingle(result => !result.Succeeded);
        Directory.Exists(harness.Directory).Should().BeFalse();
    }

    [Fact]
    public async Task Direct_Tool_Execution_Also_Requires_Per_Message_Authorization()
    {
        using var harness = new ImageHarness();
        var context = new ToolExecutionContext(Guid.NewGuid(), "call", harness.Directory, true, new Dictionary<string, string> { ["image_generation"] = "true" });
        using var arguments = JsonDocument.Parse(Arguments);
        var tool = new ImageGenerationTool(harness.Provider, new(harness.Directory));
        (await tool.ExecuteAsync(arguments.RootElement, context)).Success.Should().BeFalse();
        var executor = new ToolExecutor(new ToolRegistry([tool]), new FixedPermissionService(PermissionDecision.AlwaysAllow), NullLogger<ToolExecutor>.Instance);
        (await executor.ExecuteAsync("image_generation", arguments.RootElement, context)).Success.Should().BeFalse();
        harness.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_Stops_Image_Request_Without_Saving_An_Artifact()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new ImageHarness { OnImageRequest = () => cancellation.Cancel() };
        Func<Task> run = async () => await harness.RunAsync(generateImage: true, cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        harness.ImageRequests.Should().ContainSingle();
        Directory.Exists(harness.Directory).Should().BeFalse();
    }

    private sealed class ImageHarness : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "NeurosetImageTests", Guid.NewGuid().ToString("N"));
        public string[] Models { get; init; } = ["conversation-model", "gpt-image-1", "gpt-image-2"];
        public string? UnsolicitedTool { get; init; }
        public bool IgnoreToolChoice { get; init; }
        public bool DuplicateImageCall { get; init; }
        public bool GeneralToolsEnabled { get; init; } = true;
        public HttpStatusCode ImageStatus { get; init; } = HttpStatusCode.OK;
        public string? ImageResponse { get; init; }
        public Action? OnImageRequest { get; init; }
        public List<string> Paths { get; } = [];
        public List<JsonElement> ChatRequests { get; } = [];
        public List<JsonElement> ImageRequests { get; } = [];
        public InMemoryConversationRepository Repository { get; } = new();
        public OpenAiCompatibleProvider Provider { get; }
        private readonly HttpClient _client;
        private readonly Conversation _conversation = new();

        public ImageHarness()
        {
            _client = new(new DelegateHttpHandler(Handle));
            Provider = new(_client, new MemorySecretStore(), NullLogger<OpenAiCompatibleProvider>.Instance);
        }

        public async Task<List<ModelEvent>> RunAsync(bool generateImage, CancellationToken cancellationToken = default)
        {
            var tool = new ImageGenerationTool(Provider, new(Directory));
            var registry = new ToolRegistry([tool, new EchoTool()]);
            var settings = new AppSettings { LongTermMemoryEnabled = false, Generation = new() { EnableTools = GeneralToolsEnabled } };
            var runtime = new AgentRuntime(new OpenAiCompatibleProviderFactory(Provider), registry,
                new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AlwaysAllow), NullLogger<ToolExecutor>.Instance),
                Repository, new InMemoryMemoryStore(), new InMemorySettingsRepository(settings), NullLogger<AgentRuntime>.Instance);
            await Repository.SaveAsync(_conversation, cancellationToken);
            await Repository.SaveMessageAsync(new() { ConversationId = _conversation.Id, Role = MessageRole.User, Content = "Нарисуй лес" }, cancellationToken);
            var connection = new ConnectionProfile { BaseUrl = "https://example.test/v1", Model = "conversation-model", ApiKeySecretId = "secret", Organization = "test-org" };
            var events = new List<ModelEvent>();
            await foreach (var item in runtime.RunAsync(new(_conversation, await Repository.GetMessagesAsync(_conversation.Id, cancellationToken), connection,
                new(MaximumIterations: 2, Reasoning: ReasoningPreference.Low), GenerateImage: generateImage), cancellationToken)) events.Add(item);
            return events;
        }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            request.Headers.Authorization!.Parameter.Should().Be("test-key");
            request.Headers.GetValues("OpenAI-Organization").Should().Equal("test-org");
            if (path == "/v1/models") return Json(new { data = Models.Select(id => new { id }) });
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = document.RootElement.Clone();
            if (path == "/v1/images/generations")
            {
                ImageRequests.Add(body);
                OnImageRequest?.Invoke();
                if (ImageStatus != HttpStatusCode.OK) return new(ImageStatus) { Content = new StringContent("{\"error\":{\"message\":\"Image quota exceeded\"}}") };
                return ImageResponse is not null ? new(HttpStatusCode.OK) { Content = new StringContent(ImageResponse) } : Json(new { data = new[] { new { b64_json = Png } } });
            }
            path.Should().Be("/v1/chat/completions");
            ChatRequests.Add(body);
            var forced = body.TryGetProperty("tool_choice", out var choice) && choice.ValueKind == JsonValueKind.Object;
            object delta;
            if ((!IgnoreToolChoice && forced) || (UnsolicitedTool is not null && ChatRequests.Count == 1))
            {
                var name = UnsolicitedTool ?? "image_generation";
                delta = new { tool_calls = Enumerable.Range(0, DuplicateImageCall ? 2 : 1).Select(index => new { index, id = $"image-{index}", type = "function", function = new { name, arguments = Arguments } }) };
            }
            else delta = new { content = "Готово." };
            var sse = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta } } }) + "\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        public void Dispose()
        {
            _client.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
