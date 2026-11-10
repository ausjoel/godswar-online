using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan StatusPeriodicInterval = TimeSpan.FromSeconds(1);

    // Status.ini Effect 26/28 with Interval=1. ROUND3: select sources and commit native recovery under
    // status -> registry -> vitals authority, so removal, life changes and replacement cannot split a pulse;
    // same vitals path as passive recovery, so pending potion commits and death handling behave the same way.
    internal async Task AdvanceStatusPeriodicRecoveryOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (session, state) in _playerStatusStates.ToArray())
        {
            if (!Volatile.Read(ref state.PeriodicRecoveryActive)) continue;
            GameSessionContext? recovered = null;
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                // ROUND3: an enumeration may outlive disconnect/rejoin; never consume an old state's effects.
                lock (_gate)
                {
                    if (!_playerStatusStates.TryGetValue(session, out var currentState) ||
                        !ReferenceEquals(state, currentState) ||
                        !_sessions.TryGetValue(session, out var current) || !current.WorldReady ||
                        (current.Ownership.IsValid && !IsCurrentAccountSession(current.AccountId, session, current.Ownership)))
                        continue;
                    var active = state.RuntimeStatuses.Values
                        .Where(status => status.ExpiresAt > now && (status.HpPerSecond > 0 || status.MpPerSecond > 0))
                        .ToArray();
                    if (active.Length == 0)
                    {
                        Volatile.Write(ref state.PeriodicRecoveryActive, false);
                        continue;
                    }
                    // ROUND3: recheck per-application deadlines after admission; concurrent pumps cannot pulse twice.
                    var due = active.Where(status => status.NextRecoveryAt <= now).ToArray();
                    if (due.Length == 0) continue;
                    foreach (var status in due)
                        state.RuntimeStatuses[status.Kind] = status with { NextRecoveryAt = now + StatusPeriodicInterval };
                    if (ApplyAuthoritativeRecoveryPulseLocked(current, now,
                        due.Sum(static status => (long)status.HpPerSecond),
                        due.Sum(static status => (long)status.MpPerSecond)).VitalsChanged)
                        recovered = current;
                }
            }
            finally
            {
                state.Gate.Release();
            }

            if (recovered is not null) await PublishRecoveredVitalsAsync(recovered, cancellationToken);
        }
    }

    /// <summary>False when an active status of the same kind outranks it, so an item is not wasted.</summary>
    internal async Task<bool> CanApplyRuntimeStatusAsync(ClientSession session, SkillStatusEffectDefinition definition,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state)) return true;
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            return !state.RuntimeStatuses.TryGetValue(definition.Kind, out var existing) ||
                existing.ExpiresAt <= now || !IsRuntimeStatusSourceCurrent(session, existing) || // ROUND3
                existing.Priority <= definition.Priority;
        }
        finally
        {
            state.Gate.Release();
        }
    }
}
