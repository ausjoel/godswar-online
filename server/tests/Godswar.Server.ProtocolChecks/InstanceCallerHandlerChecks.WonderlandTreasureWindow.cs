using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckWonderlandCompletedTreasureWindowAsync(WonderlandHandlerFixture fixture,
        WorldInstanceRuntime runtime, NpcSpawnDefinition final, RecordingWonderlandChestStore store)
    {
        var leader = fixture.Party.Leader;
        var registry = leader.Registry;
        runtime.Map.TryGetWonderlandSnapshot(out var run);
        var completedAt = run.TerminalAt!.Value;
        var expiresAt = completedAt + WonderlandCompletionPolicy.TreasureWindow;
        var lateFinish = run with { TerminalAt = run.Deadline.AddTicks(-1) };
        Check.True(WonderlandCompletionPolicy.IsTreasureWindowOpen(lateFinish, run.Deadline.AddMinutes(4)) &&
            WonderlandTreasureChestPolicy.IsUnlocked(lateFinish, 8, run.Deadline.AddMinutes(4)),
            "finishing just before the forty-minute deadline still grants the full treasure window afterward");
        Check.True(WonderlandCompletionPolicy.TreasureWindow == TimeSpan.FromMinutes(5) &&
            !WonderlandCompletionPolicy.IsTreasureWindowOpen(run, completedAt.AddTicks(-1)) &&
            WonderlandCompletionPolicy.IsTreasureWindowOpen(run, expiresAt.AddTicks(-1)) &&
            WonderlandCompletionPolicy.AllowsIslandTravel(run, expiresAt.AddTicks(-1)) &&
            !WonderlandCompletionPolicy.AllowsIslandTravel(run, expiresAt) &&
            !WonderlandTreasureChestPolicy.IsUnlocked(run, 8, expiresAt),
            "chests and island travel share the exact five-minute completion boundary");
        await registry.AdvanceMonsterWorldOnceAsync(completedAt.AddMinutes(4), CancellationToken.None);
        Check.True(registry.TryGetWorldInstance(runtime.InstanceId, out _) && runtime.Map.Population == 2,
            "the completed runtime and both admitted members remain available four minutes after the last kill");
        leader.Character.CurrentHp = 0;
        Check.True(registry.TryReviveWonderlandPlayer(leader.Session, out var revivedInstance, out var entrance, out _) &&
            revivedInstance == runtime.InstanceId && entrance == WonderlandTerrainPolicy.GetIsland(1).Entrance,
            "a dead finisher may revive at the first island entrance during the treasure window");
        for (var island = 1; island <= 7; island++)
        {
            await ClickWonderlandPortalAsync(leader, island);
            var next = WonderlandTerrainPolicy.GetIsland(island + 1).Entrance;
            Check.True(GetSourceInstanceId(leader) == runtime.InstanceId &&
                leader.Character.PositionX == next.X && leader.Character.PositionZ == next.Z,
                "already unlocked native portals remain usable after completion for split-party chest collection");
            await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        }
        MoveToWonderlandChest(leader, final);
        var before = store.Requests.Count;
        await registry.ClaimWonderlandChestAsync(leader.Session, final, expiresAt.AddTicks(-1), CancellationToken.None);
        Check.True(store.Requests.Count == before + 1, "the final chest stays claimable until the last tick of the shared window");
        await RejectWonderlandChestAsync(leader, final, store, expiresAt, "expired completion window");
        Check.True(registry.TryTransferMap(leader.Session, 207, 0, 136, -150), "a finisher may leave before auto-egress");
        await RejectWonderlandChestAsync(leader, final, store, expiresAt.AddTicks(-1), "departed exact-instance member");
        Check.True(!registry.TryTransferWorldInstance(leader.Session, GetSourceInstanceId(leader), runtime.InstanceId,
            final.X, final.Z), "completed in-place travel cannot be used to re-admit a departed member");
        registry.Remove(fixture.Party.Followers[0].Session);
        await registry.AdvanceMonsterWorldOnceAsync(expiresAt.AddTicks(-1), CancellationToken.None);
        Check.True(registry.TryGetWorldInstance(runtime.InstanceId, out _) && runtime.Map.Population == 0,
            "an empty completed runtime retains the same treasure deadline instead of retiring early");
        await registry.AdvanceMonsterWorldOnceAsync(expiresAt, CancellationToken.None);
        Check.True(!registry.TryGetWorldInstance(runtime.InstanceId, out _) &&
            !registry.TryResolveWonderlandTravel(leader.Session, 1, out _, out _) &&
            !registry.IsWonderlandTravelCurrent(leader.Session, runtime.InstanceId, 1),
            "the exact deadline retires the empty runtime and rejects stale chest and portal ownership");
    }
}
