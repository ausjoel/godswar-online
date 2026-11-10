using System.Buffers.Binary;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandMapChecks
{
    public const string CheckName = "Wonderland live eight-island publication, committed clears, and isolated retirement";

    public static Task RunAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            CheckFullRun(mode);
            CheckTimeout(mode);
            CheckRejectedContent(mode);
            CheckActualAdmissions(mode);
            CheckCompletedCombatWindow(mode);
        }
        return Task.CompletedTask;
    }

    // ROUND3: replay the identical eight-stage authority checks with copied database content.
    internal static void CheckFullRun(MonsterRuntimeMode mode, GameplayContentCatalog? content = null)
    {
        // ROUND3: only this full-run fixture accepts optional copied content.
        var map = Create(mode, content: content);
        Check.Equal(0, map.SnapshotMonsters().Count, "configuration publishes nothing before admission seal");
        var entrance = WonderlandTerrainPolicy.GetIsland(1).Entrance;
        map.InitializeMonsters([], Start).Advance(Start, [new(101, entrance.X, entrance.Z, true, WorldInstanceId: map.WorldInstanceId)]);
        var now = Start;
        var allIds = new HashSet<uint>();
        var clearTimes = new List<DateTimeOffset>();
        var lastResult = (WonderlandKillResult?)null;
        for (var island = 1; island <= 8; island++)
        {
            var previousSurvivors = map.SnapshotMonsters().Where(m => m.IsAlive).ToArray();
            Check.True(map.TrySpawnPendingWonderlandStage(now, out var snapshot) && snapshot.CurrentIsland == island,
                $"{mode}: only unlocked island {island} publishes");
            Check.True(!map.TrySpawnPendingWonderlandStage(now, out _), "repeat publication is harmless");
            var visible = map.SnapshotMonsters().Where(m => m.IsAlive && m.IsSpawned).ToArray();
            var live = visible.Where(m => snapshot.ActiveSpawns.Any(p => p.ObjectId == m.ObjectId)).ToArray();
            Check.Equal(snapshot.ActiveSpawns.Length + previousSurvivors.Length, visible.Length,
                "new island publication keeps every earlier survivor visible");
            Check.True(previousSurvivors.All(old => visible.Any(current => current.ObjectId == old.ObjectId &&
                current.CurrentHealth == old.CurrentHealth && current.RuntimeInstanceId == old.RuntimeInstanceId &&
                current.SpawnGeneration == old.SpawnGeneration)), "publication preserves earlier health and identity");
            Check.True(live.All(m => allIds.Add(m.ObjectId) && m.RespawnAt is null && m.SpawnGeneration == 1),
                "each object keeps a unique never-respawn identity");
            foreach (var monster in live)
            {
                var policy = snapshot.ActiveSpawns.Single(p => p.ObjectId == monster.ObjectId);
                Check.True(monster.MaximumHealth == policy.Stats.MaximumHealth && monster.Definition.Tier == policy.Stats.Level &&
                    BinaryPrimitives.ReadUInt16LittleEndian(monster.Definition.Packet.AsSpan(6)) == 207 &&
                    monster.Definition.Packet[4] == 0x12 &&
                    monster.Definition.Packet[5] == (policy.IsAllied ? snapshot.PartyCamp : 2),
                    "live appearance retains native map flags and captured level/HP");
            }
            var required = snapshot.ActiveSpawns.Where(p => p.RequiredForProgression).ToArray();
            Check.Equal(island == 2 ? 2 : island == 8 ? 5 : 1, required.Length, "only approved required targets gate progression");
            var first = live.Single(m => m.ObjectId == required[0].ObjectId);
            Check.True(map.RecordCommittedWonderlandKill(map.WorldInstanceId, first.ObjectId, 1, now).Outcome ==
                WonderlandKillOutcome.UnknownMonster, "a live monster cannot be credited as dead");
            Check.True(!map.TryApplyMonsterDamageGuarded(first.ObjectId, 1, 101, 2, first.HealthRevision, now, out _),
                "a different generation cannot damage this run");
            if (island == 5)
            {
                foreach (var ally in snapshot.ActiveSpawns.Where(p => p.IsAllied))
                {
                    var target = live.Single(m => m.ObjectId == ally.ObjectId);
                    Check.True(!map.TryApplyMonsterDamageGuarded(target.ObjectId, target.MaximumHealth, 101,
                        1, target.HealthRevision, now, out _), "allied faction monsters cannot be farmed by a player");
                }
            }
            // Finish close to the run deadline so the completion treasure
            // window must retain earlier corpses beyond that original deadline.
            if (island == 8) now = snapshot.Deadline.AddSeconds(-1);
            foreach (var target in required)
            {
                now = now.AddMilliseconds(1);
                lastResult = Kill(map, target.ObjectId, now);
                Check.True(map.RecordCommittedWonderlandKill(WorldInstanceId.New(), target.ObjectId, 1, now).Outcome ==
                    WonderlandKillOutcome.WrongInstance, "foreign instance cannot claim a committed death");
            }
            Check.True(lastResult!.ClearedIsland is { } clear && clear.Island == island && clear.ClearedAt == now,
                "clear callback exposes exact island and authoritative final-kill time");
            clearTimes.Add(now);
            var survivors = live.Where(m => !snapshot.ActiveSpawns.Single(p => p.ObjectId == m.ObjectId).RequiredForProgression).ToArray();
            Check.True(survivors.All(old => map.TryGetMonsterSnapshot(old.ObjectId, out var current) &&
                current.IsAlive && current.IsSpawned && current.CurrentHealth == old.CurrentHealth &&
                current.SpawnGeneration == old.SpawnGeneration), "boss clear neither kills nor despawns living support or allies");
            var optional = survivors.FirstOrDefault(m => !snapshot.ActiveSpawns.Single(p => p.ObjectId == m.ObjectId).IsAllied);
            if (optional is not null)
                Check.True(map.TryApplyMonsterPeriodicDamageGuarded(optional.ObjectId, 1, 101,
                    1, optional.HealthRevision, now, out var damage) && damage.AfterHealth == optional.CurrentHealth - 1,
                    "survivors accept legitimate periodic damage after island unlock and during the completed treasure window");
        }
        Check.True(allIds.Count == 115 && lastResult!.Snapshot.State == WonderlandRunState.Completed &&
            lastResult.Snapshot.CompletedIslands == 8 && lastResult.Snapshot.TerminalAt == now,
            "all eight islands complete once after 115 disjoint published objects");
        Check.True(lastResult!.Snapshot.Clears.Select(c => c.ClearedAt).SequenceEqual(clearTimes),
            "immutable clear history preserves every reward milestone timestamp");
        Check.True(!map.TrySpawnPendingWonderlandStage(now, out _), "completion cannot publish another stage");
        var lastCorpse = map.SnapshotWonderlandBossCorpses().MaxBy(corpse => corpse.DiedAt)!;
        var lastCorpseExpires = lastCorpse.DiedAt + WonderlandBossLootPolicy.CorpseLifetime;
        map.AdvanceMonsters(lastCorpseExpires.AddTicks(-1));
        Check.True(map.TryGetMonsterSnapshot(lastCorpse.Definition.MonsterObjectId, out var beforeExpiry) &&
            !beforeExpiry.IsAlive && beforeExpiry.IsSpawned && beforeExpiry.DespawnAt == lastCorpseExpires,
            "the last boss retains its complete twenty-second pickup window even across run completion");
        var survivorsAtCorpseExpiry = map.SnapshotMonsters().Where(monster => monster.IsAlive).ToArray();
        map.AdvanceMonsters(lastCorpseExpires);
        Check.True(map.SnapshotMonsters().All(m => m.RespawnAt is null &&
            (m.IsAlive ? m.IsSpawned : !m.IsSpawned)) &&
            survivorsAtCorpseExpiry.All(prior => map.TryGetMonsterSnapshot(prior.ObjectId, out var current) &&
                current.IsAlive && current.IsSpawned && current.CurrentHealth == prior.CurrentHealth &&
                current.SpawnGeneration == prior.SpawnGeneration && current.RuntimeInstanceId == prior.RuntimeInstanceId),
            "twenty-second corpse expiry retires dead actors while preserving living combatants and their exact health/identity");
        var treasureEnds = now + WonderlandCompletionPolicy.TreasureWindow;
        map.AdvanceMonsters(treasureEnds.AddTicks(-1));
        Check.True(map.SnapshotMonsters().Where(m => map.TryGetWonderlandBossCorpse(m.ObjectId, out _))
            .All(m => !m.IsAlive && !m.IsSpawned),
            "the five-minute island-treasure window cannot extend or recreate expired boss corpses");
        map.AdvanceMonsters(treasureEnds);
        Check.True(map.SnapshotMonsters().All(m => m.RespawnAt is null &&
            (m.IsAlive ? m.IsSpawned && !m.IsMoving : !m.IsSpawned)),
            "terminal cleanup expires corpses and stops survivors without inventing deaths or respawns");
    }

    private static void CheckTimeout(MonsterRuntimeMode mode)
    {
        var map = Create(mode);
        map.TryGetWonderlandSnapshot(out var start);
        Check.True(map.TrySpawnPendingWonderlandStage(Start, out _), "timeout fixture publishes first island");
        var boss = map.SnapshotMonsters().First();
        var runtime = map.InitializeMonsters([], Start);
        runtime.Advance(Start, [new(101, boss.X + 20, boss.Z, true, WorldInstanceId: map.WorldInstanceId)]);
        Check.True(runtime.Snapshot().Single(m => m.ObjectId == boss.ObjectId).IsMoving,
            "timeout fixture begins an actual chase before combat freezes");
        Check.True(!runtime.TryApplyDamage(boss.ObjectId, boss.MaximumHealth, 101, 1, start.Deadline, out _),
            "combat deadline wins even before the registry timer tick");
        var expired = map.AdvanceWonderland(start.Deadline);
        Check.True(expired.State == WonderlandRunState.TimedOut && expired.TerminalAt == start.Deadline &&
            expired.CompletedIslands == 0, "deadline is fixed at 40 minutes and cannot award a late kill");
        var stopped = runtime.Advance(start.Deadline);
        Check.True(stopped.Updates.Any(u => u.Monster.ObjectId == boss.ObjectId && u.Kind == MonsterRuntimeUpdateKind.Arrived) &&
            runtime.Snapshot().All(m => !m.IsMoving && m.VelocityX == 0 && m.VelocityZ == 0),
            "terminal freeze publishes a native movement stop instead of leaving clients chasing");
        Check.True(!map.TrySpawnPendingWonderlandStage(Start, out _) &&
            map.CancelWonderland(Start).State == WonderlandRunState.TimedOut, "stale tick/cancel cannot reopen a terminal run");
    }

    private static void CheckRejectedContent(MonsterRuntimeMode mode)
    {
        var descriptor = WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(), new(207),
            InstanceKind.Dungeon, 5, Start);
        var map = new MapInstance(descriptor, mode);
        Check.True(!map.TryConfigureWonderland(GameplayContentCatalog.Empty, [new(101, 120, 0)], Start) &&
            map.SnapshotMonsters().Count == 0, "missing published templates reject setup atomically");
        var content = Content();
        var bad = content with { MonsterTemplates = content.MonsterTemplates.Select(t =>
            t.TemplateKey == "B_boss_xerxer_001" ? t with { Rank = "normal" } : t).ToArray() };
        Check.True(!map.TryConfigureWonderland(bad, [new(101, 120, 0)], Start), "rank drift fails closed");
        var unexpectedPet = content with { MonsterTemplates = content.MonsterTemplates.Select(t =>
            t.TemplateKey == "B_normale_robber_003" ? t with { IsPet = true } : t).ToArray() };
        Check.True(!map.TryConfigureWonderland(unexpectedPet, [new(101, 120, 0)], Start),
            "the captured Assaulter model exception does not admit arbitrary pet-marked hostiles");
        Check.True(map.TryConfigureWonderland(content, [new(101, 120, 0)], Start), "valid retry succeeds after rejected setup");
    }

    private static void CheckActualAdmissions(MonsterRuntimeMode mode)
    {
        var descriptor = WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(), new(207),
            InstanceKind.Dungeon, 5, Start);
        var map = new MapInstance(descriptor, mode);
        Check.True(map.TryConfigureWonderland(Content(), [new(101, 120, 0), new(102, 140, 1)], Start),
            "partial admission fixture plans two different-faction participants");
        map.TryGetWonderlandSnapshot(out var original);
        Check.True(!map.TryFinalizeWonderlandAdmissions([]) && !map.TryFinalizeWonderlandAdmissions([999]),
            "empty and foreign admission sets cannot rewrite the pending roster");
        Check.True(map.TryFinalizeWonderlandAdmissions([102]) && map.TryFinalizeWonderlandAdmissions([102]) &&
            !map.TryFinalizeWonderlandAdmissions([101, 102]), "actual entrant seal is immutable and idempotent");
        map.TryGetWonderlandSnapshot(out var sealedRun);
        Check.True(sealedRun.Participants.Length == 1 && sealedRun.Participants[0].CharacterId == 102 &&
            sealedRun.PartyCamp == 0 && sealedRun.StartedAt == original.StartedAt && sealedRun.Deadline == original.Deadline,
            "failed leader admission does not change original faction or reset the run clock");
        Check.True(map.TrySpawnPendingWonderlandStage(Start, out _), "actual admitted roster publishes after seal");
        var boss = map.SnapshotMonsters().First();
        Check.Equal(8_000_000u, boss.MaximumHealth, "Alpha retains its captured eight-million HP for one actual entrant");
        Check.True(!map.TryApplyMonsterDamageGuarded(boss.ObjectId, 1, 101, 1, boss.HealthRevision, Start, out _) &&
            map.TryApplyMonsterDamageGuarded(boss.ObjectId, 1, 102, 1, boss.HealthRevision, Start, out _),
            "failed admission is removed from the live runtime's damage participant whitelist");
    }
}
