using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FactionCrierHandlerChecks
{
    private static async Task CheckNavigationDoesNotMutateAsync()
    {
        var before = CreateSnapshot(
            level: 80,
            GameDefaults.EmptyKitBag,
            after: false);
        var after = CreateSnapshot(
            level: 80,
            GameDefaults.EmptyKitBag,
            after: true);
        var executor = new FactionCrierExecutor();
        await using var fixture = await CreateFixtureAsync(
            secure: true,
            before,
            after,
            executor);

        await InvokeAsync(fixture.Handler, CreateActionPacket(2));

        AssertFunctionResponse(
            fixture.ReadLegacyPackets().Single(),
            [10, 20, 1001],
            "Faction Crier exchange navigation");
        Check.Equal(0, executor.ReplayCount,
            "Faction Crier navigation never checks the inbox");
        Check.Equal(0, executor.ExecuteCount,
            "Faction Crier navigation never executes a command");
        Check.Equal(0, fixture.Snapshots.ReadCount,
            "Faction Crier navigation never reloads progression");
    }
}
