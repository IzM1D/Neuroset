using FluentAssertions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tests;

public sealed class ConversationMessageGroupingTests
{
    [Fact]
    public void Consecutive_Assistant_Tool_Iterations_Are_One_Visible_Response()
    {
        var user = new ChatMessage { Role = MessageRole.User, Content = "Find the answer" };
        var failedAttempt = new ChatMessage
        {
            Role = MessageRole.Assistant,
            ParentMessageId = user.Id,
            ToolCalls = [ToolCall("first", succeeded: false)]
        };
        var retry = new ChatMessage
        {
            Role = MessageRole.Assistant,
            ParentMessageId = failedAttempt.Id,
            ToolCalls = [ToolCall("second", succeeded: true)]
        };
        var final = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "Final answer",
            ParentMessageId = retry.Id
        };

        var groups = ConversationMessageGrouping.GroupVisibleResponses([user, failedAttempt, retry, final]);

        groups.Should().HaveCount(2);
        groups[0].Should().ContainSingle().Which.Should().BeSameAs(user);
        groups[1].Should().Equal(failedAttempt, retry, final);
    }

    [Fact]
    public void A_New_User_Message_Starts_A_New_Assistant_Response()
    {
        var firstUser = new ChatMessage { Role = MessageRole.User, Content = "First" };
        var firstAssistant = new ChatMessage { Role = MessageRole.Assistant, Content = "One" };
        var secondUser = new ChatMessage { Role = MessageRole.User, Content = "Second" };
        var secondAssistant = new ChatMessage { Role = MessageRole.Assistant, Content = "Two" };

        var groups = ConversationMessageGrouping.GroupVisibleResponses([firstUser, firstAssistant, secondUser, secondAssistant]);

        groups.Should().HaveCount(4);
        groups.Select(group => group[0]).Should().Equal(firstUser, firstAssistant, secondUser, secondAssistant);
    }

    [Fact]
    public void Separate_Roleplay_Character_Turns_Remain_Separate_Visible_Messages()
    {
        var first = RoleplayMessage("Mira", Guid.NewGuid());
        var second = RoleplayMessage("Jon", Guid.NewGuid());

        var groups = ConversationMessageGrouping.GroupVisibleResponses([first, second]);

        groups.Should().HaveCount(2);
        groups[0].Should().ContainSingle().Which.Should().BeSameAs(first);
        groups[1].Should().ContainSingle().Which.Should().BeSameAs(second);
    }

    private static ToolCallRecord ToolCall(string id, bool succeeded) => new()
    {
        Id = id,
        Name = "test_tool",
        ArgumentsJson = "{}",
        Execution = new ToolExecutionRecord
        {
            ToolCallId = id,
            ToolName = "test_tool",
            Succeeded = succeeded,
            Output = succeeded ? "ok" : string.Empty,
            Error = succeeded ? null : "failed"
        }
    };

    private static ChatMessage RoleplayMessage(string name, Guid characterId) => new()
    {
        Role = MessageRole.Assistant,
        Content = name,
        Metadata = new Dictionary<string, string>
        {
            [RoleplayMessageMetadata.CharacterId] = characterId.ToString("D"),
            [RoleplayMessageMetadata.CharacterName] = name,
            [RoleplayMessageMetadata.ResponseGroupId] = Guid.NewGuid().ToString("D")
        }
    };
}
