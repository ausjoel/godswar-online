using System.Security.Cryptography;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// B22 G2 (the admin d36): the two Class Suit point medals (GENERATED items of G1's gear-tiers-v1 family).
/// - Valour Laurel (4900): Lv140-170 bosses and elites on any map (the spawn's own level byte, the client key's
///   rank A_/B_/C_ + boss/elite), faction guards excluded. Elites drop one at medal.laurel_elite_chance_bp
///   (default 2500 = 25%), bosses always drop medal.laurel_boss_count (default 5). About 37 such spawns today
///   (Mycenae, Olympia, Thebes, Oracle of Delphi, the two suburbs' wraiths).
/// - Aether Crest (4901): every monster of the Lv170-200 instance Aether Sanctum (AetherSanctumPolicy):
///   normals medal.crest_normal_chance_bp (1000 = 10%), elites always 1, bosses medal.crest_boss_count (5).
///   Sanctum bosses come back after 60 minutes and elites after 15 (MonsterRespawnPolicy), so a party camping the
///   whole map tops out near 2 x 5 + 9 x 4 = 46 Crests an hour plus the normals (885k HP each); an Aether set is ~110.
/// Pays for: a worn set (about 11 pieces, sum of insignia points about 22) costs about 110 Laurels (Titan),
/// 176 Laurels (Primordial) and 110 Crests (Aether); see ClassSuitTierUpgradeCatalog.
/// Rolled in code outside the loot table's cap (like the god gems): one deterministic roll per death.
/// Switch medal.drops (default 1 = on).
/// </summary>
internal static class MedalLootRule
{
    public const uint ValourLaurel = 4900;
    public const uint AetherCrest = 4901;
    public const int LaurelMinimumLevel = 140;
    public const int LaurelMaximumLevel = 170;

    public enum Rank { None, Normal, Elite, Boss }

    /// <summary>The client key's rank: A_/B_/C_ (any case) + normal / elite / boss.</summary>
    public static Rank RankOf(string templateKey)
    {
        if (templateKey.Length < 3 || templateKey[1] != '_' || char.ToUpperInvariant(templateKey[0]) is not ('A' or 'B' or 'C'))
        {
            return Rank.None;
        }

        var rank = templateKey.AsSpan(2);
        return rank.StartsWith("boss", StringComparison.OrdinalIgnoreCase) ? Rank.Boss
            : rank.StartsWith("elite", StringComparison.OrdinalIgnoreCase) ? Rank.Elite
            : rank.StartsWith("normal", StringComparison.OrdinalIgnoreCase) ? Rank.Normal
            : Rank.None;
    }

    /// <summary>Deterministic per death; null = no medal.</summary>
    public static (uint ItemId, int Quantity)? TryRoll(CapturedMonsterSpawn monster, Guid deathEventId,
        ServerRatesSnapshot rates)
    {
        if (deathEventId == Guid.Empty || rates.Get(Finalise2RateKeys.MedalDrops) < 1m)
        {
            return null;
        }

        var rank = RankOf(monster.TemplateKey);
        if (monster.MapId == Godswar.Server.Application.WorldInstances.AetherSanctumPolicy.MapId)
        {
            return rank switch
            {
                Rank.Boss => (AetherCrest, Count(rates, Finalise2RateKeys.MedalCrestBossCount)),
                Rank.Elite => (AetherCrest, 1),
                Rank.Normal when Hit(deathEventId, rates, Finalise2RateKeys.MedalCrestNormalChance) => (AetherCrest, 1),
                _ => null
            };
        }

        if (monster.FactionCamp is 0 or 1 || monster.Tier is < LaurelMinimumLevel or > LaurelMaximumLevel)
        {
            return null;
        }

        return rank switch
        {
            Rank.Boss => (ValourLaurel, Count(rates, Finalise2RateKeys.MedalLaurelBossCount)),
            Rank.Elite when Hit(deathEventId, rates, Finalise2RateKeys.MedalLaurelEliteChance) => (ValourLaurel, 1),
            _ => null
        };
    }

    private static int Count(ServerRatesSnapshot rates, string key) =>
        (int)Math.Clamp(rates.Get(key), 0m, 99m);

    private static bool Hit(Guid deathEventId, ServerRatesSnapshot rates, string key)
    {
        var chance = (int)Math.Clamp(rates.Get(key), 0m, 10_000m);
        Span<byte> input = stackalloc byte[24];
        deathEventId.TryWriteBytes(input[..16]);
        "MedalB22"u8.CopyTo(input[16..]); // own salt: independent of the table, the bell and the god gems
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return BitConverter.ToUInt32(hash) % 10_000 < chance;
    }
}
