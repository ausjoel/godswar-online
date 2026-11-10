using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    /// <summary>Published islands retain living actors and corpse identity until the run ends.</summary>
    private sealed class WonderlandMonsterRuntime(WorldInstanceId instanceId,
        DateTimeOffset deadline, IEnumerable<int> participants, int expectedActorCount) : IMonsterMapRuntime
    {
        private readonly Dictionary<uint, (WonderlandSpawnPolicy Policy, IMonsterMapRuntime Runtime)> _owners = [];
        private readonly HashSet<int> _participants = participants.ToHashSet();
        /// <summary>Adds a member admitted after the run was sealed.</summary>
        internal bool TryAddParticipant(int characterId) => _participants.Add(characterId);
        private readonly List<MonsterRuntimeUpdate> _terminalUpdates = [];
        private bool _terminal;
        private bool _publicationPending;
        private bool _finalGuardUnlocked;
        private DateTimeOffset _combatDeadline = deadline;
        public byte MapId => WonderlandEncounterPolicy.Map;
        public int Count => _owners.Count;
        internal bool CanPublish(DateTimeOffset now) => CanFight(now);
        // Called with the map's Wonderland gate before its monster gate. Combat
        // never calls back into the run while holding the monster gate.
        internal void UpdateCombatWindow(WonderlandSnapshot run, DateTimeOffset now)
        {
            // Run.Advance and stale kill rejection return the latest snapshot.
            // An older caller must not freeze an already earned treasure window.
            if (now < run.LastObservedAt) now = run.LastObservedAt;
            // Unlock from committed run progress, never from an uncredited HP-zero snapshot.
            _finalGuardUnlocked |= run.CurrentIsland == 8 && !run.PublicationPending &&
                run.RequiredMonstersRemaining <= 1;
            if (!WonderlandCompletionPolicy.IsCombatWindowOpen(run, now))
            {
                StopCombat();
                return;
            }
            _combatDeadline = run.State == WonderlandRunState.Completed
                ? run.TerminalAt!.Value + WonderlandCompletionPolicy.TreasureWindow : run.Deadline;
        }
        internal void StopCombat()
        {
            if (_terminal) return;
            _terminal = true;
            foreach (var entry in _owners.Values)
            {
                var monster = entry.Runtime.Snapshot().Single();
                if (monster.IsAlive && monster.IsSpawned && monster.IsMoving)
                    _terminalUpdates.Add(new(MonsterRuntimeUpdateKind.Arrived,
                        FreezeTerminal(monster), MovementEndField: 1));
            }
        }

        private bool CanFight(DateTimeOffset now)
        {
            if (now >= _combatDeadline) StopCombat();
            return !_terminal;
        }

        internal void ValidateAttachment(IReadOnlyList<(WonderlandSpawnPolicy Policy,
            IMonsterMapRuntime Runtime, MonsterRuntimeSnapshot Monster)> staged)
        {
            if (_terminal || staged.Count == 0 || Count + staged.Count > expectedActorCount ||
                staged.Select(s => s.Policy.ObjectId).Distinct().Count() != staged.Count ||
                staged.Any(s => _owners.ContainsKey(s.Policy.ObjectId) || s.Runtime.MapId != MapId ||
                    s.Runtime.Count != 1 || s.Monster.ObjectId != s.Policy.ObjectId ||
                    s.Monster.SpawnGeneration != 1 || !s.Monster.IsAlive || !s.Monster.IsSpawned ||
                    s.Monster.RespawnAt is not null || s.Monster.MaximumHealth != s.Policy.Stats.MaximumHealth))
                throw new InvalidOperationException("Wonderland staged monster identities are invalid or reused.");
        }

        internal void AttachValidated(IReadOnlyList<(WonderlandSpawnPolicy Policy,
            IMonsterMapRuntime Runtime, MonsterRuntimeSnapshot Monster)> staged)
        {
            foreach (var entry in staged) _owners.Add(entry.Policy.ObjectId, (entry.Policy, entry.Runtime));
            _publicationPending = true;
        }

        private MonsterRuntimeSnapshot FreezeTerminal(MonsterRuntimeSnapshot monster)
        {
            return monster.IsAlive && _terminal
                ? monster with { IsMoving = false,
                    VelocityX = 0, VelocityY = 0, VelocityZ = 0, MovementTicks = 0,
                    RemainingMovementTicks = 0, CombatPhase = MonsterCombatPhase.None }
                : monster;
        }

        public IReadOnlyList<MonsterRuntimeSnapshot> Snapshot() => _owners.Values
            .Select(e => FreezeTerminal(e.Runtime.Snapshot().Single())).OrderBy(m => m.ObjectId).ToArray();

        public bool TryGetSnapshot(uint objectId, out MonsterRuntimeSnapshot snapshot)
        {
            if (_owners.TryGetValue(objectId, out var entry) && entry.Runtime.TryGetSnapshot(objectId, out snapshot))
            {
                snapshot = FreezeTerminal(snapshot);
                return true;
            }
            snapshot = null!;
            return false;
        }

        private bool TryGetCombatOwner(uint objectId, DateTimeOffset now, int? attacker,
            out IMonsterMapRuntime runtime)
        {
            runtime = null!;
            if (!CanFight(now) || attacker.HasValue && !_participants.Contains(attacker.Value) ||
                !_owners.TryGetValue(objectId, out var entry) || entry.Policy.IsAllied ||
                IsLockedFinalGuard(entry.Policy)) return false;
            runtime = entry.Runtime;
            return true;
        }

        public bool TryApplyDamage(uint objectId, uint damage, int? attackerCharacterId,
            uint? expectedSpawnGeneration, DateTimeOffset now, out MonsterDamageResult result)
        {
            result = null!;
            return TryGetCombatOwner(objectId, now, attackerCharacterId, out var owner) &&
                owner.TryApplyDamage(objectId, damage, attackerCharacterId, expectedSpawnGeneration, now, out result);
        }

        public bool TryApplyPeriodicDamage(uint objectId, uint damage, int sourceCharacterId,
            uint expectedSpawnGeneration, DateTimeOffset now, out MonsterDamageResult result)
        {
            result = null!;
            return TryGetCombatOwner(objectId, now, sourceCharacterId, out var owner) &&
                owner.TryApplyPeriodicDamage(objectId, damage, sourceCharacterId, expectedSpawnGeneration, now, out result);
        }

        public bool TryApplyStun(uint objectId, int attackerCharacterId, TimeSpan duration,
            uint? expectedSpawnGeneration, DateTimeOffset now, out MonsterStunResult result)
        {
            result = null!;
            return TryGetCombatOwner(objectId, now, attackerCharacterId, out var owner) &&
                owner.TryApplyStun(objectId, attackerCharacterId, duration, expectedSpawnGeneration, now, out result);
        }

        public bool TrySetMovementSpeedBasisPoints(uint objectId, uint generation, int basisPoints) =>
            !_terminal && _owners.TryGetValue(objectId, out var owner) &&
            owner.Runtime.TrySetMovementSpeedBasisPoints(objectId, generation, basisPoints);

        public bool TryApplyControl(uint objectId, int attackerCharacterId,
            Godswar.Server.State.HostileStatusEffectDefinition definition, uint generation,
            DateTimeOffset now, out MonsterControlResult result)
        {
            result = null!;
            return TryGetCombatOwner(objectId, now, attackerCharacterId, out var owner) &&
                owner.TryApplyControl(objectId, attackerCharacterId, definition, generation, now, out result);
        }

        public bool TrySetCorpseDespawnAt(uint objectId, uint generation, DateTimeOffset? despawnAt) =>
            _owners.TryGetValue(objectId, out var owner) && owner.Runtime.TrySetCorpseDespawnAt(objectId, generation, despawnAt);

        public void ClearAggroForCharacter(int characterId, DateTimeOffset now)
        {
            foreach (var entry in _owners.Values) entry.Runtime.ClearAggroForCharacter(characterId, now);
        }

        public void ClearAggroForCharacterStateOnly(int characterId, DateTimeOffset now)
        {
            foreach (var entry in _owners.Values) entry.Runtime.ClearAggroForCharacterStateOnly(characterId, now);
        }

        public MonsterRuntimeTick Advance(DateTimeOffset now, IReadOnlyList<MonsterCombatTarget>? combatTargets = null)
        {
            var active = CanFight(now);
            var participantsInMap = (combatTargets ?? []).Where(t => t.WorldInstanceId == instanceId &&
                _participants.Contains(t.CharacterId)).ToArray();
            var targetsByIsland = new Dictionary<int, MonsterCombatTarget[]>();
            if (active)
                foreach (var island in _owners.Values.Select(entry => entry.Policy.Stage).Distinct())
                    targetsByIsland[island] = participantsInMap.Where(target =>
                        WonderlandTerrainPolicy.IsCombatArea(island, target.X, target.Z)).ToArray();
            var updates = new List<MonsterRuntimeUpdate>(_terminalUpdates);
            _terminalUpdates.Clear();
            var changed = _publicationPending;
            _publicationPending = false;
            foreach (var entry in _owners.Values)
            {
                var snapshot = entry.Runtime.Snapshot().Single();
                var canAttack = active && !IsLockedFinalGuard(entry.Policy);
                // Surviving actors remain active through the completed treasure window.
                if (snapshot.IsAlive && !canAttack) continue;
                var tick = entry.Runtime.Advance(now, canAttack && !entry.Policy.IsAllied
                    ? targetsByIsland[entry.Policy.Stage] : []);
                changed |= tick.PositionsChanged;
                updates.AddRange(tick.Updates.Where(update =>
                    update.Kind != MonsterRuntimeUpdateKind.Attacked || canAttack && !entry.Policy.IsAllied));
            }
            return new(changed, updates);
        }

        private bool IsLockedFinalGuard(WonderlandSpawnPolicy policy) =>
            policy.Stage == 8 && policy.Role == WonderlandMonsterRole.ChestGuard && !_finalGuardUnlocked;
    }
}
