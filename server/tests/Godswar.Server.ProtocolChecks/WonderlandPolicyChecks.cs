using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandPolicyChecks
{
    public const string CheckName = "Wonderland boss balance, faction roster, terrain contracts, and death ledger";

    public static Task RunAsync()
    {
        CheckClockwiseRoute();
        CheckCapturedFirstIsland();
        CheckCapturedSecondAndFinalRoute();
        CheckCompleteCapturePlacements();
        CheckCapturedFullRunHealth();
        var five = Enumerable.Range(1, 8).SelectMany(i => WonderlandMonsterPlan.Create(i, 5, 0)).ToArray();
        Check.True(five.Length == 115 && five.Select(p => p.ObjectId).Distinct().Count() == 115 &&
            five.Count(p => p.IsBoss) == 13 && five.Count(p => p.Role == WonderlandMonsterRole.Atlas) == 8,
            "plan contains all 13 boss identities and eight final Atlas followers");
        Check.True(five.Where(p => p.IsBoss && !p.IsAllied).Sum(p => (long)p.Stats.MaximumHealth) == 215_000_000,
            "hostile boss HP includes the requested fivefold enemy-marshal increase over the capture");
        foreach (var size in Enumerable.Range(1, 5))
        {
            var partyPlan = Enumerable.Range(1, 8).SelectMany(i => WonderlandMonsterPlan.Create(i, size, 0)).ToArray();
            foreach (var monster in partyPlan)
            {
                var full = five.Single(p => p.ObjectId == monster.ObjectId);
                Check.True(monster.Stats == full.Stats,
                    "all mob and boss HP and combat stats remain identical for one through five participants");
            }
        }
        foreach (byte camp in new byte[] { 0, 1 })
        {
            var marshals = WonderlandMonsterPlan.Create(5, 1, camp).Where(p => p.IsBoss).ToArray();
            Check.True(marshals.Single(p => p.IsAllied).FactionCamp == camp &&
                !marshals.Single(p => p.IsAllied).RequiredForProgression &&
                marshals.Single(p => !p.IsAllied).RequiredForProgression, "fixed leader camp selects opposing enemy marshal");
        }
        foreach (var island in Enumerable.Range(1, 8))
        {
            var geometry = WonderlandTerrainPolicy.GetIsland(island);
            Check.True(geometry.SpawnPositions.All(p => geometry.Bounds.Contains(p.X, p.Z)) &&
                geometry.Bounds.Contains(geometry.Entrance.X, geometry.Entrance.Z) &&
                geometry.Bounds.Contains(geometry.Exit.X, geometry.Exit.Z), "all authored coordinates remain on their audited island bounds");
            Check.True(WonderlandTerrainPolicy.IsCombatArea(island, geometry.Entrance.X, geometry.Entrance.Z) == (island == 7) &&
                !WonderlandTerrainPolicy.IsCombatArea(island, geometry.Exit.X, geometry.Exit.Z),
                "native seventh-island arrival retains nearby combat; other arrivals and all exit services remain protected");
        }
        var fire = WonderlandTerrainPolicy.GetIsland(6);
        Check.True(fire.GroundFirePositions.Length == 9 && fire.GroundFirePositions.All(p =>
            fire.Bounds.Contains(p.X, p.Z)) && fire.GroundFirePositions.Count(p =>
            WonderlandTerrainPolicy.DistanceSquared(fire.Entrance, p.X, p.Z) > 15 * 15 &&
            WonderlandTerrainPolicy.DistanceSquared(fire.Exit, p.X, p.Z) > 15 * 15) == 9,
            "all nine audited fire candidates keep their full five-unit circles clear of the requested travel safe zones");
        CheckLedger();
        return Task.CompletedTask;
    }

    private static void CheckClockwiseRoute()
    {
        Check.True(WonderlandTerrainPolicy.TryFindIsland(159, -163, out var first) && first == 1 &&
            WonderlandTerrainPolicy.TryFindIsland(-15, -185, out var second) && second == 2,
            "the user's southeast and south anchors identify islands one and two");
        var expectedEntries = new (float X, float Z)[]
        { (169,-216), (20,-194), (-63,-115), (-144,-21), (-186,195), (116,211), (158,29), (56,-112) };
        var previousAngle = Math.Atan2(-158, 172);
        for (var island = 1; island <= 8; island++)
        {
            var geometry = WonderlandTerrainPolicy.GetIsland(island);
            Check.True((geometry.Entrance.X, geometry.Entrance.Z) == expectedEntries[island - 1] &&
                WonderlandTerrainPolicy.TryFindIsland(geometry.Center.X, geometry.Center.Z, out var actual) && actual == island,
                "every numbered entrance and center belongs to the corrected physical island");
            Check.True(geometry.SpawnPositions.All(point => WonderlandTerrainPolicy.IsCombatArea(island, point.X, point.Z)) &&
                geometry.Bounds.Contains(geometry.TreasureChest.X, geometry.TreasureChest.Z) &&
                (island == 8 || WonderlandTraversalPolicy.RequiresExplicitClick(island)),
                "monster slots avoid arrival safety and all seven onward portals require an explicit NPC action");
            if (island is > 1 and < 8)
            {
                var angle = Math.Atan2(geometry.Center.Z, geometry.Center.X);
                while (angle >= previousAngle) angle -= Math.Tau;
                Check.True(previousAngle - angle < Math.PI, "the seven outer islands advance clockwise without reversing");
                previousAngle = angle;
            }
        }
        Check.True(WonderlandTerrainPolicy.GetIsland(8).Bounds.Contains(0, 0) &&
            !Enumerable.Range(1, 7).Any(island => WonderlandTerrainPolicy.GetIsland(island).Bounds.Contains(0, 0)),
            "the central component is reserved for the eighth island");
    }

    private static void CheckLedger()
    {
        var start = WonderlandMapChecks.Start;
        var descriptor = WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(), new(207),
            InstanceKind.Dungeon, 5, start);
        var run = new WonderlandRunRuntime(descriptor, [new(101, 120, 0)], start);
        var pending = run.Snapshot();
        var ids = pending.ActiveSpawns.Select(p => new WonderlandMonsterIdentity(p.ObjectId, 1)).ToArray();
        Check.True(!run.TryBindStage(2, ids, start, out _) && !run.TryBindStage(1, ids[..^1], start, out _),
            "no future stage or incomplete roster can be bound");
        Check.True(run.TryBindStage(1, ids, start, out _) && run.TryBindStage(1, ids, start, out _),
            "binding exact staged identities is idempotent");
        var boss = pending.ActiveSpawns.Single(p => p.RequiredForProgression);
        Check.True(run.RecordCommittedKill(WorldInstanceId.New(), boss.ObjectId, 1, pending.Deadline).Outcome ==
            WonderlandKillOutcome.WrongInstance && run.Snapshot().LastObservedAt == start,
            "foreign death cannot advance owner clock");
        Check.True(run.RecordCommittedKill(descriptor.InstanceId, boss.ObjectId, 2, start).Outcome ==
            WonderlandKillOutcome.UnknownMonster, "wrong generation cannot clear an island");
        var results = new WonderlandKillResult[16];
        Parallel.For(0, results.Length, i => results[i] = run.RecordCommittedKill(descriptor.InstanceId,
            boss.ObjectId, 1, start.AddSeconds(1)));
        Check.True(results.Count(r => r.ClearedIsland is not null) == 1 && run.Snapshot().CompletedIslands == 1,
            "concurrent duplicate final kill emits one durable milestone callback");
        Check.True(run.RecordCommittedKill(descriptor.InstanceId, boss.ObjectId, 1, start).Outcome ==
            WonderlandKillOutcome.StaleTimestamp, "backward timestamp cannot mutate progression");
        Check.True(run.Cancel(start.AddSeconds(2)).State == WonderlandRunState.Cancelled &&
            !run.TryBindStage(2, run.Snapshot().ActiveSpawns.Select(p => new WonderlandMonsterIdentity(p.ObjectId, 1)).ToArray(),
                start.AddSeconds(3), out _), "cancel pauses all future stage publication");
    }
}
