using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;

namespace Godswar.Server.ProtocolChecks;

// GUILDSTONE: GODSWAR_LOOT_FLAT_CHANCE_ITEMS lets listed items (4100) skip
// drop.chance; every other item, and every item while the list is empty,
// must roll exactly as before.
internal static class GuildStoneChanceChecks
{
    public const string CheckName =
        "GUILDSTONE flat-chance loot items (drop.chance skipped only for listed items)";

    private const int Deaths = 200_000;

    public static Task RunAsync()
    {
        try
        {
            CheckParse();
            CheckThresholds();
            CheckRolls();
        }
        finally
        {
            LootFlatChanceItems.OverrideForChecks(null);
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckParse()
    {
        Check.Equal(0, LootFlatChanceItems.Parse(null).Count, "unset list is empty");
        Check.Equal(0, LootFlatChanceItems.Parse("  ").Count, "blank list is empty");
        var parsed = LootFlatChanceItems.Parse(" 4100, x, 0, -5, 15 ,4100");
        Check.True(parsed.Count == 2 && parsed.Contains(4100u) && parsed.Contains(15u),
            "list keeps valid non-zero ids only (4100, 15)");
    }

    private static void CheckThresholds()
    {
        Install(3m);
        var rates = ServerRates.Current;
        LootFlatChanceItems.OverrideForChecks("");
        Check.Equal(3, LootFlatChanceItems.ThresholdBasisPoints(rates, 4100, 1), "list empty: 4100 at 1 bp x3 = 3 bp");
        Check.Equal(300, LootFlatChanceItems.ThresholdBasisPoints(rates, 4001, 100), "list empty: other item x3");
        Check.Equal(rates.MultiplyBasisPoints(ServerRateKeys.DropChance, 4_000),
            LootFlatChanceItems.ThresholdBasisPoints(rates, 4001, 4_000), "list empty: same as MultiplyBasisPoints (cap)");

        LootFlatChanceItems.OverrideForChecks("4100");
        Check.Equal(1, LootFlatChanceItems.ThresholdBasisPoints(rates, 4100, 1), "listed: 4100 at 1 bp = 1 bp (0.01%)");
        Check.Equal(10_000, LootFlatChanceItems.ThresholdBasisPoints(rates, 4100, 10_000), "listed: capped at 10000");
        Check.Equal(300, LootFlatChanceItems.ThresholdBasisPoints(rates, 4001, 100), "listed 4100: other item still x3");
    }

    private static void CheckRolls()
    {
        Install(3m);
        var snapshot = new MonsterLootContentSnapshot(
            [new MonsterLootTableRule("GS_boss", 2)],
            [
                new MonsterLootRule("GS_boss", 0, 4001, 2_500, 1, 1),
                new MonsterLootRule("GS_boss", 1, 4100, 1, 1, 1)
            ]);

        LootFlatChanceItems.OverrideForChecks("");
        var (stonesOff, otherOff) = Count(snapshot);
        LootFlatChanceItems.OverrideForChecks("4100");
        var (stonesOn, otherOn) = Count(snapshot);

        Check.Equal(otherOff, otherOn, "the 4001 rule drops identically with and without the list");
        // Expected 3 per 10,000 off (60) and 1 per 10,000 on (20) for 200,000 deaths.
        Check.True(stonesOff is >= 35 and <= 90, $"list empty: 4100 at 1 bp x3 ~0.03% ({stonesOff}/{Deaths})");
        Check.True(stonesOn is >= 8 and <= 35, $"listed: 4100 at 1 bp ~0.01% ({stonesOn}/{Deaths})");
        Check.True(stonesOn < stonesOff, "listed: fewer stones than with drop.chance applied");
        Console.WriteLine($"    GUILDSTONE rolls over {Deaths:N0} deaths: 4100 off={stonesOff} on={stonesOn}, 4001 {otherOn}");
    }

    private static (int Stones, int Other) Count(MonsterLootContentSnapshot snapshot)
    {
        var stones = 0;
        var other = 0;
        for (var index = 1; index <= Deaths; index++)
        {
            foreach (var drop in snapshot.RollLoot("GS_boss", new Guid(index, 41, 18, 1, 2, 3, 4, 5, 6, 7, 8)))
            {
                if (drop.ItemId == 4100) stones++;
                else other++;
            }
        }

        return (stones, other);
    }

    private static void Install(decimal dropChance) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.DropChance, dropChance)],
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
