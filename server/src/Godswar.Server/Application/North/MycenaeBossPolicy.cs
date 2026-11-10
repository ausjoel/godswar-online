using System.Collections.Frozen;

namespace Godswar.Server.Application.North;

/// <summary>
/// FINALISE2 F-19 (the admin 2026-11-05): "Mycenae used to have 4x massive bosses that were kind of like wonderland bosses that
/// would refresh every 30-60 minutes and quest rewards for killing each that would give say 8 lv 3 crystals per quest".
/// The four Mycenae elite templates (each used by exactly one spawn; contract
/// Development/finalise2/contracts/mycenae-bosses.json) are those bosses. Their HP/attack/defence come from the north
/// data step (north sql/01, rank mycenae_boss), their loot from north sql/05. Here: the random 30-60 minute respawn and
/// the server-side item reward of one daily quest per boss (camp giver Nestor; the quests ship with the north camp quests
/// behind GODSWAR_NORTH_LOCAL_QUESTS=1, because the client needs their text). Amounts and window are GENERATED.
/// </summary>
internal static class MycenaeBossPolicy
{
    public const short MapId = 6;
    public static readonly TimeSpan MinimumRespawn = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumRespawn = TimeSpan.FromMinutes(60);

    /// <summary>Level 3 Crystal, 8 per boss quest (the admin's example, GENERATED).</summary>
    public const uint RewardItemId = 4232;
    public const int RewardQuantity = 8;

    public static FrozenSet<string> TemplateKeys { get; } = new[]
    {
        "A_elite_Ahrimanm_001",        // [Elite]Soul Harvester Maxxi
        "A_elite_greecewarriorm_001",  // [Elite]Theban Vanguard
        "A_elite_GuardCaptainm_001",   // [Elite]Theban Sharpshooter
        "A_elite_FireDragonm_001",     // [Elite]Hakaar
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Sparta 1813-1816 and Athens 1983-1986, one per boss in the order above.</summary>
    public static FrozenSet<uint> QuestIds { get; } =
        new uint[] { 1813, 1814, 1815, 1816, 1983, 1984, 1985, 1986 }.ToFrozenSet();

    public static bool IsBoss(short mapId, string templateKey) =>
        mapId == MapId && TemplateKeys.Contains(templateKey);

    /// <summary>A fresh random delay in [30, 60] minutes for a killed Mycenae boss.</summary>
    public static bool TryRespawnDelay(short mapId, string templateKey, out TimeSpan delay)
    {
        delay = default;
        if (!IsBoss(mapId, templateKey)) return false;
        var span = (long)(MaximumRespawn - MinimumRespawn).TotalSeconds;
        delay = MinimumRespawn + TimeSpan.FromSeconds(Random.Shared.NextInt64(span + 1));
        Console.WriteLine($"[mycenae-boss] killed template={templateKey} respawn_in={(int)delay.TotalSeconds}s");
        return true;
    }
}
