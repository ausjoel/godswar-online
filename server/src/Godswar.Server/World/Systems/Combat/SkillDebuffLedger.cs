using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Target-owned Weakness / Blinded for one monster (callers hold the PveMonsterElementalState gate).
/// One entry per kind; a stronger or equal one replaces it, an expired one is ignored on read.
/// </summary>
internal sealed class SkillDebuffLedger
{
    private readonly Dictionary<SkillHitDebuffKind, (int Amount, long ExpiresAt)> _active = [];

    public bool TryApply(SkillHitDebuffDefinition definition, long nowMilliseconds)
    {
        if (definition.Amount <= 0 || definition.DurationMilliseconds <= 0 || nowMilliseconds < 0)
        {
            return false;
        }

        if (_active.TryGetValue(definition.Kind, out var current) &&
            current.ExpiresAt > nowMilliseconds && current.Amount > definition.Amount)
        {
            return false;
        }

        _active[definition.Kind] = (definition.Amount, nowMilliseconds + definition.DurationMilliseconds);
        return true;
    }

    public int Amount(SkillHitDebuffKind kind, long nowMilliseconds) =>
        _active.TryGetValue(kind, out var value) && value.ExpiresAt > nowMilliseconds ? value.Amount : 0;

    public void Clear() => _active.Clear();
}
