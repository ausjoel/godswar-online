using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// F-143 (the admin 2026-11-09: "30g in mall or in some loot tables 0.5%"): the Item Mall sells the Magic Bell (4113) for
/// 30 Gold in both camps, and 15 listed bosses/elites drop one bell at 0.5% per kill, rolled in code (no loot rule).
/// </summary>
internal static class MagicBellSourceChecks
{
    public const string CheckName = "F-143 Magic Bell sources: Item Mall 30 Gold, 0.5% boss/elite drops rolled in code";

    private const int Deaths = 200_000;

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            CheckMall();
            CheckLoot();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckMall()
    {
        foreach (var camp in new[] { GameDefaults.AthensCamp, GameDefaults.SpartaCamp })
        {
            // Captured catalog: pets tab (category 3), index 8. Bought on the item-sweep rig 2026-11-04 at 30 Gold.
            Check.True(PacketBuilder.TryResolveMallCatalogOffer(camp, 3, 8, MagicBellLootRule.ItemId, out var offer) &&
                       offer.UnitPrice == 30 && offer.Currency == CapitalNpcShopCurrency.Gold,
                $"camp {camp}: the Item Mall sells the Magic Bell (category 3, index 8) for 30 Gold");
            Check.True(CapitalNpcServiceProtocol.MallCurrency == CapitalNpcShopCurrency.Gold, "the mall charges Gold");
        }
    }

    private static void CheckLoot()
    {
        Check.True(Finalise2Rates.On(Finalise2RateKeys.MagicBellDrops), "pet.magic_bell_drops defaults to on");
        Check.Equal(50, Finalise2Rates.Amount(Finalise2RateKeys.MagicBellDropChance), "drop chance defaults to 50 bp (0.5%)");
        Check.Equal(15, MagicBellLootRule.Templates.Count, "15 GENERATED boss/elite templates");
        Check.True(MagicBellLootRule.IsListed("A_boss_bull_001") && MagicBellLootRule.IsListed("A_boss_bull_001@11") &&
                   !MagicBellLootRule.IsListed("A_boss_bull_0011") && !MagicBellLootRule.IsListed("A_normal_stub_001"),
            "listed by template; an area table template@map counts, other templates do not");

        Install(dropChance: 3m, bellDrops: 1m, bellChance: 50m);
        var bells = Count("B_boss_dragon_001");
        // 0.5% of 200,000 = 1,000; drop.chance 3 (live) must not triple it.
        Check.True(bells is >= 880 and <= 1_120, $"listed boss: ~0.5% with drop.chance 3 ({bells}/{Deaths})");
        Check.Equal(0, Count("A_normal_stub_001"), "unlisted template: never");
        Check.True(Enumerable.Range(1, 1000).All(i => MagicBellLootRule.TryRoll("B_boss_dragon_001", Death(i), ServerRates.Current) ==
                       MagicBellLootRule.TryRoll("B_boss_dragon_001", Death(i), ServerRates.Current)),
            "deterministic per death (a replayed kill drops the same)");

        Install(dropChance: 3m, bellDrops: 1m, bellChance: 100m);
        var doubled = Count("B_boss_dragon_001");
        Check.True(doubled is >= 1_800 and <= 2_200, $"chance 100 bp: ~1% ({doubled}/{Deaths})");

        Install(dropChance: 3m, bellDrops: 0m, bellChance: 50m);
        Check.Equal(0, Count("B_boss_dragon_001"), "pet.magic_bell_drops 0: no bells");
        Console.WriteLine($"    F-143 bell rolls over {Deaths:N0} deaths: {bells} bells ({bells * 100.0 / Deaths:0.00}%)");
    }

    private static Guid Death(int index) => new(index, 143, 13, 1, 2, 3, 4, 5, 6, 7, 8);

    private static int Count(string template)
    {
        var rates = ServerRates.Current;
        var bells = 0;
        for (var index = 1; index <= Deaths; index++)
        {
            if (MagicBellLootRule.TryRoll(template, Death(index), rates)) bells++;
        }

        return bells;
    }

    private static void Install(decimal dropChance, decimal bellDrops, decimal bellChance) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [
                KeyValuePair.Create(ServerRateKeys.DropChance, dropChance),
                KeyValuePair.Create(Finalise2RateKeys.MagicBellDrops, bellDrops),
                KeyValuePair.Create(Finalise2RateKeys.MagicBellDropChance, bellChance)
            ],
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
