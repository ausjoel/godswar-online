// ROUND3: issue #21 follows the reported clear-five/empty-six route through the real handlers.
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandPublicationCheckName =
        "Wonderland handler publishes islands six to eight after seven native portal hops";

    public static async Task RunWonderlandPublicationAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var actor = fixture.Party.Leader;
        var allIds = new HashSet<uint>();
        for (var island = 1; island <= 8; island++)
        {
            Check.True(runtime.Map.TryGetWonderlandSnapshot(out var run) &&
                run.CurrentIsland == island && !run.PublicationPending &&
                run.CompletedIslands == island - 1, "the world tick publishes the next unlocked stage");
            var active = runtime.Map.SnapshotMonsters().Where(m =>
                run.ActiveSpawns.Any(p => p.ObjectId == m.ObjectId)).ToArray();
            Check.Equal(run.ActiveSpawns.Length, active.Length, "every authored stage actor is attached");
            Check.True(active.All(m => m.IsAlive && m.IsSpawned && m.SpawnGeneration == 1 && allIds.Add(m.ObjectId)),
                "the next island contains visible living actors with distinct never-respawn identities");
            Console.WriteLine($"WONDERLAND handler island={island} actors={active.Length} required={run.RequiredMonstersRemaining} pending={run.PublicationPending}");
            await ClearWonderlandHandlerIslandAsync(fixture, runtime);
            if (island == 8) break;
            var before = actor.ReadPackets().Count;
            await ClickWonderlandPortalAsync(actor, island);
            var next = WonderlandTerrainPolicy.GetIsland(island + 1).Entrance;
            Check.True(GetSourceInstanceId(actor) == runtime.InstanceId && actor.Character.CurrentMap == 207 &&
                actor.Character.PositionX == next.X && actor.Character.PositionZ == next.Z &&
                actor.ReadPackets().Skip(before).Count(p => ReadOpcode(p) == Opcodes.SceneChange) == 1,
                "the native Teleport action preserves the exact dungeon and reaches the next authored entrance");
            await CompleteAtlantisSceneReadinessAsync(actor.Handler);
        }
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var done) &&
            done.State == WonderlandRunState.Completed && done.CompletedIslands == 8 && !done.PublicationPending,
            "committed final required kills complete the eight-island run");
        Check.Equal(115, allIds.Count, "all 115 existing authored actors publish once across the entire run");
    }
}
