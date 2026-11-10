using System.Globalization;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// BAY2: shows a paid Bay Under Attack completion reward to this member: the
/// stored level and EXP, the level-up frames (same as the Lucky Gods claim),
/// the native EXP gain and one chat-log line. No pop-up.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>Refusal for a late joiner outside level 50-90 (chat log, fits one note field).</summary>
    private const string HarborAttackLevelBandNote =
        "Bay Under Attack is for levels 50-90. No daily entry was used.";

    private async Task ProjectHarborAttackRewardAsync(
        HarborAttackRewardProjection projection,
        CancellationToken cancellationToken)
    {
        var character = _character;
        var reward = projection.Reward;
        if (character is null || character.Id != reward.CharacterId || _session.IsDisconnected)
        {
            return;
        }

        var previousLevel = character.Level;
        character.Level = projection.StoredLevel;
        character.Experience = projection.StoredExperience;
        if (character.Level > previousLevel)
        {
            await RefreshLevelUpStatsAsync(character, cancellationToken);
        }
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        for (var level = previousLevel + 1; level <= character.Level; level++)
        {
            var maximum = PlayerExperienceCatalog.GetClientExperienceMaximum(
                level, character.FighterLevelSealed);
            var experience = level == character.Level ? character.Experience : 0;
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(LocalPlayerObjectId, level, maximum, experience,
                    character.MaxHp, character.CurrentHp, character.MaxMp, character.CurrentMp),
                cancellationToken,
                "HarborAttackRewardLevelUp");
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerLevelUp(CurrentPlayerObjectId, level, maximum, experience,
                    character.MaxHp, character.CurrentHp, character.MaxMp, character.CurrentMp),
                cancellationToken,
                _session,
                "HarborAttackRewardLevelUpWorld");
        }
        await _session.SendAsync(
            PacketBuilder.ExperienceGain(reward.ExperienceGranted, character.Experience),
            cancellationToken,
            "HarborAttackRewardExperience");
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken,
            "HarborAttackRewardStatus");
        if (character.Level > previousLevel)
        {
            await SendQuestLevelUpRefreshAsync(cancellationToken);
        }

        var cleared = (projection.ClearanceBasisPoints / 100).ToString(CultureInfo.InvariantCulture);
        var note = reward.ExperienceGranted > 0
            ? $"Bay Under Attack: {cleared}% cleared, +" +
                reward.ExperienceGranted.ToString("N0", CultureInfo.InvariantCulture) + " EXP."
            : $"Bay Under Attack: {cleared}% cleared. No EXP earned.";
        await _session.SendAsync(
            PacketBuilder.PersonalNotice(System.Text.Encoding.ASCII.GetBytes(note)),
            cancellationToken,
            "HarborAttackRewardNote");
        Console.WriteLine(
            $"[harbor] reward shown character={character.Name} exp=+{reward.ExperienceGranted} " +
            $"level={previousLevel}->{character.Level} exp-now={character.Experience}");
    }
}
