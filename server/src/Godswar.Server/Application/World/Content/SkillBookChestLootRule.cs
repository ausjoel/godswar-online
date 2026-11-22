using System.Collections.Frozen;
using System.Security.Cryptography;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// B22 K1 (the admin d43): world-boss skill-book chests. Rolled in code like the Magic Bell (MagicBellLootRule): most of
/// these boss tables already use all 32 loot_index slots and a stored rule would lose to maximum_drops. A kill of one
/// of the 19 open-map world bosses (gameplay_world_boss_definitions) drops its tier's chest at
/// drop.skill_book_chest_chance_bp (default 500 = 5%, flat: drop.chance does not apply), switch drop.skill_book_chests.
/// Tier by boss level: up to 100 = chest 6520 (Lv6 books), 101-140 = 6521 (Lv7), 141-170 = 6522 (Lv8),
/// 171+ = 6523 (Lv9-10). Levels from the live spawn rows on 2026-11-21; the five marked (map) have no spawn row and
/// use their map's usual monster level (GENERATED estimate).
/// </summary>
internal static class SkillBookChestLootRule
{
    // template -> (its open map from gameplay_world_boss_definitions, chest). Only a kill on that map rolls.
    public static readonly FrozenDictionary<string, (short Map, uint Chest)> Chests =
        new Dictionary<string, (short Map, uint Chest)>(StringComparer.Ordinal)
    {
        ["A_boss_boar_001"] = (3, 6520),             // Boar King Tomas, map 3, ~Lv54 (map)
        ["A_boss_wolf_005"] = (5, 6520),             // Astrien, Lv56
        ["A_boss_bull_001"] = (11, 6520),             // Minos the Bull King, Lv47
        ["A_boss_spider_008"] = (13, 6520),           // Spider Queen Ala, Lv47
        ["B_bossB_octopus_001"] = (12, 6520),         // Naga Siren Eirsigel, Lv95
        ["B_bossB_spriggan_001"] = (14, 6520),        // Evil Treant Falio, Lv95
        ["A_boss_amazon_004"] = (16, 6520),           // Leader Cassirer, Lv93
        ["A_boss_mage_018"] = (18, 6520),             // Lord Barryonyx, map 18, ~Lv75 (map)
        ["B_boss_cyclops_001"] = (19, 6520),          // Giant Alcyoneus, map 19, ~Lv96 (map)
        ["B_bossB_xerxes_001"] = (8, 6521),          // Mardonius, map 8, ~Lv120 (boss_lv120 loot profile)
        ["B_boss_dragon_001"] = (17, 6521),           // Red Dragon Puluo, Lv120
        ["B_boss_centaur_001"] = (15, 6521),          // Centaur Shaikh Hailer, map 15, ~Lv109 (map)
        ["A_boss_kingofscorpiondi_001"] = (9, 6522), // Scorpion Lord Selket, Lv145
        ["A_boss_long_005"] = (20, 6522),             // Hydra Lord Xausa, Lv150
        ["C_boss_centaur_001"] = (7, 6522),          // Centaur Leader, Lv160
        ["A_boss_kingofscorpion_001"] = (6, 6522),   // Darkmist, Lv170
        ["C_boss_dragon_014"] = (10, 6523),           // Little Demate, Lv180
        ["C_boss_dragon_002"] = (22, 6523),           // Ice Dragon, Lv190
        ["C_boss_dragon_013"] = (21, 6523)       // Bahamut, Lv198
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool TryGetChest(string templateKey, short mapId, out uint chestItemId)
    {
        var at = templateKey.IndexOf('@');
        chestItemId = Chests.TryGetValue(at < 0 ? templateKey : templateKey[..at], out var entry) && entry.Map == mapId
            ? entry.Chest
            : 0;
        return chestItemId != 0;
    }

    public static int ChanceBasisPoints(ServerRatesSnapshot rates) =>
        (int)Math.Clamp(rates.Get(Finalise2RateKeys.SkillBookChestChance), 0m, 10_000m);

    /// <summary>Deterministic per death (a replay of the same kill drops the same), independent of the table roll.</summary>
    public static bool TryRoll(string templateKey, short mapId, Guid deathEventId, ServerRatesSnapshot rates, out uint chestItemId)
    {
        chestItemId = 0;
        if (deathEventId == Guid.Empty || rates.Get(Finalise2RateKeys.SkillBookChestDrops) < 1m ||
            !TryGetChest(templateKey, mapId, out var chest))
        {
            return false;
        }

        Span<byte> input = stackalloc byte[24];
        deathEventId.TryWriteBytes(input[..16]);
        "BookChst"u8.CopyTo(input[16..]); // salt: never the same hash as a table rule or the Magic Bell
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        if (BitConverter.ToUInt32(hash) % 10_000 >= ChanceBasisPoints(rates))
        {
            return false;
        }

        chestItemId = chest;
        return true;
    }

    /// <summary>Adds the chest in a free loot index with its roll chance (so the corpse cap keeps it as a rare drop).</summary>
    public static IReadOnlyList<RolledMonsterLoot> Add(IReadOnlyList<RolledMonsterLoot> drops, uint chestItemId, int chance)
    {
        var used = drops.Select(static drop => drop.LootIndex).ToHashSet();
        for (var index = CorpseLootLimit.LootIndexCount - 1; index >= 0; index--)
        {
            if (!used.Contains(index))
            {
                return [.. drops, new RolledMonsterLoot(index, chestItemId, 1, null, ChanceBasisPoints: chance)];
            }
        }

        return drops;
    }
}
