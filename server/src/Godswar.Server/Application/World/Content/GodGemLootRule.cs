using System.Collections.Frozen;
using System.Security.Cryptography;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// B22 G1 (d38): the four boss-only "god gems" (Olympian Ruby/Topaz/Sapphire/Emerald, items 4910-4913, GENERATED in
/// the gear-tiers-v1 item family) drop from the 19 open-map world bosses. Rolled in code like the Magic Bell
/// (every boss table already uses all 32 loot_index slots), outside the table's cap: one gem at
/// gem.god_gem_drop_chance_bp (default 200 = 2%), which of the four from the same per-death hash.
/// Off by default (gem.god_gem_drops 0): turn it on once client 1.5.5+ (icons/names) is out.
/// </summary>
internal static class GodGemLootRule
{
    public static readonly uint[] ItemIds = [4910, 4911, 4912, 4913];

    /// <summary>The published world bosses (gameplay_world_boss_definitions on the 21 Nov rig copy, maps 3-22).</summary>
    public static readonly FrozenSet<string> Templates = new[]
    {
        "A_boss_boar_001", "A_boss_wolf_005", "A_boss_kingofscorpion_001", "C_boss_centaur_001",
        "B_bossB_xerxes_001", "A_boss_kingofscorpiondi_001", "C_boss_dragon_014", "A_boss_bull_001",
        "B_bossB_octopus_001", "A_boss_spider_008", "B_bossB_spriggan_001", "B_boss_centaur_001",
        "A_boss_amazon_004", "B_boss_dragon_001", "A_boss_mage_018", "B_boss_cyclops_001",
        "A_boss_long_005", "C_boss_dragon_013", "C_boss_dragon_002"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsListed(string templateKey)
    {
        var at = templateKey.IndexOf('@');
        return Templates.Contains(at < 0 ? templateKey : templateKey[..at]);
    }

    /// <summary>Deterministic per death; null = no gem.</summary>
    public static uint? TryRoll(string templateKey, Guid deathEventId, ServerRatesSnapshot rates)
    {
        if (deathEventId == Guid.Empty || !Godswar.Server.Application.Limits.EndgameSwitch.GodGemDrops(rates) /* W16 master */ || !IsListed(templateKey))
        {
            return null;
        }

        var chance = (int)Math.Clamp(rates.Get(Finalise2RateKeys.GodGemDropChance), 0m, 10_000m);
        Span<byte> input = stackalloc byte[24];
        deathEventId.TryWriteBytes(input[..16]);
        "GodGemB2"u8.CopyTo(input[16..]); // own salt: independent of the table and the Magic Bell
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        if (BitConverter.ToUInt32(hash) % 10_000 >= chance)
        {
            return null;
        }
        return ItemIds[BitConverter.ToUInt32(hash[4..]) % (uint)ItemIds.Length];
    }
}
