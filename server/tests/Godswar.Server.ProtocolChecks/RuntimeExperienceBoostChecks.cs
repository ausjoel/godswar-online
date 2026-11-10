// ROUND3: source-aware EXP must modify actual award channels and native presentation exactly once.
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class RuntimeExperienceBoostChecks
{
    public const string CheckName = "Generic runtime EXP channels compete with durable boosts and preserve source lifetimes";

    public static Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var low = Runtime(500, now);
        var high = Runtime(506, now);
        var talent = Runtime(509, now);
        var state = RuntimeExperienceBoostPolicy.Merge(ExperienceBoostState.Empty, [low, talent], now);
        Check.Equal(125, state.ApplyTo(100), "fighter gets the authored 25 percent");
        Check.Equal(200, state.ApplyToTalent(100), "talent gets only its explicit channel");
        Check.Equal(125, state.ApplyToPet(100), "pet gets the authored 25 percent");
        var snapshot = PlayerStatusComposer.Compose(ExperienceBoostState.Empty, [low, talent], now);
        Check.Equal(2, snapshot.Effects.Count, "each runtime EXP status has exactly one native icon");
        Check.True(Math.Abs(snapshot.Aggregate.ExperienceBonus - 0.25f) < 0.0001f,
            "native fighter EXP display agrees with reward calculation");

        var durable = new ExperienceBoostState([
            new ActiveExperienceBoost(505, 14, 5_000, 3, now.AddHours(1), "durable-potion"),
            new ActiveExperienceBoost(511, 22, 20_000, 1, now.AddHours(8), "weekend")]);
        state = RuntimeExperienceBoostPolicy.Merge(durable, [low], now);
        Check.Equal(350, state.ApplyTo(100), "weaker runtime rank cannot bypass durable priority");
        Check.True(!state.ActiveBoosts.Any(boost => boost.StatusId == 500), "blocked runtime status is not advertised as active");
        snapshot = PlayerStatusComposer.Compose(durable, [low], now);
        Check.Equal(2, snapshot.Effects.Count, "blocked runtime buff does not leave an extra icon");
        state = RuntimeExperienceBoostPolicy.Merge(durable, [high], now);
        Check.Equal(400, state.ApplyTo(100), "stronger runtime replaces same-kind durable grant rather than adding it twice");
        Check.Equal(400, state.ApplyToPet(100), "pet follows the same source priority");
        Check.Equal(350, durable.ApplyTo(100), "session override never mutates durable state");
        state = RuntimeExperienceBoostPolicy.Merge(durable, [high], now.AddHours(2));
        Check.Equal(300, state.ApplyTo(100), "expired runtime and durable potion restore the remaining weekend source");

        var enduring = new ExperienceBoostState([
            new ActiveExperienceBoost(508, ExperienceBoostKinds.PersistentExperiencePotion,
                10_000, 9, now.AddHours(8), "enduring-potion")]);
        state = RuntimeExperienceBoostPolicy.Merge(enduring, [Runtime(508, now)], now);
        Check.Equal(200, state.ApplyTo(100), "the exact same status cannot count twice across native and durable kinds");
        Check.Equal(ExperienceBoostKinds.PersistentExperiencePotion, state.ActiveBoosts.Single().Kind,
            "existing durable source channel is preserved");

        var curse = RuntimeExperienceBoostPolicy.Merge(ExperienceBoostState.Empty, [Runtime(1445, now)], now);
        Check.Equal(0, curse.ApplyTo(100), "negative fighter multiplier clamps to zero");
        Check.Equal(0, curse.ApplyToTalent(100), "negative talent multiplier clamps to zero");
        Check.Equal(0, curse.ApplyToPet(100), "negative pet multiplier clamps to zero");
        return Task.CompletedTask;
    }

    private static ActiveRuntimeStatus Runtime(int statusId, DateTimeOffset now)
    {
        Check.True(GenericStatusCatalog.TryResolve(statusId, out var definition, out _), "authored EXP status resolves");
        return new ActiveRuntimeStatus(definition.StatusId, definition.Kind, definition.Priority,
            definition.Beneficial, now + definition.Duration, ClientStatusAggregate.Empty, 1)
        { OperatorApplied = true, ExperienceBonuses = definition.ExperienceBonuses };
    }
}
