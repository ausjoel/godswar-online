using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// ROUND3 runtime spawns (2026-11-09): what one runtime spawn is for. Owner-only spawns can only be hurt by,
/// and only fight, their owner, so their corpse loot (claimed by the killer) is the owner's.
/// </summary>
/// <remarks>
/// ROUND3 summons (2026-11-09): <paramref name="OwnerLimit"/> is how many live spawns of the same source family
/// (the part of <paramref name="Source"/> before the first ':', e.g. "treasure", "totem", "item") one owner may
/// have on the map, this one included; 1 = the original rule. <paramref name="Camp"/> (0 Sparta, 1 Athens) makes
/// the spawn an ally of that faction: packet byte 5 carries it (the client draws it as friendly and the PORTALS
/// aggro filter never lets it start a fight with that faction), and that faction's players cannot hurt it.
/// </remarks>
internal sealed record WorldRuntimeSpawnSpec(
    int OwnerCharacterId,
    bool OwnerOnly,
    TimeSpan Lifetime,
    IReadOnlyList<WorldRuntimeSpawnDrop> Drops,
    string Source,
    int OwnerLimit = 1,
    byte? Camp = null,
    System.Collections.Concurrent.ConcurrentDictionary<int, byte>? Attackers = null)
// W2 F-27 TROY (slice 2): Attackers, when given, collects every character whose damage landed (the Troy boss chests
// pay only those). Null for every other caller, which then behaves exactly as before.
{
    /// <summary>The source family the owner limit counts in: "treasure:12003" -> "treasure".</summary>
    public string SourceFamily
    {
        get
        {
            var colon = Source.IndexOf(':');
            return colon < 0 ? Source : Source[..colon];
        }
    }

    /// <summary>
    /// ROUND3 summons, the admin F-150 ("summons no EXP"): the source families whose kills pay no kill EXP, Talent EXP
    /// or pet EXP to anyone (killer or party). Totem Pole soldiers ("totem") are allies an enemy player kills, so
    /// two alts could otherwise farm them; treasure guardians ("treasure") are the dig's own summons, the treasure is
    /// paid at the dig. Seal-box monsters and scroll bosses ("item") and the Troy heroes ("troy") keep their rewards.
    /// </summary>
    internal static readonly IReadOnlySet<string> NoKillRewardFamilies =
        new HashSet<string>(StringComparer.Ordinal) { "totem", "treasure" };

    /// <summary>F-150: whether killing this spawn pays the ordinary kill EXP / Talent EXP / pet EXP.</summary>
    public bool PaysKillExperience => !NoKillRewardFamilies.Contains(SourceFamily);
}

/// <summary>One corpse loot line of a runtime spawn. Quantity never exceeds the item's stack (99).</summary>
internal readonly record struct WorldRuntimeSpawnDrop(uint ItemId, int Quantity);

/// <summary>
/// ROUND3 runtime spawns: a contained overlay on an open-world map's monster runtime. The base runtime (the
/// published roster, Legacy or ECS) is untouched; each runtime spawn is its own single-monster runtime with
/// <see cref="MonsterRespawnPolicy.Never"/>, routed by object id, in the same shape as
/// <c>MapInstance.AtlantisMonsterRuntime</c>. Spawns live in a disjoint object-id pool, despawn when their
/// lifetime runs out (alive) or when their corpse is retired (dead), and never emit Respawned updates, so the
/// world-boss notice and respawn state cannot see them. Every call happens under the map's monster gate.
/// </summary>
internal sealed class WorldRuntimeSpawnOverlay : IMonsterMapRuntime
{
    /// <summary>The ordinary client monster registry is 8000..49999; this pool is unused by every other system
    /// (Medusa 40000+, Atlantis 42000+, authored 43000+/44000+, Wonderland 46000+, NPC 47750).</summary>
    internal const uint FirstObjectId = 48_000;
    internal const uint LastObjectId = 48_999;

    /// <summary>GENERATED: at most this many live runtime spawns per map.</summary>
    internal const int MaximumPerMap = 20;

    /// <summary>GENERATED: at most this many live runtime spawns per owner and source family, unless the spec
    /// asks for more (<see cref="WorldRuntimeSpawnSpec.OwnerLimit"/>, never above <see cref="MaximumPerMap"/>).</summary>
    internal const int MaximumPerOwner = 1;

    /// <summary>An id is not handed out again this soon after retirement, so a new spawn (generation 1 again)
    /// can never alias an old corpse's unclaimed loot, which is keyed by (instance, object id).</summary>
    internal static readonly TimeSpan IdQuarantine = TimeSpan.FromMinutes(5);

    /// <summary>A dead spawn whose corpse is somehow kept is removed this long after its lifetime anyway.</summary>
    internal static readonly TimeSpan CorpseGrace = TimeSpan.FromMinutes(2);

    private readonly IMonsterMapRuntime _base;
    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly Dictionary<uint, DateTimeOffset> _retiredAt = [];
    private uint _nextObjectId = FirstObjectId;
    private bool _publicationPending;
    private readonly Func<int, int?>? _campOf;

    /// <param name="campOf">ROUND3 summons: the camp of a character on this map (null when unknown); used to keep
    /// a faction's players off that faction's allied spawns.</param>
    public WorldRuntimeSpawnOverlay(IMonsterMapRuntime baseRuntime, Func<int, int?>? campOf = null)
    {
        _base = baseRuntime ?? throw new ArgumentNullException(nameof(baseRuntime));
        _campOf = campOf;
    }

    internal static bool IsRuntimeSpawnObjectId(uint objectId) =>
        objectId is >= FirstObjectId and <= LastObjectId;

    internal IMonsterMapRuntime Base => _base;

    internal int SpawnCount => _entries.Count;

    public byte MapId => _base.MapId;

    public int Count => _base.Count + _entries.Count;

    internal int CountForOwner(int characterId) =>
        _entries.Values.Count(entry => entry.Spec.OwnerCharacterId == characterId);

    /// <summary>ROUND3 summons: the owner's live spawns of one source family.</summary>
    internal int CountForOwner(int characterId, string sourceFamily) =>
        _entries.Values.Count(entry => entry.Spec.OwnerCharacterId == characterId &&
            string.Equals(entry.Spec.SourceFamily, sourceFamily, StringComparison.Ordinal));

    /// <summary>The effective owner limit of a spec: at least 1, at most the map limit.</summary>
    internal static int OwnerLimitOf(WorldRuntimeSpawnSpec spec) =>
        Math.Clamp(spec.OwnerLimit, MaximumPerOwner, MaximumPerMap);

    internal bool TryGetSpawn(uint objectId, Guid runtimeInstanceId, out WorldRuntimeSpawnSpec spec)
    {
        if (_entries.TryGetValue(objectId, out var entry) && entry.RuntimeInstanceId == runtimeInstanceId)
        {
            spec = entry.Spec;
            return true;
        }
        spec = null!;
        return false;
    }

    /// <summary>The next free id, round robin, skipping live spawns, quarantined ids and <paramref name="taken"/>.</summary>
    internal bool TryAllocateObjectId(IReadOnlySet<uint> taken, DateTimeOffset now, out uint objectId)
    {
        ArgumentNullException.ThrowIfNull(taken);
        const uint size = LastObjectId - FirstObjectId + 1;
        for (var attempt = 0u; attempt < size; attempt++)
        {
            var candidate = _nextObjectId;
            _nextObjectId = candidate == LastObjectId ? FirstObjectId : candidate + 1;
            if (_entries.ContainsKey(candidate) || taken.Contains(candidate) ||
                _retiredAt.TryGetValue(candidate, out var retired) && now - retired < IdQuarantine)
            {
                continue;
            }
            objectId = candidate;
            return true;
        }
        objectId = 0;
        return false;
    }

    /// <summary>
    /// Attaches a staged single-monster runtime. Every check runs before anything changes; a refusal leaves
    /// the overlay exactly as it was.
    /// </summary>
    internal bool TryAttach(IMonsterMapRuntime runtime, WorldRuntimeSpawnSpec spec, DateTimeOffset now,
        out string refusal)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(spec);
        var monsters = runtime.Snapshot();
        if (runtime.MapId != MapId || monsters.Count != 1)
        {
            refusal = "staged runtime is not one monster on this map";
            return false;
        }
        var monster = monsters[0];
        if (!IsRuntimeSpawnObjectId(monster.ObjectId) || !monster.IsAlive || !monster.IsSpawned ||
            monster.SpawnGeneration != 1 || monster.RespawnAt is not null)
        {
            refusal = "staged monster identity is invalid";
            return false;
        }
        if (_entries.ContainsKey(monster.ObjectId) || _base.TryGetSnapshot(monster.ObjectId, out _))
        {
            refusal = "object id already in use";
            return false;
        }
        if (_entries.Count >= MaximumPerMap)
        {
            refusal = "map limit reached";
            return false;
        }
        if (CountForOwner(spec.OwnerCharacterId, spec.SourceFamily) >= OwnerLimitOf(spec))
        {
            refusal = "owner limit reached";
            return false;
        }

        _entries.Add(monster.ObjectId, new Entry(runtime, monster.ObjectId, monster.RuntimeInstanceId, spec,
            now + spec.Lifetime));
        _retiredAt.Remove(monster.ObjectId);
        _publicationPending = true;
        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// ROUND3 summons: attaches several staged spawns all or nothing (a treasure dig's boss set, a Totem Pole's
    /// soldiers). Every spawn is checked first, the map and owner limits for the whole group included; a refusal
    /// leaves the overlay exactly as it was.
    /// </summary>
    internal bool TryAttachGroup(IReadOnlyList<(IMonsterMapRuntime Runtime, WorldRuntimeSpawnSpec Spec)> group,
        DateTimeOffset now, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Count == 0)
        {
            refusal = "empty group";
            return false;
        }
        if (_entries.Count + group.Count > MaximumPerMap)
        {
            refusal = "map limit reached";
            return false;
        }
        foreach (var family in group.GroupBy(item => (item.Spec.OwnerCharacterId, item.Spec.SourceFamily)))
        {
            var limit = family.Min(item => OwnerLimitOf(item.Spec));
            if (CountForOwner(family.Key.OwnerCharacterId, family.Key.SourceFamily) + family.Count() > limit)
            {
                refusal = "owner limit reached";
                return false;
            }
        }
        var ids = new HashSet<uint>();
        foreach (var (runtime, spec) in group)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(spec);
            var monsters = runtime.Snapshot();
            if (runtime.MapId != MapId || monsters.Count != 1)
            {
                refusal = "staged runtime is not one monster on this map";
                return false;
            }
            var monster = monsters[0];
            if (!IsRuntimeSpawnObjectId(monster.ObjectId) || !monster.IsAlive || !monster.IsSpawned ||
                monster.SpawnGeneration != 1 || monster.RespawnAt is not null)
            {
                refusal = "staged monster identity is invalid";
                return false;
            }
            if (!ids.Add(monster.ObjectId) || _entries.ContainsKey(monster.ObjectId) ||
                _base.TryGetSnapshot(monster.ObjectId, out _))
            {
                refusal = "object id already in use";
                return false;
            }
        }

        foreach (var (runtime, spec) in group)
        {
            var monster = runtime.Snapshot()[0];
            _entries.Add(monster.ObjectId, new Entry(runtime, monster.ObjectId, monster.RuntimeInstanceId, spec,
                now + spec.Lifetime));
            _retiredAt.Remove(monster.ObjectId);
        }
        _publicationPending = true;
        refusal = string.Empty;
        return true;
    }

    /// <summary>Removes a spawn at once (a rolled-back use). The next tick publishes the removal.</summary>
    internal bool Remove(uint objectId, DateTimeOffset now)
    {
        if (!_entries.Remove(objectId))
        {
            return false;
        }
        _retiredAt[objectId] = now;
        _publicationPending = true;
        return true;
    }

    public IReadOnlyList<MonsterRuntimeSnapshot> Snapshot()
    {
        var baseMonsters = _base.Snapshot();
        if (_entries.Count == 0)
        {
            return baseMonsters;
        }
        return baseMonsters.Concat(_entries.Values.SelectMany(entry => entry.Runtime.Snapshot())).ToArray();
    }

    public bool TryGetSnapshot(uint objectId, out MonsterRuntimeSnapshot snapshot) =>
        _entries.TryGetValue(objectId, out var entry)
            ? entry.Runtime.TryGetSnapshot(objectId, out snapshot)
            : _base.TryGetSnapshot(objectId, out snapshot);

    public bool TryApplyDamage(uint objectId, uint damage, int? attackerCharacterId,
        uint? expectedSpawnGeneration, DateTimeOffset now, out MonsterDamageResult result)
    {
        if (!_entries.TryGetValue(objectId, out var entry))
        {
            return _base.TryApplyDamage(objectId, damage, attackerCharacterId, expectedSpawnGeneration, now,
                out result);
        }
        if (!entry.Admits(attackerCharacterId, _campOf))
        {
            result = null!;
            return false;
        }
        var applied = entry.Runtime.TryApplyDamage(objectId, damage, attackerCharacterId, expectedSpawnGeneration, now,
            out result);
        if (applied && attackerCharacterId is { } attacker)
        {
            entry.Spec.Attackers?.TryAdd(attacker, 0); // W2 F-27 TROY
        }
        return applied;
    }

    public bool TryApplyPeriodicDamage(uint objectId, uint damage, int sourceCharacterId,
        uint expectedSpawnGeneration, DateTimeOffset now, out MonsterDamageResult result)
    {
        if (!_entries.TryGetValue(objectId, out var entry))
        {
            return _base.TryApplyPeriodicDamage(objectId, damage, sourceCharacterId, expectedSpawnGeneration, now,
                out result);
        }
        if (!entry.Admits(sourceCharacterId, _campOf))
        {
            result = null!;
            return false;
        }
        var applied = entry.Runtime.TryApplyPeriodicDamage(objectId, damage, sourceCharacterId,
            expectedSpawnGeneration, now, out result);
        if (applied)
        {
            entry.Spec.Attackers?.TryAdd(sourceCharacterId, 0); // W2 F-27 TROY
        }
        return applied;
    }

    public bool TrySetMovementSpeedBasisPoints(uint objectId, uint expectedSpawnGeneration, int speedBasisPoints) =>
        _entries.TryGetValue(objectId, out var entry)
            ? entry.Runtime.TrySetMovementSpeedBasisPoints(objectId, expectedSpawnGeneration, speedBasisPoints)
            : _base.TrySetMovementSpeedBasisPoints(objectId, expectedSpawnGeneration, speedBasisPoints);

    public bool TrySetCorpseDespawnAt(uint objectId, uint expectedSpawnGeneration, DateTimeOffset? despawnAt) =>
        _entries.TryGetValue(objectId, out var entry)
            ? entry.Runtime.TrySetCorpseDespawnAt(objectId, expectedSpawnGeneration, despawnAt)
            : _base.TrySetCorpseDespawnAt(objectId, expectedSpawnGeneration, despawnAt);

    public bool TryApplyStun(uint objectId, int attackerCharacterId, TimeSpan duration,
        uint? expectedSpawnGeneration, DateTimeOffset now, out MonsterStunResult result)
    {
        if (!_entries.TryGetValue(objectId, out var entry))
        {
            return _base.TryApplyStun(objectId, attackerCharacterId, duration, expectedSpawnGeneration, now,
                out result);
        }
        if (!entry.Admits(attackerCharacterId, _campOf))
        {
            result = null!;
            return false;
        }
        return entry.Runtime.TryApplyStun(objectId, attackerCharacterId, duration, expectedSpawnGeneration, now,
            out result);
    }

    public bool TryApplyControl(uint objectId, int attackerCharacterId, HostileStatusEffectDefinition definition,
        uint expectedSpawnGeneration, DateTimeOffset now, out MonsterControlResult result)
    {
        if (!_entries.TryGetValue(objectId, out var entry))
        {
            return _base.TryApplyControl(objectId, attackerCharacterId, definition, expectedSpawnGeneration, now,
                out result);
        }
        if (!entry.Admits(attackerCharacterId, _campOf))
        {
            result = null!;
            return false;
        }
        return entry.Runtime.TryApplyControl(objectId, attackerCharacterId, definition, expectedSpawnGeneration,
            now, out result);
    }

    public void ClearAggroForCharacter(int characterId, DateTimeOffset now)
    {
        _base.ClearAggroForCharacter(characterId, now);
        foreach (var entry in _entries.Values)
        {
            entry.Runtime.ClearAggroForCharacter(characterId, now);
        }
    }

    public void ClearAggroForCharacterStateOnly(int characterId, DateTimeOffset now)
    {
        _base.ClearAggroForCharacterStateOnly(characterId, now);
        foreach (var entry in _entries.Values)
        {
            entry.Runtime.ClearAggroForCharacterStateOnly(characterId, now);
        }
    }

    public MonsterRuntimeTick Advance(DateTimeOffset now, IReadOnlyList<MonsterCombatTarget>? combatTargets = null)
    {
        var positionsChanged = _publicationPending;
        _publicationPending = false;

        // A corpse retired on the previous tick has already had its Despawned update delivered.
        foreach (var retiring in _entries.Values.Where(static entry => entry.Retiring).ToArray())
        {
            Remove(retiring.ObjectId, now);
            positionsChanged = true;
            Console.WriteLine($"[runtime-spawn] retired map={MapId} object={retiring.ObjectId} " +
                $"source={retiring.Spec.Source} reason=corpse");
        }
        _publicationPending = false;

        var baseTick = _base.Advance(now, combatTargets);
        if (_entries.Count == 0)
        {
            return positionsChanged ? new(true, baseTick.Updates) : baseTick;
        }

        positionsChanged |= baseTick.PositionsChanged;
        var updates = new List<MonsterRuntimeUpdate>(baseTick.Updates);
        foreach (var entry in _entries.Values.ToArray())
        {
            var alive = entry.Runtime.TryGetSnapshot(entry.ObjectId, out var before) && before.IsAlive;
            if (now >= entry.ExpiresAt && alive || now >= entry.ExpiresAt + CorpseGrace)
            {
                // Lifetime over: the snapshot no longer lists it, so every viewer gets an ordinary removal.
                Remove(entry.ObjectId, now);
                positionsChanged = true;
                Console.WriteLine($"[runtime-spawn] despawned map={MapId} object={entry.ObjectId} " +
                    $"source={entry.Spec.Source} reason=timeout");
                continue;
            }

            var targets = entry.Spec.OwnerOnly && combatTargets is not null
                ? combatTargets.Where(target => target.CharacterId == entry.Spec.OwnerCharacterId).ToArray()
                : combatTargets;
            var tick = entry.Runtime.Advance(now, targets);
            positionsChanged |= tick.PositionsChanged;
            updates.AddRange(tick.Updates);
            if (entry.Runtime.TryGetSnapshot(entry.ObjectId, out var after) && !after.IsAlive && !after.IsSpawned)
            {
                entry.Retiring = true;
            }
        }
        _publicationPending = false;
        return new(positionsChanged, updates);
    }

    private sealed class Entry(
        IMonsterMapRuntime runtime,
        uint objectId,
        Guid runtimeInstanceId,
        WorldRuntimeSpawnSpec spec,
        DateTimeOffset expiresAt)
    {
        public IMonsterMapRuntime Runtime { get; } = runtime;
        public uint ObjectId { get; } = objectId;
        public Guid RuntimeInstanceId { get; } = runtimeInstanceId;
        public WorldRuntimeSpawnSpec Spec { get; } = spec;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Retiring { get; set; }

        /// <summary>Owner-only spawns take hits from their owner only; unattributed damage is refused too.
        /// ROUND3 summons: an allied (camp) spawn takes no hits from its owner or its own faction.</summary>
        public bool Admits(int? characterId, Func<int, int?>? campOf = null)
        {
            if (Spec.OwnerOnly)
            {
                return characterId == Spec.OwnerCharacterId;
            }
            if (Spec.Camp is { } camp && characterId is { } attacker)
            {
                return attacker != Spec.OwnerCharacterId && campOf?.Invoke(attacker) != camp;
            }
            return true;
        }
    }
}
