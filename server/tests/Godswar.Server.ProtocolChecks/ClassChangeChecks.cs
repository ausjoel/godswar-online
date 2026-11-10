using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rates;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// CLASSCHANGE: the Class Shifter's rules, its gate, the talent epoch and its first-use schema.
internal static class ClassChangeChecks
{
    public const string CheckName = "Class Shifter maps the client buttons, is off by default and refunds talents at the upgrade price";

    public static Task RunAsync()
    {
        // NpcFunWork.lua page two: 110 Warrior, 111 Champion, 112 Mage, 113 Priest;
        // server professions 0 Warrior, 1 Champion, 2 Priest, 3 Mage.
        byte[] expected = [0, 1, 3, 2];
        for (var click = 110; click <= 113; click++)
        {
            Check.True(ClassChangePolicy.TryResolveTargetProfession(click, out var profession), $"button {click} is a class");
            Check.Equal(expected[click - 110], profession, $"button {click} profession");
        }

        Check.True(!ClassChangePolicy.TryResolveTargetProfession(114, out _), "114 (permit slot page) is not a class");
        Check.True(!ClassChangePolicy.TryResolveTargetProfession(116, out _), "weapon buttons are not classes");

        Check.Equal(112, ClassChangePolicy.ResolveClassClick(100, 112), "captured click: 100 in +16, class in +20");
        Check.Equal(113, ClassChangePolicy.ResolveClassClick(113, -1), "class in +16 alone");
        Check.Equal(-1, ClassChangePolicy.ResolveClassClick(100, -1), "page-one entry is not a class click");
        Check.Equal(-1, ClassChangePolicy.ResolveClassClick(101, 112), "class number under the weapon page is ignored");
        Check.Equal(-1, ClassChangePolicy.ResolveClassClick(100, 114), "permit form is not a class click");
        Check.Equal(-1, ClassChangePolicy.ResolveClassClick(-1, -1), "first-page request is not a class click");

        var defaults = ServerRatesSnapshot.Default;
        Check.True(!ClassChangePolicy.IsEnabled(null, defaults), "off by default");
        Check.True(!ClassChangePolicy.IsEnabled("false", defaults), "env false keeps it off");
        Check.True(ClassChangePolicy.IsEnabled("true", defaults), "env true turns it on");
        Check.True(ClassChangePolicy.IsEnabled("1", defaults), "env 1 turns it on");
        var on = ServerRatesSnapshot.Create(
            [new(ServerRateKeys.ClassChangeEnabled, 1m), new(ServerRateKeys.ClassChangeFreeLevel, 120m)],
            1, DateTimeOffset.UtcNow, "check", "test");
        Check.True(ClassChangePolicy.IsEnabled(null, on), "server rate npc.class_change = 1 turns it on");
        Check.Equal(0m, ServerRateCatalog.Get(ServerRateKeys.ClassChangeEnabled).DefaultValue, "rate defaults to 0");
        Check.True(ServerRateCatalog.Get(ServerRateKeys.ClassChangeEnabled).Kind == ServerRateKind.Absolute, "switch is absolute");

        Check.Equal(100, ClassChangePolicy.FreeLevel(defaults), "retail free level 100");
        Check.Equal(120, ClassChangePolicy.FreeLevel(on), "free level follows the rate");
        Check.True(!ClassChangePolicy.RequiresPermit(100, 100), "level 100 is free");
        Check.True(ClassChangePolicy.RequiresPermit(101, 100), "level 101 needs a permit");
        Check.Equal(4266, ClassChangePolicy.PermitItemId, "Class Shift Permit item");

        // REVIEW FIX: restore the process-global rates afterwards so later checks see what they saw before.
        var savedRates = ServerRates.Current;
        try
        {
            ServerRates.ResetToDefaults();
            Check.Equal(0L, ClassChangePolicy.TalentRefund(0), "rank 0 refunds nothing");
            Check.Equal(1L, ClassChangePolicy.TalentRefund(1), "rank 1 cost 1");
            Check.Equal(55L, ClassChangePolicy.TalentRefund(10), "ranks 1-10 cost 1..10");
            Check.Equal(65L, ClassChangePolicy.TalentRefund(11), "rank 11 costs 10");
            // Hard-coded stock prices (rank 12 = 17, rank 41 = 380), not recomputed by the function under test.
            Check.Equal(82L, ClassChangePolicy.TalentRefund(12), "ranks 1-12 at stock price: 55 + 10 + 17");
            Check.Equal(380, Godswar.Server.Application.Talents.TalentProgression.CalculateStockUpgradeCost(40), "rank 41 stock price 380");
            Check.Equal(56, ClassChangePolicy.TalentRefund([10, 1, 0], 7), "tree refund sums every talent");
            Check.True(
                (long)ClassChangePolicy.TalentRefund([100, 100, 100, 100, 100, 100], int.MaxValue - 5) <= 5,
                "refund saturates so the points column cannot overflow");
        }
        finally
        {
            ServerRates.Install(savedRates);
        }

        // REVIEW FIX (refund drift): the price actually paid is used when the receipts prove it.
        Check.True(ClassChangePolicy.TryPaidRefund(3, 13, [(3, 6, 13), (2, 4, 12), (1, 2, 11)], out var paid) && paid == 12,
            "paid refund sums the stored costs (rate 2 purchases refund 12, not today's 6)");
        Check.True(!ClassChangePolicy.TryPaidRefund(3, 14, [(3, 6, 13), (2, 4, 12), (1, 2, 11)], out _),
            "a newer row revision than the receipts (out-of-band edit) falls back to today's price");
        Check.True(!ClassChangePolicy.TryPaidRefund(3, 13, [(3, 6, 13), (2, 4, 12)], out _),
            "missing receipts fall back");
        Check.True(!ClassChangePolicy.TryPaidRefund(2, 12, [(3, 6, 12), (2, 4, 11)], out _),
            "ranks that do not match fall back");
        Check.True(!ClassChangePolicy.TryPaidRefund(0, 0, [], out _), "rank 0 has nothing to refund");

        // REVIEW FIX (blocker): talent commands are scoped to the class-change epoch; epoch 0 is unchanged.
        var operation = Enumerable.Range(0, 32).Select(static b => (byte)b).ToArray();
        Check.True(ReferenceEquals(operation, ClassChangeTalentEpoch.Scope(operation, 0)), "epoch 0 keeps the talent operation id byte-identical");
        var epoch1 = ClassChangeTalentEpoch.Scope(operation, 1);
        var epoch2 = ClassChangeTalentEpoch.Scope(operation, 2);
        Check.Equal(32, epoch1.Length, "scoped operation id stays a 32-byte digest");
        Check.True(!epoch1.SequenceEqual(operation) && !epoch1.SequenceEqual(epoch2), "each epoch gets its own operation id");
        Check.True(epoch1.SequenceEqual(ClassChangeTalentEpoch.Scope(operation, 1)), "the scoped id is deterministic (retries still dedupe)");

        // REVIEW FIX (rollback): no registered migration, so the previous dll still starts.
        var migrations = PostgresSchemaMigrationCatalog.All;
        Check.True(!migrations.Any(static m => m.Id.Contains("class_change", StringComparison.Ordinal)),
            "class change registers no schema migration (tables are created on first use)");
        Check.True(PostgresCharacterClassChangeStore.SchemaSql.Contains("CREATE TABLE IF NOT EXISTS public.character_class_change_receipts", StringComparison.Ordinal) &&
                   PostgresCharacterClassChangeStore.SchemaSql.Contains("CREATE TABLE IF NOT EXISTS public.character_sealed_skills", StringComparison.Ordinal),
            "first-use DDL is idempotent");
        Check.Equal(14, (int)PostgresCharacterClassChangeStore.LastCharacterSlot, "worn check covers slots 0-14 (fashion 12), not mounts 15-20");
        Check.Equal(200, ClassChangePolicy.StaleLine, "stale click answers 200 (EndMessage)");
        return Task.CompletedTask;
    }
}
