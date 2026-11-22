using Godswar.Server.Application.Limits;
using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W4F (wave 4 item 6): Original GodsWar vs Origin/Reborn limits switch.
internal static class GameLimitProfileChecks
{
    public const string CheckName = "W4F Original GodsWar / Origin limits switch";

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        var definition = ServerRateCatalog.Definitions.Single(static d => d.Key == GameLimitProfile.RateKey);
        Check.Equal(0m, definition.DefaultValue, "Origin/Reborn limits are the default");
        CheckProfile(original: false);
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(GameLimitProfile.RateKey, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            CheckProfile(original: true);
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckProfile(bool original)
    {
        var label = original ? "Original" : "Origin";
        Check.Equal(original, GameLimitProfile.Original, $"{label}: switch reads the hot rate");
        Check.Equal(original ? (short)10 : (short)20, GameLimitProfile.MaximumQuality(EquipmentForgeCalculator.MaximumQuality), $"{label}: quality cap");
        Check.Equal(original ? (short)12 : (short)25, GameLimitProfile.MaximumGrade(EquipmentForgeCalculator.MaximumGrade), $"{label}: grade cap");
        Check.Equal(original ? (short)2 : (short)4, GameLimitProfile.MaximumSockets(HolyStoneDrillEligibilityPolicy.MaximumSockets), $"{label}: socket cap");
        Check.Equal(!original, GameLimitProfile.HolySuitTargetAllowed(5), $"{label}: Platinum 10 -> Mithril 1");
        Check.True(GameLimitProfile.HolySuitTargetAllowed(4), $"{label}: Platinum stays allowed");
        Check.Equal(!original, GameLimitProfile.ClassSuitTierIVAllowed, $"{label}: Class Suit IV");

        // Sapphire on a Q10 item and Emerald on a G12 item: refused as out of range only under Original.
        var sapphire = new EquipmentForgeRequest(
            Item(1000, quality: 10),
            new EquipmentForgeMaterialSelection(Item(4210), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 25), 25));
        EquipmentForgeCalculator.TryCalculate(sapphire, out _, out var sapphireError);
        Check.Equal(original, sapphireError == EquipmentForgeValidationError.ProgressionOutOfRange,
            $"{label}: Sapphire past Q10 is out of range only under Original ({sapphireError})");
        var emerald = new EquipmentForgeRequest(
            Item(1000, grade: 12) with { Attribute1 = 24 },
            new EquipmentForgeMaterialSelection(Item(4220), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 20), 20));
        EquipmentForgeCalculator.TryCalculate(emerald, out _, out var emeraldError);
        Check.Equal(original, emeraldError == EquipmentForgeValidationError.ProgressionOutOfRange,
            $"{label}: Emerald past +12 is out of range only under Original ({emeraldError})");
    }

    private static CompactItemEntry Item(uint itemId, short quality = 1, short grade = 1, short stack = 1) =>
        CompactItemEntry.Empty with { Id = itemId, Quality = quality, Grade = grade, Stack = stack };
}
