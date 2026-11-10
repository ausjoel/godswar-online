using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private readonly object _wonderlandGate = new();
    private WonderlandRunRuntime? _wonderlandRun;
    private WonderlandMonsterRuntime? _wonderlandMonsters;
    private CapturedMonsterSpawn[][]? _wonderlandDefinitions;
    private MonsterCombatProfileCatalog? _wonderlandProfiles;
    private Dictionary<uint, WonderlandSpawnPolicy>? _wonderlandPolicies;
    private GameplayContentCatalog? _wonderlandContent;
    private int[]? _wonderlandFinalizedIds;

    internal bool TryConfigureWonderland(GameplayContentCatalog content,
        IReadOnlyList<WonderlandParticipant> participants, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(participants);
        lock (_wonderlandGate)
        {
            if (_wonderlandRun is not null)
            {
                var existing = _wonderlandRun.Snapshot();
                return existing.StartedAt == startedAt && existing.Participants.SequenceEqual(participants);
            }
            if (!WonderlandEncounterPolicy.IsWonderlandInstance(Descriptor)) return false;
            try
            {
                var run = new WonderlandRunRuntime(Descriptor, participants, startedAt);
                var policies = Enumerable.Range(1, 8).Select(i =>
                    WonderlandMonsterPlan.Create(i, participants.Count, participants[0].Camp)).ToArray();
                var definitions = PrepareWonderlandDefinitions(content, policies);
                var profiles = MonsterCombatProfileCatalog.Create(content);
                profiles = profiles.WithAuthoredOverrides(WonderlandEncounterPolicy.Map,
                    definitions.SelectMany(stage => stage).Select(definition =>
                    {
                        var policy = policies.SelectMany(stage => stage).Single(p => p.ObjectId == definition.ObjectId);
                        return (definition.ObjectId, definition.TemplateKey,
                            WonderlandMonsterProfilePolicy.Resolve(profiles.Resolve(definition), policy));
                    }).ToArray());
                var monsterRuntime = new WonderlandMonsterRuntime(Descriptor.InstanceId,
                    run.Snapshot().Deadline, participants.Select(p => p.CharacterId), policies.Sum(stage => stage.Length));
                lock (_membershipGate)
                lock (_monsterRuntimeGate)
                {
                    if (_monsterRuntime is not null && _monsterRuntime.Count != 0) return false;
                    ValidateWonderlandObjectIds(definitions.SelectMany(stage => stage).ToArray());
                    _wonderlandRun = run;
                    _wonderlandContent = content;
                    _wonderlandDefinitions = definitions;
                    _wonderlandProfiles = profiles;
                    _wonderlandPolicies = policies.SelectMany(stage => stage).ToDictionary(p => p.ObjectId);
                    _wonderlandMonsters = monsterRuntime;
                    _monsterRuntime = monsterRuntime;
                    _monsterRespawnPolicy = MonsterRespawnPolicy.Never;
                }
                return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                InvalidDataException or OverflowException)
            {
                Console.Error.WriteLine($"[wonderland] setup rejected: {error.Message}");
                return false;
            }
        }
    }

    internal bool TryGetWonderlandSnapshot(out WonderlandSnapshot snapshot)
    {
        lock (_wonderlandGate)
        {
            snapshot = _wonderlandRun?.Snapshot()!;
            return snapshot is not null;
        }
    }

    internal bool TryGetWonderlandSpawnPolicy(uint objectId, out WonderlandSpawnPolicy policy)
    {
        lock (_wonderlandGate)
        {
            policy = null!;
            return _wonderlandPolicies is not null && _wonderlandPolicies.TryGetValue(objectId, out policy!);
        }
    }

    internal bool TrySpawnPendingWonderlandStage(DateTimeOffset now, out WonderlandSnapshot snapshot)
    {
        lock (_wonderlandGate)
        {
            snapshot = _wonderlandRun?.Advance(now)!;
            if (snapshot is null || snapshot.State != WonderlandRunState.Active || !snapshot.PublicationPending)
            {
                if (snapshot is not null && snapshot.State != WonderlandRunState.Active)
                {
                    ScheduleWonderlandBossCorpseRetirement(snapshot);
                    UpdateWonderlandCombatWindow(snapshot, now);
                }
                return false;
            }
            lock (_membershipGate)
            lock (_monsterRuntimeGate)
            {
                if (!_wonderlandMonsters!.CanPublish(now))
                {
                    snapshot = _wonderlandRun!.Advance(snapshot.Deadline);
                    return false;
                }
                var definitions = _wonderlandDefinitions![snapshot.CurrentIsland - 1];
                ValidateWonderlandObjectIds(definitions);
                var staged = definitions.Select(definition =>
                {
                    var policy = _wonderlandPolicies![definition.ObjectId];
                    var behavior = new MonsterBehaviorPolicy(policy.AggroRadius, policy.LeashRadius,
                        policy.Stationary ? 0f : 2f, policy.Stationary, policy.Stats.AttackInterval,
                        passive: policy.Role == WonderlandMonsterRole.FiringHoop,
                        navigationGrid: policy.Stage == 8 ? MonsterNavigationGrid.WonderlandFinalIsland : null);
                    var runtime = MonsterMapRuntimeFactory.Create(_monsterRuntimeMode, MapId,
                        [definition], now, worldBossCatalog: _worldBossCatalog,
                        respawnPolicy: MonsterRespawnPolicy.Never, monsterCombatProfiles: _wonderlandProfiles,
                        behaviorPolicy: behavior);
                    return (Policy: policy, Runtime: runtime, Monster: runtime.Snapshot().Single());
                }).ToArray();
                _wonderlandMonsters.ValidateAttachment(staged);
                var identities = staged.Select(entry => new WonderlandMonsterIdentity(
                    entry.Monster.ObjectId, entry.Monster.SpawnGeneration)).ToArray();
                if (!_wonderlandRun!.TryBindStage(snapshot.CurrentIsland, identities, now, out snapshot)) return false;
                _wonderlandMonsters.AttachValidated(staged);
                return true;
            }
        }
    }

    internal WonderlandSnapshot AdvanceWonderland(DateTimeOffset now)
    {
        lock (_wonderlandGate)
        {
            var snapshot = RequireWonderlandRun().Advance(now);
            ScheduleWonderlandBossCorpseRetirement(snapshot);
            UpdateWonderlandCombatWindow(snapshot, now);
            return snapshot;
        }
    }

    internal WonderlandSnapshot CancelWonderland(DateTimeOffset now)
    {
        lock (_wonderlandGate)
        {
            var snapshot = RequireWonderlandRun().Cancel(now);
            ScheduleWonderlandBossCorpseRetirement(snapshot);
            StopWonderlandCombat();
            return snapshot;
        }
    }

    internal WonderlandKillResult RecordCommittedWonderlandKill(WorldInstanceId instanceId,
        uint objectId, uint generation, DateTimeOffset now)
    {
        lock (_wonderlandGate)
        lock (_monsterRuntimeGate)
        {
            var run = RequireWonderlandRun();
            if (instanceId != Descriptor.InstanceId || !_wonderlandMonsters!.TryGetSnapshot(objectId, out var monster) ||
                monster.SpawnGeneration != generation || monster.IsAlive || monster.CurrentHealth != 0)
                return new(instanceId != Descriptor.InstanceId ? WonderlandKillOutcome.WrongInstance :
                    WonderlandKillOutcome.UnknownMonster, run.Snapshot());
            var result = run.RecordCommittedKill(instanceId, objectId, generation, now);
            RetainCommittedWonderlandBossCorpse(result, monster, now);
            ScheduleWonderlandBossCorpseRetirement(result.Snapshot);
            _wonderlandMonsters.UpdateCombatWindow(result.Snapshot, now);
            return result;
        }
    }

    private WonderlandRunRuntime RequireWonderlandRun() => _wonderlandRun ??
        throw new InvalidOperationException("Wonderland has not been configured for this exact instance.");

    private void StopWonderlandCombat()
    {
        lock (_monsterRuntimeGate) _wonderlandMonsters?.StopCombat();
    }

    private void UpdateWonderlandCombatWindow(WonderlandSnapshot snapshot, DateTimeOffset now)
    {
        lock (_monsterRuntimeGate) _wonderlandMonsters?.UpdateCombatWindow(snapshot, now);
    }

    private void ValidateWonderlandObjectIds(IReadOnlyList<CapturedMonsterSpawn> definitions)
    {
        EnsureMonsterObjectIdsDoNotCollideWithNpcs(definitions);
        var players = _sessions.Values.Select(context => context.ObjectId).ToHashSet();
        if (definitions.Any(definition => players.Contains(definition.ObjectId)))
            throw new InvalidOperationException("Wonderland monster identity collides with a player.");
    }
}
