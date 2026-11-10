using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.State; // ROUND3

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<
        ClientSession,
        Func<
            SkillCastInterruptionReason,
            CancellationToken,
            Task?,
            Task>> _skillCastInterruptionSinks = [];
    private readonly ConcurrentDictionary<
        ClientSession,
        Func<
            SkillCastInterruptionReason,
            PreparedSkillCastInterruption?>>
        _preparedSkillCastInterruptionSinks = [];

    public void RegisterSkillCastInterruptionSink(
        ClientSession session,
        Func<
            SkillCastInterruptionReason,
            CancellationToken,
            Task?,
            Task> sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_skillCastInterruptionSinks.TryAdd(session, sink))
        {
            throw new InvalidOperationException(
                "A skill-cast interruption sink is already registered " +
                "for this session.");
        }
    }

    public void UnregisterSkillCastInterruptionSink(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _skillCastInterruptionSinks.TryRemove(session, out _);
    }

    internal void RegisterPreparedSkillCastInterruptionSink(
        ClientSession session,
        Func<
            SkillCastInterruptionReason,
            PreparedSkillCastInterruption?> sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_preparedSkillCastInterruptionSinks.TryAdd(session, sink))
        {
            throw new InvalidOperationException(
                "A prepared skill-cast interruption sink is already " +
                "registered for this session.");
        }
    }

    internal void UnregisterPreparedSkillCastInterruptionSink(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _preparedSkillCastInterruptionSinks.TryRemove(session, out _);
    }

    public Task RequestSkillCastInterruptionAsync(
        ClientSession session,
        SkillCastInterruptionReason reason,
        CancellationToken cancellationToken,
        Task? notificationBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _skillCastInterruptionSinks.TryGetValue(
            session,
            out var sink)
            ? sink(
                reason,
                cancellationToken,
                notificationBarrier)
            : Task.CompletedTask;
    }

    public PlayerSkillCastControl GetPlayerSkillCastControl(
        ClientSession session,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        var wonderland = GetWonderlandPlayerControl(session, now);
        if (wonderland == PlayerSkillCastControl.Stunned)
        {
            return wonderland;
        }

        if (HasActiveElementalShock(session, now))
        {
            return PlayerSkillCastControl.Stunned;
        }

        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return wonderland;
        }

        var resolved = wonderland;
        foreach (var status in Volatile.Read(
                     ref state.SkillCastControlStatuses))
        {
            if (status.ExpiresAt <= now)
            {
                continue;
            }

            var candidate =
                PlayerSkillCastControlCatalog.ResolveActiveBlock(
                    status.StatusId);
            // ROUND3: generic status controls must not depend on a hard-coded status-ID list.
            if ((status.Modifiers.Control & (HostileStatusControlFlags.NonMagicUsing |
                    HostileStatusControlFlags.NonTechniqueUsing)) != 0)
                candidate = (status.Modifiers.Control & HostileStatusControlFlags.NonMoving) != 0
                    ? PlayerSkillCastControl.Stunned : PlayerSkillCastControl.Silenced;
            if (candidate == PlayerSkillCastControl.Stunned)
            {
                return candidate;
            }

            if (candidate == PlayerSkillCastControl.Silenced)
            {
                resolved = candidate;
            }
        }

        return resolved;
    }

    public bool IsPlayerStatusMovementAllowed(
        ClientSession session,
        DateTimeOffset now)
    {
        if (GetPlayerSkillCastControl(session, now) ==
            PlayerSkillCastControl.Stunned)
        {
            return false;
        }

        // Read the immutable control snapshot without taking the status
        // semaphore. Status publication can await a pending cast while a
        // movement handler is finishing its interruption.
        return !_playerStatusStates.TryGetValue(session, out var state) ||
            !Volatile.Read(ref state.SkillCastControlStatuses).Any(status =>
                status.ExpiresAt > now &&
                (PlayerSkillCastControlCatalog.BlocksMovement(status.StatusId) ||
                 (status.Modifiers.Control & HostileStatusControlFlags.NonMoving) != 0));
    }

    private bool HasActiveElementalShock(
        ClientSession session,
        DateTimeOffset now)
    {
        ElementalCombatSessionFence fence;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !context.WorldReady ||
                !context.Ownership.IsValid)
            {
                return false;
            }

            fence = new(
                context.CharacterId,
                context.MapId,
                context.Ownership);
        }

        return TryGetElementalStatusAdjustment(
                session,
                fence,
                now.ToUnixTimeMilliseconds(),
                movementSpeed: 0,
                physicalDefense: 0,
                magicDefense: 0,
                hitRating: 0,
                healingReceived: 0,
                out var adjustment) &&
            !adjustment.MovementAllowed;
    }

    // ROUND3: lock-free snapshots avoid taking the status gate inside handler action checks.
    internal HostileStatusControlFlags GetRuntimeControlFlags(ClientSession session, DateTimeOffset now) =>
        !_playerStatusStates.TryGetValue(session, out var state) ? HostileStatusControlFlags.None :
        Volatile.Read(ref state.SkillCastControlStatuses).Where(status => status.ExpiresAt > now)
            .Aggregate(HostileStatusControlFlags.None, (flags, status) => flags | status.Modifiers.Control);

    private static void RefreshSkillCastControlSnapshot(
        PlayerStatusState state)
    {
        Volatile.Write(
            ref state.SkillCastControlStatuses,
            state.RuntimeStatuses.Values
                .Where(status =>
                    status.Modifiers.Control != HostileStatusControlFlags.None || // ROUND3
                    PlayerSkillCastControlCatalog.ResolveActiveBlock(
                        status.StatusId) !=
                    PlayerSkillCastControl.None ||
                    PlayerSkillCastControlCatalog.BlocksMovement(
                        status.StatusId))
                .ToArray());
    }
}
