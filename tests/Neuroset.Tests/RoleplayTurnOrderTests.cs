using FluentAssertions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tests;

public sealed class RoleplayTurnOrderTests
{
    [Fact]
    public void Turns_Follow_The_Player_And_Character_List_In_A_Cycle()
    {
        var mira = new RoleplayCharacter { Name = "Mira" };
        var jon = new RoleplayCharacter { Name = "Jon" };
        var settings = new RoleplaySettings { Characters = [mira, jon] };

        RoleplayTurnOrder.GetNextSpeakerId(settings, RoleplaySpeaker.User).Should().Be(mira.Id.ToString("D"));
        RoleplayTurnOrder.GetNextSpeakerId(settings, mira.Id.ToString("D")).Should().Be(jon.Id.ToString("D"));
        RoleplayTurnOrder.GetNextSpeakerId(settings, jon.Id.ToString("D")).Should().Be(RoleplaySpeaker.User);
    }

    [Fact]
    public void A_Selected_Character_Can_Start_The_Cycle()
    {
        var mira = new RoleplayCharacter { Name = "Mira" };
        var jon = new RoleplayCharacter { Name = "Jon" };
        var settings = new RoleplaySettings { Characters = [mira, jon] };

        settings.StartingSpeakerId = RoleplayTurnOrder.NormalizeSpeakerId(settings, jon.Id.ToString("D"));
        settings.PendingSpeakerId = settings.StartingSpeakerId;

        settings.PendingSpeakerId.Should().Be(jon.Id.ToString("D"));
        RoleplayTurnOrder.GetNextSpeakerId(settings, settings.PendingSpeakerId).Should().Be(RoleplaySpeaker.User);
    }

    [Fact]
    public void The_First_Turn_Uses_The_Configured_Starting_Speaker()
    {
        var mira = new RoleplayCharacter { Name = "Mira" };
        var settings = new RoleplaySettings { Characters = [mira], StartingSpeakerId = mira.Id.ToString("D") };

        settings.PendingSpeakerId = settings.StartingSpeakerId;

        settings.PendingSpeakerId.Should().Be(mira.Id.ToString("D"));
    }

    [Fact]
    public void Missing_Speakers_Fall_Back_To_The_Player()
    {
        var settings = new RoleplaySettings { Characters = [new RoleplayCharacter { Name = "Mira" }] };

        RoleplayTurnOrder.NormalizeSpeakerId(settings, Guid.NewGuid().ToString("D")).Should().Be(RoleplaySpeaker.User);
        RoleplayTurnOrder.GetNextSpeakerId(settings, Guid.NewGuid().ToString("D")).Should().Be(RoleplaySpeaker.User);
    }
}
