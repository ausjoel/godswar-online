using System.Security.Cryptography;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// B22 G2 (the admin d38: "rare gems from new maps"): the Aether Sanctum (map 70) drops the rare and epic gems that forge
/// Titan / Primordial / Aether gear from G20 to G24 (RareGemForgeRules). One roll per death, own salt, which colour from
/// the same hash (colour is cosmetic):
///   normals: Radiant at gem.radiant_normal_chance_bp (default 100 = 1%)
///   elites:  Primal at gem.primal_elite_chance_bp (500 = 5%), else Radiant at gem.radiant_elite_chance_bp (2500 = 25%)
///   bosses:  gem.primal_boss_count Primal gems (default 1)
/// A set of tier gear forged G20 -> G24 needs about 22 Radiant and 22 Primal (2 + 2 per piece; a failed forge keeps
/// the gem only if the forge says so). The G25 god gems stay with the open-map world bosses (gem.god_gem_drops).
/// Switch gem.sanctum_drops (default 1).
/// </summary>
internal static class SanctumGemLootRule
{
    public static readonly uint[] Radiant = [4902, 4903, 4904, 4905];
    public static readonly uint[] Primal = [4906, 4907, 4908, 4909];

    public static (uint ItemId, int Quantity)? TryRoll(CapturedMonsterSpawn monster, Guid deathEventId,
        ServerRatesSnapshot rates)
    {
        if (deathEventId == Guid.Empty ||
            monster.MapId != Godswar.Server.Application.WorldInstances.AetherSanctumPolicy.MapId ||
            rates.Get(Finalise2RateKeys.SanctumGemDrops) < 1m)
        {
            return null;
        }

        Span<byte> input = stackalloc byte[24];
        deathEventId.TryWriteBytes(input[..16]);
        "GemSanct"u8.CopyTo(input[16..]); // own salt: independent of the table, the medals and the god gems
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        var colour = (int)(BitConverter.ToUInt32(hash[8..]) % 4);
        var roll0 = BitConverter.ToUInt32(hash) % 10_000;
        var roll4 = BitConverter.ToUInt32(hash[4..]) % 10_000;
        bool Hit(uint roll, string key) => roll < (uint)Math.Clamp(rates.Get(key), 0m, 10_000m);

        return MedalLootRule.RankOf(monster.TemplateKey) switch
        {
            MedalLootRule.Rank.Boss when (int)Math.Clamp(rates.Get(Finalise2RateKeys.PrimalBossCount), 0m, 99m) is > 0 and var n =>
                (Primal[colour], n),
            MedalLootRule.Rank.Elite when Hit(roll0, Finalise2RateKeys.PrimalEliteChance) => (Primal[colour], 1),
            MedalLootRule.Rank.Elite when Hit(roll4, Finalise2RateKeys.RadiantEliteChance) => (Radiant[colour], 1),
            MedalLootRule.Rank.Normal when Hit(roll0, Finalise2RateKeys.RadiantNormalChance) => (Radiant[colour], 1),
            _ => null
        };
    }
}
