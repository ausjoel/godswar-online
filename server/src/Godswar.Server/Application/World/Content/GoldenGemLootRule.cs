using System.Security.Cryptography;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// W22 (the admin d141, 2026-11-22: "world boss drop only. 0.5-1% of the time"): the Golden Gem (4259, the Mount Feeder's
/// gear-upgrade material since W18) drops only from the 19 open-map world bosses, killed on their own map (the same
/// boss/map list as the skill-book chests, <see cref="SkillBookChestLootRule.Chests"/>). One gem at
/// drop.golden_gem_chance_bp (default 75 = 0.75%, flat: drop.chance does not apply); 0 = no Golden Gems.
/// Rolled in code like the chests (boss tables use all 32 loot_index slots), with its own salt.
/// </summary>
internal static class GoldenGemLootRule
{
    public const uint ItemId = 4259;

    public static int ChanceBasisPoints(ServerRatesSnapshot rates) =>
        (int)Math.Clamp(rates.Get(Finalise2RateKeys.GoldenGemDropChance), 0m, 10_000m);

    /// <summary>Deterministic per death (a replay of the same kill drops the same), independent of every other roll.</summary>
    public static bool TryRoll(string templateKey, short mapId, Guid deathEventId, ServerRatesSnapshot rates)
    {
        var chance = ChanceBasisPoints(rates);
        if (deathEventId == Guid.Empty || chance <= 0 ||
            !SkillBookChestLootRule.TryGetChest(templateKey, mapId, out _))
        {
            return false;
        }

        Span<byte> input = stackalloc byte[24];
        deathEventId.TryWriteBytes(input[..16]);
        "GoldGem1"u8.CopyTo(input[16..]); // own salt: never the same hash as the table, the chest or the god gems
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return BitConverter.ToUInt32(hash) % 10_000 < chance;
    }
}
