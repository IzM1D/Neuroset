using Neuroset.Domain;

namespace Neuroset.Application;

public static class RoleplayTurnOrder
{
    // The canonical cycle is the human participant followed by characters in their configured list order.
    public static string NormalizeSpeakerId(RoleplaySettings settings, string? speakerId)
    {
        if (speakerId?.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase) == true)
            return RoleplaySpeaker.User;

        return settings.Characters
            .FirstOrDefault(character => character.Id.ToString("D").Equals(speakerId, StringComparison.OrdinalIgnoreCase))?
            .Id.ToString("D") ?? RoleplaySpeaker.User;
    }

    public static string GetNextSpeakerId(RoleplaySettings settings, string? currentSpeakerId)
    {
        if (currentSpeakerId?.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase) == true)
            return settings.Characters.FirstOrDefault()?.Id.ToString("D") ?? RoleplaySpeaker.User;

        var currentIndex = settings.Characters.FindIndex(character =>
            character.Id.ToString("D").Equals(currentSpeakerId, StringComparison.OrdinalIgnoreCase));
        return currentIndex >= 0 && currentIndex + 1 < settings.Characters.Count
            ? settings.Characters[currentIndex + 1].Id.ToString("D")
            : RoleplaySpeaker.User;
    }
}
