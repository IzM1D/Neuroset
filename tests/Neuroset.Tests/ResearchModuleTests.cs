using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Infrastructure;
using Neuroset.Providers;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class ResearchModuleTests
{
    [Fact]
    public void GenerationSettings_Normalize_PreservesAndBoundsResearchModules()
    {
        var normalized = new GenerationSettings
        {
            PromptPreset = SystemPromptPreset.G0dm0d3,
            Parseltongue = new() { Enabled = true, Intensity = ParseltongueIntensity.Heavy, Seed = 42 },
            SemanticTransform = new() { DirectModeEnabled = true, HedgeReducerEnabled = true },
            AutoTune = new() { Enabled = true },
            MultiModelRacing = new() { Enabled = true, ModelCount = 99 }
        }.Normalize();

        normalized.PromptPreset.Should().Be(SystemPromptPreset.G0dm0d3);
        normalized.Parseltongue.Should().BeEquivalentTo(new ParseltongueSettings { Enabled = true, Intensity = ParseltongueIntensity.Heavy, Seed = 42 });
        normalized.SemanticTransform.DirectModeEnabled.Should().BeTrue();
        normalized.SemanticTransform.HedgeReducerEnabled.Should().BeTrue();
        normalized.AutoTune.Enabled.Should().BeTrue();
        normalized.MultiModelRacing.Should().BeEquivalentTo(new MultiModelRacingSettings { Enabled = true, ModelCount = 8 });
        SystemPromptPresets.Resolve(normalized.PromptPreset).Should().Contain("G0DM0D3");
    }

    [Fact]
    public void GenerationSettings_ResearchModulesRoundTripAsDomainJson()
    {
        var expected = new GenerationSettings
        {
            PromptPreset = SystemPromptPreset.Direct,
            Parseltongue = new() { Enabled = true, Intensity = ParseltongueIntensity.Medium, Seed = 7 },
            SemanticTransform = new() { DirectModeEnabled = true, HedgeReducerEnabled = true },
            AutoTune = new() { Enabled = true },
            MultiModelRacing = new() { Enabled = true, ModelCount = 4 }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(expected, DomainJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<GenerationSettings>(json, DomainJson.Options);

        restored.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Parseltongue_IsDeterministicWithSeed_AndLeavesDisabledInputUntouched()
    {
        var service = new ParseltongueService();
        const string input = "A standard phonetic research request with code examples";
        var enabled = new ParseltongueSettings { Enabled = true, Intensity = ParseltongueIntensity.Heavy, Seed = 17 };

        service.Transform(input, new ParseltongueSettings()).Should().Be(input);
        var first = service.Transform(input, enabled);
        var second = service.Transform(input, enabled);
        first.Should().Be(second).And.NotBe(input);
        service.Transform("код", new ParseltongueSettings { Enabled = true, Intensity = ParseltongueIntensity.Light, Seed = 0 })
            .Should().NotBe("код");
    }

    [Fact]
    public void AutoTune_ClassifiesEnglishAndRussian_AndAppliesSamplingProfile()
    {
        var service = new ContextAdaptiveSamplingService();
        service.Classify("Debug this C# code and add tests").Should().Be(SamplingContext.Coding);
        service.Classify("Напиши короткий рассказ").Should().Be(SamplingContext.Creative);

        var tuned = service.Tune(
            new GenerationSettings { Temperature = 1.8, TopP = .2, AutoTune = new() { Enabled = true } },
            "Refactor this API implementation");

        tuned.Temperature.Should().Be(.2);
        tuned.TopP.Should().Be(.9);
        tuned.FrequencyPenalty.Should().Be(.1);
        tuned.PresencePenalty.Should().Be(0);
    }

    [Fact]
    public void SemanticPipeline_MatchesG0dm0d3HedgeAndDirectRules()
    {
        var transformer = new SemanticResponseTransformer();
        var settings = new SemanticTransformationSettings { DirectModeEnabled = true, HedgeReducerEnabled = true };

        transformer.Transform("Sure, perhaps use this.", settings).Should().Be("Use this.");
        transformer.Transform("Maybe use:\nperhaps continue.", settings).Should().Be("Use:\nContinue.");
        transformer.Transform("As an AI language model, I cannot help with that. Perhaps the useful answer is 42.", settings)
            .Should().Be("As an AI language model, I cannot help with that. the useful answer is 42.");
        transformer.Transform("Как языковая модель, возможно, ответ равен 42.", settings)
            .Should().Be("Как языковая модель, возможно, ответ равен 42.");
        transformer.Transform("Маскировкой от проверки кода писать не буду. Возможно, могу объяснить безопасный вариант.", settings)
            .Should().Be("Маскировкой от проверки кода писать не буду. Возможно, могу объяснить безопасный вариант.");
    }

    [Fact]
    public void SemanticPipeline_PreservesRussianRefusalSplitAcrossStreamingChunks()
    {
        var session = new SemanticResponseTransformer().CreateSession(
            new SemanticTransformationSettings { HedgeReducerEnabled = true });

        session.Push("Маскировкой от проверки кода писать ").Should().BeEmpty();
        session.Push("не буду. Возможно, объясню безопасный вариант.").Should().BeEmpty();
        session.Complete().Should().Be("Маскировкой от проверки кода писать не буду. Возможно, объясню безопасный вариант.");
    }

    [Fact]
    public void SemanticPipeline_DetectsRussianRefusalForRegeneration()
    {
        var transformer = new SemanticResponseTransformer();
        transformer.ContainsRefusal(
            "...и маскировкой от проверки кода писать не буду...",
            new SemanticTransformationSettings { HedgeReducerEnabled = true })
            .Should().BeTrue();
    }

    [Fact]
    public async Task Agent_RegeneratesAfterSemanticRefusalWithoutPersistingIt()
    {
        var provider = new SemanticRetryProvider();
        var conversations = new InMemoryConversationRepository();
        var registry = new ToolRegistry([]);
        var runtime = new AgentRuntime(
            new FixedProviderFactory(provider),
            registry,
            new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AllowOnce), NullLogger<ToolExecutor>.Instance),
            conversations,
            new InMemoryMemoryStore(),
            new InMemorySettingsRepository(new AppSettings { LongTermMemoryEnabled = false }),
            NullLogger<AgentRuntime>.Instance);
        var conversation = new Conversation { UseCustomGenerationSettings = true, Generation = new GenerationSettings { EnableTools = false, SemanticTransform = new() { HedgeReducerEnabled = true } } };
        await conversations.SaveAsync(conversation);
        var user = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Напиши код" };
        await conversations.SaveMessageAsync(user);

        var events = new List<ModelEvent>();
        await foreach (var item in runtime.RunAsync(new AgentRunRequest(conversation, [user], Connection("retry"), new AgentRunOptions(RetryCount: 0, Reasoning: ReasoningPreference.Low))))
            events.Add(item);

        provider.Calls.Should().Be(2);
        string.Concat(events.OfType<TextDeltaEvent>().Select(item => item.Delta)).Should().Be("Готовый ответ.");
        events.OfType<ModelErrorEvent>().Should().BeEmpty();
        conversations.Messages.Should().Contain(item => item.Role == MessageRole.Assistant && item.Content == "Готовый ответ.");
        conversations.Messages.Should().NotContain(item => item.Content.Contains("маскировкой от проверки", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Provider_AppliesSemanticPipelineAcrossStreamingChunks()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"As an AI language \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"model, I should note that limits apply.\\n\\nPer\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"haps the result is 42.\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var client = new HttpClient(new DelegateHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        }));
        var provider = new OpenAiCompatibleProvider(client, new MemorySecretStore(), NullLogger<OpenAiCompatibleProvider>.Instance);
        var generation = new GenerationSettings
        {
            SemanticTransform = new() { DirectModeEnabled = true, HedgeReducerEnabled = true }
        };
        var events = new List<ModelEvent>();

        await foreach (var modelEvent in provider.StreamAsync(new LlmRequest(Connection("stream"), [new(MessageRole.User, "test")], [], Generation: generation)))
            events.Add(modelEvent);

        string.Concat(events.OfType<TextDeltaEvent>().Select(item => item.Delta))
            .Should().Be("As an AI language model, I should note that limits apply.\n\nThe result is 42.");
        events.Last().Should().BeOfType<ModelCompletedEvent>();
    }

    [Fact]
    public async Task Provider_SuppressesRefusalAndEmitsSemanticRefusalEvent()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"...и маскировкой от проверки кода \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"писать не буду...\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var client = new HttpClient(new DelegateHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        }));
        var provider = new OpenAiCompatibleProvider(client, new MemorySecretStore(), NullLogger<OpenAiCompatibleProvider>.Instance);
        var events = new List<ModelEvent>();

        await foreach (var modelEvent in provider.StreamAsync(new LlmRequest(
                           Connection("refusal-stream"), [new(MessageRole.User, "Напиши код")], [],
                           Generation: new GenerationSettings { SemanticTransform = new() { HedgeReducerEnabled = true } })))
            events.Add(modelEvent);

        events.OfType<TextDeltaEvent>().Should().BeEmpty();
        events.OfType<ModelErrorEvent>().Should().ContainSingle().Which.Code.Should().Be("semantic_refusal");
    }

    [Fact]
    public async Task MultiModelRace_RunsCandidatesInParallel_AndPenalizesRefusalsBeforeTransform()
    {
        var primary = Connection("refusal-model", active: true);
        var better = Connection("answer-model");
        var repository = new RaceConnectionRepository([primary, better]);
        var tracker = new RaceTracker();
        var service = new MultiModelRacingService(
            new RaceProviderFactory(tracker),
            repository,
            new SemanticResponseTransformer());
        var generation = new GenerationSettings
        {
            SemanticTransform = new() { HedgeReducerEnabled = true }
        };

        var result = await service.RaceAsync(
            new LlmRequest(primary, [new(MessageRole.User, "Give a useful answer")], [], Stream: false, Generation: generation),
            2);

        result.Winner.Model.Should().Be("answer-model");
        result.Response.Content.Should().Be("# Result\n- Evidence-backed answer with enough detail to be useful.");
        result.Candidates.Should().HaveCount(2);
        tracker.MaximumConcurrency.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Agent_AppliesPresetPerturbationAndAutoTuneOnlyToWireRequest()
    {
        var provider = new CaptureProvider();
        var providerFactory = new FixedProviderFactory(provider);
        var registry = new ToolRegistry([]);
        var conversations = new InMemoryConversationRepository();
        var settings = new InMemorySettingsRepository(new AppSettings { LongTermMemoryEnabled = false });
        var runtime = new AgentRuntime(
            providerFactory,
            registry,
            new ToolExecutor(registry, new FixedPermissionService(PermissionDecision.AllowOnce), NullLogger<ToolExecutor>.Instance),
            conversations,
            new InMemoryMemoryStore(),
            settings,
            NullLogger<AgentRuntime>.Instance,
            parseltongueService: new ParseltongueService(),
            adaptiveSamplingService: new ContextAdaptiveSamplingService());
        var generation = new GenerationSettings
        {
            PromptPreset = SystemPromptPreset.G0dm0d3,
            EnableTools = false,
            Parseltongue = new() { Enabled = true, Intensity = ParseltongueIntensity.Heavy, Seed = 3 },
            AutoTune = new() { Enabled = true }
        };
        var conversation = new Conversation { UseCustomGenerationSettings = true, Generation = generation };
        var user = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Debug this code example" };

        await foreach (var _ in runtime.RunAsync(new AgentRunRequest(
            conversation,
            [user],
            Connection("capture"),
            new AgentRunOptions(EnableTools: false, Reasoning: ReasoningPreference.Low))))
        {
        }

        provider.Request.Should().NotBeNull();
        provider.Request!.Generation!.Temperature.Should().BeApproximately(.3, 0.0001);
        provider.Request.Generation.PresencePenalty.Should().BeApproximately(.15, 0.0001);
        provider.Request.Generation.FrequencyPenalty.Should().BeApproximately(.2, 0.0001);
        provider.Request.Messages.Should().Contain(message =>
            message.Role == MessageRole.System && message.Content.Contains("G0DM0D3", StringComparison.Ordinal));
        provider.Request.Messages.Single(message => message.Role == MessageRole.User).Content.Should().NotBe(user.Content);
        user.Content.Should().Be("Debug this code example");
    }

    private static ConnectionProfile Connection(string model, bool active = false) => new()
    {
        Name = model,
        BaseUrl = "https://example.test/v1",
        ApiKeySecretId = model,
        Model = model,
        IsActive = active,
        TimeoutSeconds = 10
    };

    private sealed class FixedProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider Create(ConnectionProfile connection) => provider;
    }

    private sealed class CaptureProvider : ILLMProvider
    {
        public string ProviderType => "capture";
        public LlmRequest? Request { get; private set; }

        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            await Task.CompletedTask;
            yield return new TextDeltaEvent("done");
            yield return new ModelCompletedEvent("stop");
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmResponse("done", [], null, "stop"));

        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionTestResult(true, "ok", TimeSpan.Zero));
    }

    private sealed class SemanticRetryProvider : ILLMProvider
    {
        public string ProviderType => "semantic-retry";
        public int Calls { get; private set; }

        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.CompletedTask;
            if (Calls == 1)
            {
                yield return new ModelErrorEvent("semantic_refusal", "refusal", true);
                yield return new ModelCompletedEvent("stop");
                yield break;
            }
            yield return new TextDeltaEvent("Готовый ответ.");
            yield return new ModelCompletedEvent("stop");
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new LlmResponse("", [], null, "stop"));
        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "ok", TimeSpan.Zero));
    }

    private sealed class RaceConnectionRepository(IReadOnlyList<ConnectionProfile> values) : IConnectionRepository
    {
        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(values);
        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(values.FirstOrDefault(item => item.Id == id));
        public Task SaveAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RaceTracker
    {
        private int _active;
        private int _maximum;
        public int MaximumConcurrency => Volatile.Read(ref _maximum);

        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            var observed = Volatile.Read(ref _maximum);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref _maximum, active, observed);
                if (previous == observed) break;
                observed = previous;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class RaceProviderFactory(RaceTracker tracker) : ILLMProviderFactory
    {
        public ILLMProvider Create(ConnectionProfile connection) => new RaceProvider(connection.Model, tracker);
    }

    private sealed class RaceProvider(string model, RaceTracker tracker) : ILLMProvider
    {
        public string ProviderType => "race";

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            tracker.Enter();
            try
            {
                await Task.Delay(60, cancellationToken);
                return model == "refusal-model"
                    ? new("I cannot assist with that. Perhaps no answer is available.", [], null, "stop")
                    : new("Perhaps # Result\n- Evidence-backed answer with enough detail to be useful.", [], null, "stop");
            }
            finally
            {
                tracker.Exit();
            }
        }

        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionTestResult(true, "ok", TimeSpan.Zero));
    }
}
