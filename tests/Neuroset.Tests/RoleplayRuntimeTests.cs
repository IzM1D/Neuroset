using System.Runtime.CompilerServices;
using FluentAssertions;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tests;

public sealed class RoleplayRuntimeTests
{
    [Fact]
    public async Task Prompt_Generator_Uses_Its_Own_System_Prompt_And_User_Description()
    {
        var provider = new ScriptedProvider([new("Finished character prompt", [], null, "stop")]);
        var runtime = new RoleplayRuntime(new FixedFactory(provider));

        var prompt = await runtime.GenerateCharacterPromptAsync("A reserved lunar cartographer", Connection);

        prompt.Should().Be("Finished character prompt");
        provider.CompleteRequests.Should().ContainSingle();
        provider.CompleteRequests[0].Messages.Should().Contain(message => message.Role == MessageRole.System && message.Content.Contains("roleplay characters", StringComparison.OrdinalIgnoreCase));
        provider.CompleteRequests[0].Messages.Should().Contain(message => message.Role == MessageRole.User && message.Content.Contains("A reserved lunar cartographer"));
        provider.CompleteRequests[0].Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task Character_Context_Treats_Other_Participants_As_Humans()
    {
        var mira = new RoleplayCharacter { Name = "Mira", SystemPrompt = "You are a curious astronomer." };
        var jon = new RoleplayCharacter { Name = "Jon", SystemPrompt = "You are an impatient pilot." };
        var provider = new ScriptedProvider([], "I saw it too.");
        var runtime = new RoleplayRuntime(new FixedFactory(provider));
        var conversation = new Conversation { Mode = ConversationMode.Roleplay, Roleplay = new RoleplaySettings { Characters = [mira, jon] } };
        var user = new ChatMessage { Role = MessageRole.User, Content = "What crossed the sky?" };
        var otherCharacter = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "A silver flare.",
            Metadata = new Dictionary<string, string>
            {
                [RoleplayMessageMetadata.CharacterId] = jon.Id.ToString("D"),
                [RoleplayMessageMetadata.CharacterName] = jon.Name
            }
        };

        var events = new List<ModelEvent>();
        await foreach (var modelEvent in runtime.StreamCharacterAsync(conversation, mira, [user, otherCharacter], Connection, new GenerationSettings { EnableTools = true })) events.Add(modelEvent);

        events.OfType<TextDeltaEvent>().Should().ContainSingle().Which.Delta.Should().Be("I saw it too.");
        provider.CompleteRequests.Should().BeEmpty();
        provider.StreamRequests.Should().ContainSingle();
        var request = provider.StreamRequests[0];
        request.Tools.Should().BeEmpty();
        request.Generation!.EnableTools.Should().BeFalse();
        request.Messages[0].Content.Should().Contain("only artificial intelligence").And.Contain("real human being");
        request.Messages.Should().Contain(message => message.Role == MessageRole.User && message.Content.Contains("[Jon]: A silver flare."));
    }

    [Fact]
    public async Task Scene_And_Player_Generators_Use_Their_Own_System_Prompts()
    {
        var provider = new ScriptedProvider([new("Scene brief", [], null, "stop"), new("Player sheet", [], null, "stop")]);
        var runtime = new RoleplayRuntime(new FixedFactory(provider));

        var scene = await runtime.GenerateCharacterPromptAsync("A storm-locked lighthouse", Connection, RoleplayPromptKind.Scene);
        var player = await runtime.GenerateCharacterPromptAsync("A tired radio operator", Connection, RoleplayPromptKind.Player);

        scene.Should().Be("Scene brief");
        player.Should().Be("Player sheet");
        provider.CompleteRequests[0].Messages[0].Content.Should().Contain("shared scene description");
        provider.CompleteRequests[0].Messages[1].Content.Should().Contain("A storm-locked lighthouse");
        provider.CompleteRequests[1].Messages[0].Content.Should().Contain("character a human player controls");
        provider.CompleteRequests[1].Messages[1].Content.Should().Contain("A tired radio operator");
    }

    [Fact]
    public async Task Character_Prompt_Contains_Scene_Player_Sheet_And_Every_Other_Character_Sheet()
    {
        var mira = new RoleplayCharacter { Name = "Mira", SystemPrompt = "You are a curious astronomer." };
        var jon = new RoleplayCharacter { Name = "Jon", SystemPrompt = "You are an impatient pilot." };
        var provider = new ScriptedProvider([], "Noted.");
        var runtime = new RoleplayRuntime(new FixedFactory(provider));
        var conversation = new Conversation
        {
            Mode = ConversationMode.Roleplay,
            Roleplay = new RoleplaySettings
            {
                Characters = [mira, jon],
                SceneDescription = "A storm-locked lighthouse on the night of the eclipse.",
                PlayerName = "Elias",
                PlayerDescription = "A tired radio operator who trusts nobody."
            }
        };
        var user = new ChatMessage { Role = MessageRole.User, Content = "What crossed the sky?" };

        await foreach (var _ in runtime.StreamCharacterAsync(conversation, mira, [user], Connection, new GenerationSettings { SystemPrompt = "Keep the pace slow." })) { }

        var systemPrompt = provider.StreamRequests[0].Messages[0].Content;
        systemPrompt.Should().Contain("You are Mira");
        systemPrompt.Should().Contain("A storm-locked lighthouse on the night of the eclipse.");
        systemPrompt.Should().Contain("You are a curious astronomer.");
        systemPrompt.Should().Contain("Elias").And.Contain("A tired radio operator who trusts nobody.");
        systemPrompt.Should().Contain("Jon").And.Contain("You are an impatient pilot.");
        systemPrompt.Should().Contain("Keep the pace slow.");
        systemPrompt.Should().Contain("only artificial intelligence").And.Contain("real human being");
        provider.StreamRequests[0].Messages.Should().Contain(message => message.Content == "[Elias]: What crossed the sky?");
    }

    [Fact]
    public async Task Empty_Scene_And_Player_Fields_Leave_The_Prompt_Free_Of_Invented_Content()
    {
        var mira = new RoleplayCharacter { Name = "Mira", SystemPrompt = "You are a curious astronomer." };
        var provider = new ScriptedProvider([], "Noted.");
        var runtime = new RoleplayRuntime(new FixedFactory(provider));
        var conversation = new Conversation { Mode = ConversationMode.Roleplay, Roleplay = new RoleplaySettings { Characters = [mira] } };
        var user = new ChatMessage { Role = MessageRole.User, Content = "Hello." };

        await foreach (var _ in runtime.StreamCharacterAsync(conversation, mira, [user], Connection, new GenerationSettings())) { }

        var systemPrompt = provider.StreamRequests[0].Messages[0].Content;
        systemPrompt.Should().NotContain("Scene:");
        systemPrompt.Should().NotContain("character sheet");
        systemPrompt.Should().NotContain("The other people in this scene");
        systemPrompt.Should().NotContain("Shared scene instructions");
        provider.StreamRequests[0].Messages.Should().Contain(message => message.Content == "[User]: Hello.");
    }

    [Fact]
    public void Roleplay_Agent_Options_Lock_Tools_Off()
    {
        var options = AgentRunOptions.ForConversation(new Conversation { Mode = ConversationMode.Roleplay }, new AppSettings { AgentMaxIterations = 20 });
        options.EnableTools.Should().BeFalse();
    }

    private static readonly ConnectionProfile Connection = new() { Name = "test", BaseUrl = "https://example.test/v1", Model = "model" };
    private sealed class FixedFactory(ILLMProvider provider) : ILLMProviderFactory { public ILLMProvider Create(ConnectionProfile connection) => provider; }

    private sealed class ScriptedProvider(IEnumerable<LlmResponse> completeResponses, string streamText = "") : ILLMProvider
    {
        private readonly Queue<LlmResponse> _responses = new(completeResponses);
        public string ProviderType => "test";
        public List<LlmRequest> CompleteRequests { get; } = [];
        public List<LlmRequest> StreamRequests { get; } = [];
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            CompleteRequests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamRequests.Add(request);
            yield return new TextDeltaEvent(streamText);
            yield return new ModelCompletedEvent("stop");
            await Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "ok", TimeSpan.Zero));
    }
}
