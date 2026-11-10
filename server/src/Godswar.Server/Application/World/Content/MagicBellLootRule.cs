using System.Collections.Frozen;
using System.Security.Cryptography;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// W3 Magic Bell drops (F-143, the admin 2026-11-09: "30g in mall or in some loot tables 0.5%"). Rolled in code, not
/// stored as a loot rule: on live every one of these tables already uses all 32 loot_index slots (CHECK 0..31; the
/// rig's copy of live refused an appended rule), and a rule after the others would also lose to maximum_drops on
/// 92-100% of kills. So a kill of one of the templates below (any map, also its template@map area table) drops one
/// Magic Bell at pet.magic_bell_drop_chance_bp (default 50 = 0.5%, flat: drop.chance and drop.quantity do not apply),
/// on top of and outside its table's cap. Switch pet.magic_bell_drops (0 = never). Read at each kill; no SQL.
/// </summary>
internal static class MagicBellLootRule
{
    public const uint ItemId = 4113;

    /// <summary>
    /// GENERATED: 15 named mid/high-level bosses and elites with live spawns (Development/drops/live-spawns-full-20261102.csv).
    /// </summary>
    public static readonly FrozenSet<string> Templates = new[]
    {
        "A_boss_bull_001",            // Minos the Bull King, Lv47, map 11
        "A_boss_spider_008",          // Spider Queen Ala, Lv47, map 13
        "A_boss_wolf_005",            // Astrien, Lv56, map 5
        "A_boss_amazon_004",          // Leader Cassirer, Lv93, map 16
        "B_boss_dragon_001",          // Red Dragon Puluo, Lv120, map 17
        "C_boss_centaur_001",         // Centaur Leader, Lv166, map 7
        "C_boss_stymphalianbird_001", // Stymphalian Bird, Lv174, map 20
        "A_elite_dryad_003",          // Knight Skaru, Lv74, map 5
        "A_elite_mage_010",           // Dark Spirit Yones, Lv75-76, maps 16/18
        "A_elite_robber_004",         // Predatus, Lv80-81, maps 16/18
        "A_elite_turtle_002",         // Storm Turtle Read, Lv85, map 16
        "A_elite_lion_005",           // Aldau, Lv90, map 17
        "A_elite_skeleton_014",       // Bloody Lord Wikido, Lv95, map 16
        "B_elite_mage_011",           // Fire Lord Neo, Lv106, map 17
        "B_elite_persiawarrior_002"   // Warrior Ktuoma, Lv106, map 17
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsListed(string templateKey)
    {
        var at = templateKey.IndexOf('@');
        return Templates.Contains(at < 0 ? templateKey : templateKey[..at]);
    }

    /// <summary>Deterministic per death (a replay of the same kill drops the same), independent of the table roll.</summary>
    public static bool TryRoll(string templateKey, Guid deathEventId, ServerRatesSnapshot rates)
    {
        if (deathEventId == Guid.Empty || rates.Get(Finalise2RateKeys.MagicBellDrops) < 1m || !IsListed(templateKey))
        {
            return false;
        }

        var chance = (int)Math.Clamp(rates.Get(Finalise2RateKeys.MagicBellDropChance), 0m, 10_000m);
        Span<byte> input = stackalloc byte[24];
        deathEventId.TryWriteBytes(input[..16]);
        "MagicBel"u8.CopyTo(input[16..]); // salt: never the same hash as a table rule (loot_index 0-31)
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return BitConverter.ToUInt32(hash) % 10_000 < chance;
    }
}
