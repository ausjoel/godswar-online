using Godswar.Server.Application.Rates;

namespace Godswar.Server.Game;

/// <summary>
/// FINALISE2 P5 (the admin F-63): info-window lines for kills and pet EXP, and per-tab routing. The quest reward line
/// is P2's (F-24, branch f2-p2-quests-north): on merge it should call SendInfoLineAsync(text, InfoTabQuest, token)
/// so that it lands in the Quest tab on client 1.4.2.
/// <para>
/// Tabs of the lower-right info window (Specialmsg.xml filter panel, 1-based): 1 Trade, 2 Quest, 3 Team, 4 PK,
/// 5 Guild, 6 Other, 7 Pet, 8 Zodiac. Client 1.4.2's SrvMsg.lua reads a leading "#n#" and files the line under tab n.
/// An older SrvMsg.lua would print the prefix literally, so the server only prefixes on a connection whose client
/// declared the feature: 1.4.2 sends one NPCFUN frame with tag 0x49 (value 0x49010000, version 1 in bits 16-23)
/// whenever it shows a personal note without a prefix (the first line after login, and any stock note the server
/// sends without one). A repeated declaration is ignored.
/// </para>
/// Each line has its own hot rate (log.kill_gains, log.pet_gains; 1 = on, 0 = off).
/// </summary>
internal sealed partial class GameClientHandler
{
    internal const byte InfoTabsDeclareTag = 0x49;
    internal const int InfoTabQuest = 2;
    internal const int InfoTabOther = 6;
    internal const int InfoTabPet = 7;

    // FINALISE2: true once this connection's client said it understands "#n#" prefixes.
    private bool _infoTabsDeclared;

    private void HandleInfoTabsDeclaration(uint value)
    {
        var version = (int)((value >> 16) & 0xFF);
        if (version < 1 || _infoTabsDeclared) return;
        _infoTabsDeclared = true;
        Console.WriteLine($"[gain-log] info tabs declared account={_account?.Id ?? 0} version={version}");
    }

    private static bool RateOn(string key) => ServerRates.Current.Absolute(key) == 1;

    /// <summary>After a committed kill reward: one line for the hero's EXP and one for the pet's.</summary>
    private async Task LogKillGainAsync(
        string monsterName,
        int experience,
        int talentExperience,
        bool partyShare,
        int petExperience,
        CancellationToken token)
    {
        if (experience > 0 && RateOn(ServerRateKeys.KillGainLog))
        {
            var who = string.IsNullOrWhiteSpace(monsterName) ? "a monster" : monsterName.Trim();
            var line = (partyShare ? $"Party kill ({who}): " : $"Killed {who}: ") + $"+{Amount(experience)} EXP";
            if (talentExperience > 0) line += $", +{Amount(talentExperience)} talent EXP";
            await SendInfoLineAsync(line, InfoTabOther, token);
        }

        if (petExperience > 0 && RateOn(ServerRateKeys.PetGainLog))
        {
            await SendInfoLineAsync($"Pet: +{Amount(petExperience)} pet EXP", InfoTabPet, token);
        }
    }
}
