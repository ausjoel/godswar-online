namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    // Existing Legacy/ECS runtimes retain their exact combat and corpse state.
    // A run retains 25 disjoint waves plus two ambient capture pets.
    private sealed class AtlantisMonsterRuntime(byte mapId, DateTimeOffset deadline) : IMonsterMapRuntime
    {
        private readonly List<IMonsterMapRuntime> _waves = new(26);
        private readonly Dictionary<uint, IMonsterMapRuntime> _owners = new(247);
        private bool _publicationPending;
        private bool _terminal;

        public byte MapId { get; } = mapId;
        public int Count => _owners.Count;
        internal DateTimeOffset Deadline { get; } = deadline;

        internal void StopCombat() => _terminal = true;
        internal bool CanPublish(DateTimeOffset now) => CanFight(now);

        private bool CanFight(DateTimeOffset now)
        {
            if (now >= Deadline) _terminal = true;
            return !_terminal;
        }

        internal void ValidateAttachment(IMonsterMapRuntime wave, IReadOnlyList<MonsterRuntimeSnapshot> snapshots)
        {
            if (_terminal || wave.MapId != MapId || snapshots.Count == 0 ||
                _waves.Count >= 26 || Count + snapshots.Count > 247 ||
                snapshots.Select(monster => monster.ObjectId).Distinct().Count() != snapshots.Count ||
                snapshots.Any(monster => _owners.ContainsKey(monster.ObjectId) ||
                    !monster.IsAlive || !monster.IsSpawned || monster.SpawnGeneration != 1))
            {
                throw new InvalidOperationException("Atlantis wave runtime identities are invalid or reused.");
            }
        }

        internal void AttachValidated(IMonsterMapRuntime wave, IReadOnlyList<MonsterRuntimeSnapshot> snapshots)
        {
            _waves.Add(wave);
            _publicationPending = true;
            foreach (var monster in snapshots)
            {
                _owners.Add(monster.ObjectId, wave);
            }
        }

        public IReadOnlyList<MonsterRuntimeSnapshot> Snapshot() =>
            _waves.SelectMany(wave => wave.Snapshot()).OrderBy(monster => monster.ObjectId).ToArray();

        public bool TryGetSnapshot(uint objectId, out MonsterRuntimeSnapshot snapshot)
        {
            if (_owners.TryGetValue(objectId, out var wave))
            {
                return wave.TryGetSnapshot(objectId, out snapshot);
            }
            snapshot = null!;
            return false;
        }

        public bool TryApplyDamage(uint objectId, uint damage, int? attackerCharacterId,
            uint? expectedSpawnGeneration, DateTimeOffset now, out MonsterDamageResult result)
        {
            if (CanFight(now) && _owners.TryGetValue(objectId, out var wave))
            {
                return wave.TryApplyDamage(objectId, damage, attackerCharacterId,
                    expectedSpawnGeneration, now, out result);
            }
            result = null!;
            return false;
        }

        public bool TryApplyPeriodicDamage(uint objectId, uint damage, int sourceCharacterId,
            uint expectedSpawnGeneration, DateTimeOffset now, out MonsterDamageResult result)
        {
            if (CanFight(now) && _owners.TryGetValue(objectId, out var wave))
            {
                return wave.TryApplyPeriodicDamage(objectId, damage, sourceCharacterId,
                    expectedSpawnGeneration, now, out result);
            }
            result = null!;
            return false;
        }

        public bool TrySetMovementSpeedBasisPoints(uint objectId, uint expectedSpawnGeneration, int speedBasisPoints) =>
            !_terminal && _owners.TryGetValue(objectId, out var wave) &&
            wave.TrySetMovementSpeedBasisPoints(objectId, expectedSpawnGeneration, speedBasisPoints);

        public bool TrySetCorpseDespawnAt(uint objectId, uint expectedSpawnGeneration, DateTimeOffset? despawnAt) =>
            _owners.TryGetValue(objectId, out var wave) &&
            wave.TrySetCorpseDespawnAt(objectId, expectedSpawnGeneration, despawnAt);

        public bool TryApplyStun(uint objectId, int attackerCharacterId, TimeSpan duration,
            uint? expectedSpawnGeneration, DateTimeOffset now, out MonsterStunResult result)
        {
            if (CanFight(now) && _owners.TryGetValue(objectId, out var wave))
            {
                return wave.TryApplyStun(objectId, attackerCharacterId, duration,
                    expectedSpawnGeneration, now, out result);
            }
            result = null!;
            return false;
        }

        public void ClearAggroForCharacter(int characterId, DateTimeOffset now)
        {
            foreach (var wave in _waves) wave.ClearAggroForCharacter(characterId, now);
        }

        public bool TryApplyControl(uint objectId, int attackerCharacterId,
            Godswar.Server.State.HostileStatusEffectDefinition definition, uint generation,
            DateTimeOffset now, out MonsterControlResult result)
        {
            result = null!;
            return CanFight(now) && _owners.TryGetValue(objectId, out var wave) &&
                wave.TryApplyControl(objectId, attackerCharacterId, definition, generation, now, out result);
        }

        public void ClearAggroForCharacterStateOnly(int characterId, DateTimeOffset now)
        {
            foreach (var wave in _waves) wave.ClearAggroForCharacterStateOnly(characterId, now);
        }

        public MonsterRuntimeTick Advance(DateTimeOffset now, IReadOnlyList<MonsterCombatTarget>? combatTargets = null)
        {
            var active = CanFight(now);
            var updates = new List<MonsterRuntimeUpdate>();
            var positionsChanged = _publicationPending;
            _publicationPending = false;
            foreach (var wave in _waves)
            {
                var tick = wave.Advance(now, active ? combatTargets : []);
                positionsChanged |= tick.PositionsChanged;
                updates.AddRange(active ? tick.Updates : tick.Updates.Where(
                    update => update.Kind != MonsterRuntimeUpdateKind.Attacked));
            }
            return new(positionsChanged, updates);
        }
    }
}
