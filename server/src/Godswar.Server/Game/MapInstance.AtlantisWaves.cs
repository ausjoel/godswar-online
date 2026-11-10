using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private AtlantisWaveRuntime? _atlantisWaves;
    private AtlantisMonsterRuntime? _atlantisMonsters;
    private (int CharacterId, int Level)[]? _atlantisAdmittedParty;
    private CapturedMonsterSpawn[][]? _atlantisWaveDefinitions;
    private MonsterCombatProfileCatalog? _atlantisCombatProfiles;

    internal bool TryConfigureAtlantisWaves(
        GameplayContentCatalog content,
        IReadOnlyList<(int CharacterId, int Level)> admittedParty,
        DateTimeOffset initializedAt)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(admittedParty);
        var party = admittedParty.OrderBy(member => member.CharacterId).ToArray();
        lock (_atlantisEncounterGate)
        {
            if (_atlantisWaves is not null)
            {
                return party.SequenceEqual(_atlantisAdmittedParty!);
            }
            if (!AtlantisEncounterPolicy.IsAtlantisInstance(Descriptor) ||
                _atlantisRun is not null && _atlantisRun.Snapshot().StartedAt != initializedAt)
            {
                return false;
            }

            try
            {
                var definitions = PrepareAtlantisWaveDefinitions(content, party);
                var petDefinitions = PrepareAtlantisPetDefinitions(content, party);
                var waves = new AtlantisWaveRuntime(Descriptor, initializedAt);
                var profiles = MonsterCombatProfileCatalog.Create(content);
                var monsters = new AtlantisMonsterRuntime(MapId, waves.Snapshot().Deadline);
                var pets = MonsterMapRuntimeFactory.Create(_monsterRuntimeMode, MapId,
                    petDefinitions, initializedAt, worldBossCatalog: _worldBossCatalog,
                    respawnPolicy: MonsterRespawnPolicy.Never, monsterCombatProfiles: profiles,
                    behaviorPolicy: new MonsterBehaviorPolicy(14f, 32f, 2f));
                var petSnapshots = pets.Snapshot();
                monsters.ValidateAttachment(pets, petSnapshots);
                lock (_membershipGate)
                {
                    lock (_monsterRuntimeGate)
                    {
                        if (_monsterRuntime is not null && _monsterRuntime.Count != 0)
                        {
                            return false;
                        }
                        ValidateAtlantisObjectIdIsolation(definitions.SelectMany(wave => wave)
                            .Concat(petDefinitions).ToArray());
                        monsters.AttachValidated(pets, petSnapshots);
                        _atlantisAdmittedParty = party;
                        _atlantisWaveDefinitions = definitions;
                        _atlantisCombatProfiles = profiles;
                        _atlantisWaves = waves;
                        _atlantisMonsters = monsters;
                        _monsterRuntime = monsters;
                        _monsterRespawnPolicy = MonsterRespawnPolicy.Never;
                    }
                }
                return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or
                InvalidOperationException or OverflowException)
            {
                Console.Error.WriteLine($"[atlantis] wave setup rejected: {error.Message}");
                return false;
            }
        }
    }

    internal bool TryGetAtlantisWaveSnapshot(out AtlantisWaveSnapshot snapshot)
    {
        lock (_atlantisEncounterGate)
        {
            snapshot = _atlantisWaves?.Snapshot()!;
            return snapshot is not null;
        }
    }

    internal bool TrySpawnPendingAtlantisWave(DateTimeOffset now, out AtlantisWaveSnapshot snapshot)
    {
        lock (_atlantisEncounterGate)
        {
            snapshot = _atlantisWaves?.Snapshot()!;
            if (_atlantisWaves is null || _atlantisRun is null)
            {
                return false;
            }
            var score = _atlantisRun.Advance(now);
            if (score.State != AtlantisRunState.Active)
            {
                _atlantisWaves.Advance(now);
                StopAtlantisMonsterCombat();
                snapshot = _atlantisWaves.Snapshot();
                return false;
            }
            var pending = _atlantisWaves.PreviewPendingWave(now);
            if (pending is null)
            {
                snapshot = _atlantisWaves.Snapshot();
                return false;
            }

            var definitions = _atlantisWaveDefinitions![pending.Definition.WaveIndex];
            lock (_membershipGate)
            {
                lock (_monsterRuntimeGate)
                {
                    if (!_atlantisMonsters!.CanPublish(now))
                    {
                        // Combat can observe the deadline before an older
                        // queued world tick. Do not bind a nonexistent wave.
                        _atlantisRun.Advance(_atlantisMonsters.Deadline);
                        snapshot = _atlantisWaves.Advance(_atlantisMonsters.Deadline);
                        return false;
                    }
                    ValidateAtlantisObjectIdIsolation(definitions);
                    var staged = MonsterMapRuntimeFactory.Create(_monsterRuntimeMode, MapId,
                        definitions, now, worldBossCatalog: _worldBossCatalog,
                        respawnPolicy: MonsterRespawnPolicy.Never,
                        monsterCombatProfiles: _atlantisCombatProfiles,
                        behaviorPolicy: AtlantisMonsterCombatPolicy.Behavior);
                    var stagedMonsters = staged.Snapshot().OrderBy(monster => monster.ObjectId).ToArray();
                    if (stagedMonsters.Length != pending.Definition.Slots.Length ||
                        stagedMonsters.Any(monster => monster.SpawnGeneration != 1 || !monster.IsAlive ||
                            !monster.IsSpawned || monster.RespawnAt is not null))
                    {
                        throw new InvalidOperationException("Atlantis staged wave did not preserve its never-respawn identities.");
                    }
                    _atlantisMonsters.ValidateAttachment(staged, stagedMonsters);
                    var identities = stagedMonsters.Select((monster, index) => new AtlantisWaveMonsterIdentity(
                        monster.ObjectId, monster.SpawnGeneration, pending.Definition.Slots[index].Rank)).ToArray();
                    var binding = _atlantisWaves.BindPendingWave(pending.Token, identities, now);
                    snapshot = binding.Snapshot;
                    if (binding.Outcome != AtlantisWaveBindOutcome.Bound)
                    {
                        return false;
                    }

                    // Validation and staging complete before the publication point.
                    // Object IDs are disjoint across all 25 retained wave runtimes.
                    _atlantisMonsters.AttachValidated(staged, stagedMonsters);
                    return true;
                }
            }
        }
    }

    private void ValidateAtlantisObjectIdIsolation(IReadOnlyList<CapturedMonsterSpawn> definitions)
    {
        EnsureMonsterObjectIdsDoNotCollideWithNpcs(definitions);
        var players = _sessions.Values.Select(context => context.ObjectId).ToHashSet();
        if (definitions.Any(definition => players.Contains(definition.ObjectId)))
        {
            throw new InvalidOperationException("Atlantis monster identity collides with a player.");
        }
    }

    private void StopAtlantisMonsterCombat()
    {
        lock (_monsterRuntimeGate)
        {
            _atlantisMonsters?.StopCombat();
        }
    }
}
