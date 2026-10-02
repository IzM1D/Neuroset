using FluentAssertions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tests;

public sealed class GenerationSpeedPolicyTests
{
    [Fact]
    public void Fast_Mode_Covers_Low_Chats_Only()
    {
        GenerationSpeedPolicy.IsFastMode(ConversationMode.Chat, ReasoningEffort.Low).Should().BeTrue();
        GenerationSpeedPolicy.IsFastMode(ConversationMode.Chat, ReasoningPreference.Low).Should().BeTrue();
        GenerationSpeedPolicy.IsFastMode(ConversationMode.Chat, ReasoningEffort.High).Should().BeFalse();
        GenerationSpeedPolicy.IsFastMode(ConversationMode.Chat, ReasoningPreference.Auto).Should().BeFalse();
        GenerationSpeedPolicy.IsFastMode(ConversationMode.Coding, ReasoningEffort.Low).Should().BeFalse();
        GenerationSpeedPolicy.IsFastMode(ConversationMode.Roleplay, ReasoningEffort.Low).Should().BeFalse();
    }

    [Fact]
    public void Iterations_Are_Capped_But_Never_Zero()
    {
        GenerationSpeedPolicy.LimitIterations(12).Should().Be(GenerationSpeedPolicy.FastModeMaximumIterations);
        GenerationSpeedPolicy.LimitIterations(2).Should().Be(2);
        GenerationSpeedPolicy.LimitIterations(0).Should().Be(1);
    }

    [Fact]
    public void Planning_Tools_Are_Dropped_And_Real_Tools_Kept()
    {
        string[] tools = ["web_search", "request_work_budget", "run_python", "delegate_coding_task"];

        GenerationSpeedPolicy.WithoutPlanningTools(tools, tool => tool).Should().Equal("web_search", "run_python");
    }

    [Fact]
    public void A_Low_Chat_Loses_The_Automatic_Extension()
    {
        var settings = new AppSettings { AgentMaxIterations = 12, AgentHardMaxIterations = 64, ReasoningPreference = ReasoningPreference.Low };

        var options = AgentRunOptions.ForConversation(new Conversation { Mode = ConversationMode.Chat }, settings);

        options.MaximumIterations.Should().Be(GenerationSpeedPolicy.FastModeMaximumIterations);
        options.HardMaximumIterations.Should().Be(GenerationSpeedPolicy.FastModeMaximumIterations);
    }

    [Fact]
    public void A_Low_Coding_Chat_Keeps_Its_Full_Budget()
    {
        var settings = new AppSettings { CodingAgentMaxIterations = 256, ReasoningPreference = ReasoningPreference.Low };

        var options = AgentRunOptions.ForConversation(new Conversation { Mode = ConversationMode.Coding }, settings);

        options.MaximumIterations.Should().Be(24);
        options.HardMaximumIterations.Should().Be(256);
    }

    [Fact]
    public void A_Default_Chat_Keeps_Its_Full_Budget()
    {
        var settings = new AppSettings { AgentMaxIterations = 12, AgentHardMaxIterations = 64 };

        var options = AgentRunOptions.ForConversation(new Conversation { Mode = ConversationMode.Chat }, settings);

        options.MaximumIterations.Should().Be(12);
        options.HardMaximumIterations.Should().Be(64);
    }

    [Fact]
    public void The_Fast_Preamble_States_The_Real_Budget()
    {
        GenerationSpeedPolicy.BuildFastModePreamble(4).Should().Contain("4 tool iterations").And.Contain("no budget extension");
    }
}
