using System.Buffers.Binary;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisLiveWaveChecks
{
    public const string CheckName = "Atlantis live wave spawning, committed progression, and terminal combat";
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly (int CharacterId, int Level)[] Party = [(101, 90)];

    public static async Task RunAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            await CheckFullLiveRunAsync(mode);
            CheckTimeoutAndPublicationValidation(mode);
            CheckStalePublicationAfterCombatDeadline(mode);
        }
    }

    internal static GameplayContentCatalog Content() => GameplayContentCatalog.Empty with
    {
        // The fixture projects the reviewed map-205 rows exactly as the
        // PostgreSQL gameplay publisher does; production receives its pin.
        MonsterTemplates = MonsterTemplateSeeds.Monsters.Where(template => template.SourceMapId == 205)
            .Select(template => new GameplayMonsterTemplateDefinition(template.SourceKey,
                template.SourceKind, template.SourceMapId, template.SceneKey, template.TemplateKey,
                template.DisplayName, template.Rank, template.IsBoss, template.IsElite, template.IsPet,
                template.AttackType, template.CollisionRange)).ToArray()
    };

    private static async Task CheckFullLiveRunAsync(MonsterRuntimeMode mode)
    {
        var content = Content();
        var map = CreateMap(mode);
        Check.True(map.TryConfigureAtlantisWaves(content, Party, Start), $"{mode}: all waves configure from published templates");
        Check.True(map.TryConfigureAtlantisWaves(content, Party, Start), "identical setup is idempotent");
        Check.True(!map.TryConfigureAtlantisWaves(content, [(101, 90), (102, 90)], Start),
            "admitted roster and balance cannot change after configuration");
        Check.Equal(2, map.SnapshotMonsters().Count, "configuration exposes only the two independent capture pets");
        Check.True(map.TryStartAtlantisEncounter(Start, out _), "score clock starts explicitly");

        var seen = new HashSet<uint>();
        var runtimeIds = new HashSet<Guid>();
        var now = Start;
        var totalKills = 0;
        for (var waveIndex = 0; waveIndex < AtlantisWavePlan.WaveCount; waveIndex++)
        {
            var expected = AtlantisWavePlan.Waves[waveIndex];
            Check.True(map.TrySpawnPendingAtlantisWave(now, out var wave) && wave.WaveIndex == waveIndex,
                $"{mode}: pending wave {waveIndex} publishes once");
            Check.True(!map.TrySpawnPendingAtlantisWave(now, out _), "publication retry cannot duplicate live spawns");
            var tick = map.AdvanceMonsters(now);
            Check.True(tick.PositionsChanged, "stationary viewers receive a refresh after wave attachment");
            var live = map.SnapshotMonsters().Where(monster => monster.IsAlive && monster.ObjectId < AtlantisPetSpawnPolicy.MermaidObjectId).OrderBy(monster => monster.ObjectId).ToArray();
            Check.Equal(expected.Slots.Length, live.Length, "only this wave is alive");
            Check.Equal(expected.Slots.Length, wave.RemainingMonsterCount, "every live monster is bound to wave authority");
            Check.True(live.All(monster =>
                    BinaryPrimitives.ReadUInt16LittleEndian(monster.Definition.Packet.AsSpan(6)) == map.MapId &&
                    BinaryPrimitives.ReadUInt16LittleEndian(monster.Definition.Packet.AsSpan(4)) == 0x212),
                "every group and boss appearance passes the native current-map gate with unchanged monster flags");
            Check.True(live.All(monster => monster.RespawnAt is null && monster.SpawnGeneration == 1),
                "fresh wave uses never-respawn generation-one monsters");
            Check.True(live.All(monster => seen.Add(monster.ObjectId)), "no object ID is reused across waves");
            Check.True(live.Select(monster => monster.RuntimeInstanceId).Distinct().Count() == 1 &&
                runtimeIds.Add(live[0].RuntimeInstanceId), "each retained wave keeps a distinct reward runtime identity");
            var readinessRuntime = map.InitializeMonsters([], now);
            Check.Equal(seen.Count + 2, readinessRuntime.Count,
                "ordinary readiness preserves every Atlantis wave instead of reinitializing default content");
            if (waveIndex == 24)
            {
                Check.Equal(800, Points(map), "final Sea Guard is alive at800 points");
            }

            for (var slotIndex = 0; slotIndex < live.Length; slotIndex++)
            {
                now = now.AddMilliseconds(1);
                var monster = live[slotIndex];
                var stats = AtlantisSpawnBalancePolicy.Resolve(Party, expected.StageIndex + 1,
                    expected.Slots[slotIndex].Rank);
                Check.True(monster.MaximumHealth == stats.MaximumHealth && monster.Definition.Tier == stats.Level,
                    "live health and level are authored server balance for the pinned admitted party");
                MonsterDamageResult death;
                var killed = slotIndex % 2 == 0
                    ? map.TryApplyMonsterDamageGuarded(monster.ObjectId, monster.MaximumHealth, 101,
                        monster.SpawnGeneration, monster.HealthRevision, now, out death)
                    : map.TryApplyMonsterPeriodicDamageGuarded(monster.ObjectId, monster.MaximumHealth, 101,
                        monster.SpawnGeneration, monster.HealthRevision, now, out death);
                Check.True(killed && death.Killed, "existing direct/periodic combat commits live monster death");
                var captured = AtlantisMonsterKillScoring.Capture(map, content, map.WorldInstanceId, death);
                Check.True(captured is not null, "scoring captures published rank and committed live identity");
                var beforePoints = Points(map);
                map.TryGetAtlantisWaveSnapshot(out var beforeWave);
                void Settle(string? receipt)
                {
                    if (receipt is not null) AtlantisMonsterKillScoring.RecordCommitted(map, captured!, now);
                }
                if (totalKills == 0)
                {
                    try
                    {
                        await MonsterDeathRewardCommitBoundary.ExecuteAsync<string?>(
                            _ => Task.FromException<string?>(new IOException("reward commit unavailable")),
                            true, onSettled: Settle);
                    }
                    catch (IOException)
                    {
                    }
                    map.TryGetAtlantisWaveSnapshot(out var uncommitted);
                    Check.True(Points(map) == beforePoints &&
                        uncommitted.RemainingMonsterCount == beforeWave.RemainingMonsterCount &&
                        !map.TrySpawnPendingAtlantisWave(now, out _),
                        "dead health alone and failed durable settlement cannot score or advance a wave");
                }

                await MonsterDeathRewardCommitBoundary.ExecuteAsync(
                    _ => Task.FromResult<string?>("committed receipt"), true, onSettled: Settle);
                map.TryGetAtlantisWaveSnapshot(out var committedWave);
                var committedPoints = Points(map);
                Check.True(committedPoints > beforePoints, "successful settlement scores this exact wave member");
                await MonsterDeathRewardCommitBoundary.ExecuteAsync(
                    _ => Task.FromResult<string?>("duplicate committed receipt"), true, onSettled: Settle);
                map.TryGetAtlantisWaveSnapshot(out var duplicateWave);
                Check.True(Points(map) == committedPoints &&
                    duplicateWave.CommittedKillCount == committedWave.CommittedKillCount &&
                    duplicateWave.WaveIndex == committedWave.WaveIndex,
                    "replayed settlement cannot score, advance, or expose another wave twice");
                totalKills++;
            }
        }

        Check.True(totalKills == 245 && seen.Count == 245 && Points(map) == 850,
            $"{mode}: all25waves contain245 unique monsters and yield exactly850points");
        map.TryGetAtlantisRunSnapshot(out var finished);
        map.TryGetAtlantisWaveSnapshot(out var finishedWaves);
        Check.True(finished.State == AtlantisRunState.Completed && finishedWaves.State == AtlantisWaveState.Completed &&
            finishedWaves.CompletedWaveCount == 25 && !map.TrySpawnPendingAtlantisWave(now, out _),
            "final committed boss freezes score and closes wave publication");
        map.AdvanceMonsters(now.AddMinutes(10));
        map.AdvanceMonsters(now.AddMinutes(10).AddSeconds(1));
        Check.True(map.SnapshotMonsters().Where(monster => monster.ObjectId < AtlantisPetSpawnPolicy.MermaidObjectId).All(monster => !monster.IsAlive && !monster.IsSpawned && monster.RespawnAt is null),
            "terminal corpse aging removes all appearances without ever respawning a monster");
    }

    private static void CheckTimeoutAndPublicationValidation(MonsterRuntimeMode mode)
    {
        var map = CreateMap(mode);
        Check.True(!map.TryConfigureAtlantisWaves(GameplayContentCatalog.Empty, Party, Start) &&
            map.SnapshotMonsters().Count == 0, "missing published template rejects all wave setup atomically");
        var content = Content();
        var badTemplate = content.MonsterTemplates.First(template =>
            template.TemplateKey == AtlantisMonsterTemplatePolicy.ResolveGroupMember(0, 0).TemplateKey);
        var bad = content with { MonsterTemplates = content.MonsterTemplates.Select(template =>
            ReferenceEquals(template, badTemplate) ? template with { Rank = "boss" } : template).ToArray() };
        Check.True(!map.TryConfigureAtlantisWaves(bad, Party, Start),
            "published rank mismatch rejects setup instead of accepting authored display labels");
        Check.True(map.TryConfigureAtlantisWaves(content, Party, Start) &&
            map.TryStartAtlantisEncounter(Start, out var run) && map.TrySpawnPendingAtlantisWave(Start, out _),
            "rejected setup leaves no partial state and valid retry works");
        map.TryGetAtlantisRunSnapshot(out run);
        var active = map.SnapshotMonsters().First(monster => monster.IsAlive);
        var owned = map.InitializeMonsters([], Start);
        owned.Advance(Start, [new(101, active.X, active.Z, true, 101)]);
        map.TryAdvanceAtlantisEncounter(run.Deadline, out var timeout);
        Check.True(timeout.State == AtlantisRunState.TimedOut &&
            !map.TrySpawnPendingAtlantisWave(run.Deadline, out _) &&
            !map.TryApplyMonsterDamage(active.ObjectId, active.MaximumHealth, run.Deadline, out _),
            "exclusive deadline stops damage and future waves");
        var terminalTick = owned.Advance(run.Deadline.AddSeconds(1), [new(101, active.X, active.Z, true, 101)]);
        Check.True(terminalTick.Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "terminal wave runtime suppresses attacks even when targets remain present");
    }

    private static MapInstance CreateMap(MonsterRuntimeMode mode) => new(
        WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(), new(205), InstanceKind.Dungeon, 5, Start), mode);

    private static void CheckStalePublicationAfterCombatDeadline(MonsterRuntimeMode mode)
    {
        var map = CreateMap(mode);
        var content = Content();
        Check.True(map.TryConfigureAtlantisWaves(content, Party, Start) &&
            map.TryStartAtlantisEncounter(Start, out _) && map.TrySpawnPendingAtlantisWave(Start, out _),
            "stale publication fixture starts first wave");
        var monsters = map.SnapshotMonsters().Where(monster => monster.ObjectId < AtlantisPetSpawnPolicy.MermaidObjectId).ToArray();
        var now = Start.AddSeconds(1);
        foreach (var monster in monsters)
        {
            Check.True(map.TryApplyMonsterDamage(monster.ObjectId, monster.MaximumHealth, now, out var death),
                "stale publication fixture commits death");
            AtlantisMonsterKillScoring.RecordCommitted(map, content, map.WorldInstanceId, death, now);
        }
        map.TryGetAtlantisRunSnapshot(out var run);
        map.TryGetAtlantisWaveSnapshot(out var pending);
        Check.True(pending.State == AtlantisWaveState.PendingPublication && Points(map) == 30,
            "second wave is pending before deadline observation");
        _ = map.TryApplyMonsterStun(monsters[0].ObjectId, 101, TimeSpan.FromSeconds(1), run.Deadline, out _);
        Check.True(!map.TrySpawnPendingAtlantisWave(run.Deadline.AddTicks(-1), out var rejected) &&
            rejected.State == AtlantisWaveState.TimedOut && map.SnapshotMonsters().Count == 14,
            $"{mode}: later combat deadline prevents an older tick from binding or spawning the next wave");
        map.TryGetAtlantisRunSnapshot(out var timedOut);
        Check.True(timedOut.State == AtlantisRunState.TimedOut && timedOut.TeamPoints == 30,
            "composite deadline is reconciled to score and wave clocks without phantom monsters");
    }

    private static int Points(MapInstance map)
    {
        Check.True(map.TryGetAtlantisRunSnapshot(out var snapshot), "run snapshot exists");
        return snapshot.TeamPoints;
    }
}
