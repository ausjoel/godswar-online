using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class CapitalShopInventoryPlannerChecks
{
    public const string CheckName =
        "Capital-shop partial-stack inventory planning";

    public static Task RunAsync()
    {
        var offered = CompactItemEntry.Parse(
            "[4001,,,,,,1,1,0,1,0,0]");
        Check.True(
            PostgresCapitalShopPurchaseStore.IsCapitalShopStackCompatible(
                offered with { Stack = 98 },
                offered),
            "the same authoritative item state can merge");
        Check.True(
            !PostgresCapitalShopPurchaseStore.IsCapitalShopStackCompatible(
                offered with { Stack = 98, Bound = 1 },
                offered),
            "different binding state cannot merge");

        var fullBag = new PostgresCapitalShopPurchaseStore.CapitalShopBag(
            [new(7001, 42, 98, "{\"stack\":98}")],
            Enumerable.Repeat(true, 96).ToArray());
        var fullBagPlan = PostgresCapitalShopPurchaseStore.PlanCapitalShopMutation(
            fullBag,
            quantity: 1,
            stackCap: 99) ?? throw new InvalidOperationException(
                "A full bag with partial-stack capacity was rejected.");
        Check.Equal(
            1,
            fullBagPlan.Updates.Count,
            "full-bag purchase uses its compatible partial stack");
        Check.Equal(
            (short)99,
            fullBagPlan.Updates[0].StackAfter,
            "full-bag partial stack reaches its cap");
        Check.Equal(
            0,
            fullBagPlan.Inserts.Count,
            "full-bag partial-stack purchase requires no empty slot");

        var occupied = new bool[96];
        occupied[0] = true;
        occupied[2] = true;
        var splitPlan = PostgresCapitalShopPurchaseStore.PlanCapitalShopMutation(
            new PostgresCapitalShopPurchaseStore.CapitalShopBag(
                [
                    new(7002, 2, 97, "{\"stack\":97}"),
                    new(7001, 0, 98, "{\"stack\":98}")
                ],
                occupied),
            quantity: 5,
            stackCap: 99) ?? throw new InvalidOperationException(
                "A merge-and-insert purchase was rejected.");
        Check.Equal(
            (short)0,
            splitPlan.Updates[0].Stack.Slot,
            "partial-stack updates are ordered by slot");
        Check.Equal(
            (short)2,
            splitPlan.Updates[1].Stack.Slot,
            "second compatible partial stack is used before an empty slot");
        Check.Equal(
            (short)1,
            splitPlan.Inserts[0].Slot,
            "remaining quantity uses the first deterministic empty slot");
        Check.Equal(
            (short)2,
            splitPlan.Inserts[0].Stack,
            "remaining quantity is preserved across update and insert splits");

        var noCapacity = PostgresCapitalShopPurchaseStore.PlanCapitalShopMutation(
            new PostgresCapitalShopPurchaseStore.CapitalShopBag([], fullBag.Occupied),
            quantity: 1,
            stackCap: 99);
        Check.True(
            noCapacity is null,
            "a genuinely full bag without stack capacity remains rejected");
        return Task.CompletedTask;
    }
}
