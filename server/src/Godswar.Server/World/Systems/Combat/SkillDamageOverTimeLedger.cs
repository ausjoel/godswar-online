using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Target-owned skill damage-over-time statuses (Bleeding, Electrocuted, Burned) for one
/// monster. Callers hold the owning PveMonsterElementalState gate. One entry per family:
/// a stronger or equal-priority hit replaces the running one (stock refresh), a weaker one
/// is ignored. Ticks come out as periodic-damage intents so the existing monster Burn
/// pump applies, publishes and credits them exactly like an elemental Burn tick.
/// </summary>
internal sealed class SkillDamageOverTimeLedger
{
    // Tick ordinals start here so a skill tick never shares (event, ordinal) with an
    // elemental Burn tick from the same hit (those stop at 32).
    internal const int TickOrdinalBase = 100;

    private readonly long _ownerObjectId;
    private readonly Dictionary<SkillDamageOverTimeFamily, Entry> _active = [];

    public SkillDamageOverTimeLedger(long ownerObjectId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerObjectId);
        _ownerObjectId = ownerObjectId;
    }

    public int ActiveCount => _active.Count;

    public bool TryApply(
        SkillDamageOverTimeDefinition definition,
        long sourceCharacterId,
        ulong sourceEventId,
        long appliedAtMilliseconds,
        int tickDamage)
    {
        if (sourceCharacterId is <= 0 or > int.MaxValue || sourceEventId == 0 ||
            appliedAtMilliseconds < 0 || tickDamage <= 0 ||
            definition.TickCount is <= 0 or > 32 || definition.IntervalMilliseconds <= 0)
        {
            return false;
        }

        if (_active.TryGetValue(definition.Family, out var current) &&
            current.EmittedTicks < current.Definition.TickCount &&
            current.Definition.Priority > definition.Priority)
        {
            return false;
        }

        _active[definition.Family] = new Entry(definition, sourceCharacterId, sourceEventId,
            appliedAtMilliseconds, tickDamage);
        return true;
    }

    public IReadOnlyList<ElementalPeriodicDamageIntent> CollectDue(long nowMilliseconds)
    {
        if (_active.Count == 0)
        {
            return [];
        }

        List<ElementalPeriodicDamageIntent>? intents = null;
        List<SkillDamageOverTimeFamily>? finished = null;
        foreach (var (family, entry) in _active)
        {
            while (entry.EmittedTicks < entry.Definition.TickCount)
            {
                var ordinal = entry.EmittedTicks + 1;
                var dueAt = entry.AppliedAtMilliseconds +
                    (long)entry.Definition.IntervalMilliseconds * ordinal;
                if (dueAt > nowMilliseconds)
                {
                    break;
                }

                entry.EmittedTicks = ordinal;
                (intents ??= []).Add(new ElementalPeriodicDamageIntent(
                    default,
                    ElementalEffectKind.Burn,
                    entry.SourceCharacterId,
                    _ownerObjectId,
                    entry.SourceEventId,
                    TickOrdinalBase + ordinal,
                    entry.TickDamage,
                    CombatEventProvenance.ElementalStatus));
            }

            if (entry.EmittedTicks >= entry.Definition.TickCount)
            {
                (finished ??= []).Add(family);
            }
        }

        foreach (var family in finished ?? [])
        {
            _active.Remove(family);
        }

        return intents is null ? [] : intents.AsReadOnly();
    }

    public void Clear() => _active.Clear();

    private sealed class Entry(
        SkillDamageOverTimeDefinition definition,
        long sourceCharacterId,
        ulong sourceEventId,
        long appliedAtMilliseconds,
        int tickDamage)
    {
        public SkillDamageOverTimeDefinition Definition { get; } = definition;
        public long SourceCharacterId { get; } = sourceCharacterId;
        public ulong SourceEventId { get; } = sourceEventId;
        public long AppliedAtMilliseconds { get; } = appliedAtMilliseconds;
        public int TickDamage { get; } = tickDamage;
        public int EmittedTicks { get; set; }
    }
}
