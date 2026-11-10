using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandMapChecks
{
    internal static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    internal static GameplayContentCatalog Content() => GameplayContentCatalog.Empty with
    {
        MonsterTemplates = MonsterTemplateSeeds.Monsters.Where(t => t.SourceMapId == 207)
            .Select(t => new GameplayMonsterTemplateDefinition(t.SourceKey, t.SourceKind, t.SourceMapId,
                t.SceneKey, t.TemplateKey, t.DisplayName, t.Rank, t.IsBoss, t.IsElite, t.IsPet,
                t.AttackType, t.CollisionRange)).ToArray()
    };

    internal static MapInstance Create(MonsterRuntimeMode mode = MonsterRuntimeMode.Legacy,
        // ROUND3: copied PostgreSQL content can exercise the same real instance fixture.
        byte camp = 0, int partySize = 1, GameplayContentCatalog? content = null)
    {
        var descriptor = WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(),
            new(207), InstanceKind.Dungeon, 5, Start);
        var map = new MapInstance(descriptor, mode);
        // ROUND3: seeded defaults remain unchanged for ordinary protocol checks.
        Check.True(map.TryConfigureWonderland(content ?? Content(), Enumerable.Range(101, partySize)
            .Select(id => new WonderlandParticipant(id, 120, camp)).ToArray(), Start), "Wonderland setup succeeds");
        return map;
    }

    internal static DateTimeOffset EnterIsland(MapInstance map, int island, DateTimeOffset? at = null)
    {
        var now = at ?? Start;
        while (true)
        {
            map.TryGetWonderlandSnapshot(out var snapshot);
            Check.True(snapshot.State == WonderlandRunState.Active && snapshot.CurrentIsland <= island,
                "fixture advances forward through required island kills");
            if (snapshot.PublicationPending)
                Check.True(map.TrySpawnPendingWonderlandStage(now, out snapshot), "fixture publishes unlocked island");
            if (snapshot.CurrentIsland == island) return now;
            foreach (var policy in snapshot.ActiveSpawns.Where(p => p.RequiredForProgression))
            {
                now = now.AddMilliseconds(1);
                Kill(map, policy.ObjectId, now);
            }
        }
    }

    internal static WonderlandKillResult Kill(MapInstance map, uint objectId, DateTimeOffset now)
    {
        var monster = map.SnapshotMonsters().Single(m => m.ObjectId == objectId);
        Check.True(map.TryApplyMonsterDamageGuarded(objectId, monster.CurrentHealth, 101,
            monster.SpawnGeneration, monster.HealthRevision, now, out var death) && death.Killed,
            "fixture commits authoritative monster death");
        return map.RecordCommittedWonderlandKill(map.WorldInstanceId, objectId, monster.SpawnGeneration, now);
    }
}
