using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct RuntimeIncomingDamageMitigation(
    decimal PhysicalDamageReduction,
    decimal MagicDamageReduction,
    int PhysicalDefenseBonus,
    int MagicDefenseBonus,
    ClientStatusAggregate StatusAggregate = default,
    int PhysicalDamageTakenIncreaseBasisPoints = 0,
    int MagicDamageTakenIncreaseBasisPoints = 0);

internal sealed partial class GameSessionRegistry
{
    internal bool TryGetRuntimeIncomingDamageMitigation(
        ClientSession session,
        DateTimeOffset now,
        out RuntimeIncomingDamageMitigation mitigation)
    {
        mitigation = default;
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            RuntimeStatusSessionLookupHook?.Invoke();
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session, out var context))
                {
                    return false;
                }

                mitigation = ComposeIncomingMitigation(
                    [],
                    ClientStatusAggregate.Empty,
                    CaptureTrainingDummyHostileIncomingModifiersLocked(
                        context,
                        now));
                return true;
            }
        }

        state.Gate.Wait();
        try
        {
            IEnumerable<ActiveRuntimeStatus> statuses;
            ClientStatusAggregate statusAggregate;
            var hostile = default(TrainingDummyHostileIncomingModifiers);
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                RuntimeStatusSessionLookupHook?.Invoke();
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return false;
                    }

                    var decision = EvaluatePlayerStatusEcsLocked(
                        session,
                        state,
                        context,
                        now);
                    statuses = decision.ActiveRuntimeStatuses;
                    statusAggregate = decision.Snapshot.Aggregate;
                    hostile =
                        CaptureTrainingDummyHostileIncomingModifiersLocked(
                            context,
                            now);
                }
            }
            else
            {
                var activeStatuses = state.RuntimeStatuses.Values
                    .Where(status => status.ExpiresAt > now)
                    .ToArray();
                statuses = activeStatuses;
                statusAggregate = PlayerStatusComposer.Compose(
                        ExperienceBoostState.Empty,
                        activeStatuses,
                        now)
                    .Aggregate;
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(session, out var context))
                    {
                        return false;
                    }

                    hostile =
                        CaptureTrainingDummyHostileIncomingModifiersLocked(
                            context,
                            now);
                }
            }

            var active = statuses.ToArray();
            mitigation = ComposeIncomingMitigation(
                active,
                statusAggregate,
                hostile);
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal bool TryPreviewRuntimeIncomingDamageMitigation(
        ClientSession session,
        DateTimeOffset now,
        out RuntimeIncomingDamageMitigation mitigation)
    {
        mitigation = default;
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session, out var context))
                {
                    return false;
                }

                mitigation = ComposeIncomingMitigation(
                    [],
                    ClientStatusAggregate.Empty,
                    PreviewTrainingDummyHostileIncomingModifiersLocked(
                        context,
                        now));
                return true;
            }
        }

        state.Gate.Wait();
        try
        {
            var active = state.RuntimeStatuses.Values
                .Where(status => status.ExpiresAt > now)
                .ToArray();
            var aggregate = PlayerStatusComposer.Compose(
                    state.ExperienceBoosts,
                    active,
                    now)
                .Aggregate;
            RuntimeStatusSessionLookupHook?.Invoke();
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session, out var context))
                {
                    return false;
                }

                mitigation = ComposeIncomingMitigation(
                    active,
                    aggregate,
                    PreviewTrainingDummyHostileIncomingModifiersLocked(
                        context,
                        now));
                return true;
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static int SumDefense(
        IEnumerable<ActiveRuntimeStatus> statuses,
        Func<ActiveRuntimeStatus, int> selector) =>
        (int)Math.Clamp(
            statuses.Aggregate(
                0L,
                (sum, status) => sum + selector(status)),
            int.MinValue,
            int.MaxValue);

    private static RuntimeIncomingDamageMitigation
        ComposeIncomingMitigation(
            IReadOnlyCollection<ActiveRuntimeStatus> statuses,
            in ClientStatusAggregate statusAggregate,
            in TrainingDummyHostileIncomingModifiers hostile) =>
        new(
            Math.Clamp(
                statuses.Sum(static status =>
                    status.PhysicalDamageReduction) +
                ToFraction(
                    hostile.PhysicalDamageReductionBasisPoints),
                0m,
                1m),
            Math.Clamp(
                statuses.Sum(static status =>
                    status.MagicDamageReduction) +
                ToFraction(
                    hostile.MagicDamageReductionBasisPoints),
                0m,
                1m),
            SaturatingAdd(
                SumDefense(
                    statuses,
                    static status => status.Modifiers.PhysicalDefense),
                hostile.PhysicalDefense),
            SaturatingAdd(
                SumDefense(
                    statuses,
                    static status => status.Modifiers.MagicDefense),
                hostile.MagicDefense),
            statusAggregate,
            // ROUND3: source-native and operator runtime modifiers share the authoritative bounded pipeline.
            SumAmplification(statuses, static status => status.PhysicalDamageTakenIncreaseBasisPoints,
                hostile.PhysicalDamageTakenIncreaseBasisPoints, hostile.StatusesByKind,
                static definition => definition.PhysicalDamageTakenIncreaseBasisPoints),
            SumAmplification(statuses, static status => status.MagicDamageTakenIncreaseBasisPoints,
                hostile.MagicDamageTakenIncreaseBasisPoints, hostile.StatusesByKind,
                static definition => definition.MagicDamageTakenIncreaseBasisPoints));

    // ROUND3: saturate once after composing active sources, never turn signed mitigation into amplification twice.
    private static int SumAmplification(IReadOnlyCollection<ActiveRuntimeStatus> statuses,
        Func<ActiveRuntimeStatus, int> selector, int hostile,
        IReadOnlyDictionary<int, HostileStatusEffectDefinition>? nativeSources,
        Func<HostileStatusEffectDefinition, int> nativeSelector)
    {
        // ROUND3: compose source values before the final clamp, so replacing a source cannot subtract from an already capped sum.
        long total = nativeSources is null ? Math.Max(0, hostile) :
            nativeSources.Values.Sum(native => (long)Math.Max(0, nativeSelector(native)));
        foreach (var status in statuses)
        {
            // ROUND3: one effective modifier per kind; a native source wins an equal-priority tie.
            // Both lifetimes remain intact, so removing/expiring the operator layer reveals the native source again.
            if (nativeSources?.TryGetValue(status.Kind, out var native) == true)
            {
                if (native.Priority >= status.Priority) continue;
                total -= Math.Max(0, nativeSelector(native));
            }
            total += Math.Max(0, selector(status));
        }
        return (int)Math.Clamp(total, 0L, Application.Combat.IncomingDamageAmplificationPolicy.MaximumBasisPoints);
    }

    private static decimal ToFraction(int basisPoints) =>
        Math.Clamp(basisPoints, 0, 10_000) / 10_000m;

    private static int SaturatingAdd(int left, int right) =>
        (int)Math.Clamp((long)left + right, int.MinValue, int.MaxValue);
}
