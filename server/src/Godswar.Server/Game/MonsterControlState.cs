using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>Immutable, generation-owned controls. Expiry never rewinds interruption fences.</summary>
internal sealed record MonsterControlEntry(HostileStatusEffectDefinition Definition, DateTimeOffset ExpiresAt);

internal sealed class MonsterControlState : IEquatable<MonsterControlState>
{
    public static MonsterControlState Empty { get; } = new([], 0, 0);
    private readonly MonsterControlEntry[] _entries;
    private MonsterControlState(MonsterControlEntry[] entries, long attacks, long casts)
    { _entries = entries; AttackInterruptionRevision = attacks; CastInterruptionRevision = casts; }
    public long AttackInterruptionRevision { get; }
    public long CastInterruptionRevision { get; }
    public IEnumerable<MonsterControlEntry> Active(DateTimeOffset now) => _entries.Where(e => e.ExpiresAt > now);
    public HostileStatusControlFlags At(DateTimeOffset now) => Active(now)
        .Aggregate(HostileStatusControlFlags.None, (flags, e) => flags | e.Definition.Control);

    public MonsterControlState InterruptedByStun() => new(_entries,
        checked(AttackInterruptionRevision + 1), checked(CastInterruptionRevision + 1));
    public MonsterControlState Cleared() => new([], checked(AttackInterruptionRevision + 1),
        checked(CastInterruptionRevision + 1));
    public bool Equals(MonsterControlState? other) => other is not null &&
        AttackInterruptionRevision == other.AttackInterruptionRevision &&
        CastInterruptionRevision == other.CastInterruptionRevision && _entries.SequenceEqual(other._entries);
    public override bool Equals(object? other) => other is MonsterControlState state && Equals(state);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(AttackInterruptionRevision); hash.Add(CastInterruptionRevision);
        foreach (var entry in _entries) hash.Add(entry);
        return hash.ToHashCode();
    }

    public bool TryApply(HostileStatusEffectDefinition definition, DateTimeOffset now,
        out MonsterControlState next, out DateTimeOffset expiresAt)
    {
        definition.Validate();
        expiresAt = now + definition.Duration;
        var active = Active(now).ToArray();
        var previous = active.FirstOrDefault(e => e.Definition.Kind == definition.Kind);
        if (previous is not null && previous.Definition.Priority > definition.Priority)
        { next = this; return false; }
        if (previous is not null && previous.Definition.Priority == definition.Priority && previous.ExpiresAt > expiresAt)
            expiresAt = previous.ExpiresAt;
        var until = expiresAt;
        var entries = active.Where(e => e.Definition.Kind != definition.Kind)
            .Append(new MonsterControlEntry(definition, until)).OrderBy(e => e.Definition.StatusId).ToArray();
        next = new(entries,
            checked(AttackInterruptionRevision + (definition.Control.HasFlag(HostileStatusControlFlags.NonAttackUsing) ? 1 : 0)),
            checked(CastInterruptionRevision + ((definition.Control & CastInterruptFlags) != 0 ? 1 : 0)));
        return true;
    }

    internal const HostileStatusControlFlags CastBlockFlags =
        HostileStatusControlFlags.NonMagicUsing | HostileStatusControlFlags.NonTechniqueUsing;
    internal const HostileStatusControlFlags CastInterruptFlags =
        CastBlockFlags | HostileStatusControlFlags.HaltIntonate;
    internal const HostileStatusControlFlags FullControl =
        CastInterruptFlags | HostileStatusControlFlags.NonMoving |
        HostileStatusControlFlags.NonAttackUsing | HostileStatusControlFlags.NonItemUsing;
}

internal sealed record MonsterControlResult(bool Applied, MonsterRuntimeSnapshot Monster, DateTimeOffset? ExpiresAt);

internal static class MonsterControlSkillPolicy
{
    // These exact native single-target families carry control, not damage.
    public static bool TryGet(int skillId, out HostileStatusEffectDefinition definition) =>
        TrainingDummyHostileStatusSkillCatalog.TryGet(skillId, out definition) &&
        definition.TargetMode == HostileStatusTargetMode.SingleTarget &&
        definition.Trigger == HostileStatusApplicationTrigger.CommittedCast &&
        definition.Control != HostileStatusControlFlags.None;
}
