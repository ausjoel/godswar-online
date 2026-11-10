using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// W2 Divine Intervention (Status.ini 1439, Effect 36 "Remove Debuff"): removes the player debuffs the server
    /// carries. That is every timed hostile runtime status (PvP stuns / controls, editor-applied hostile statuses)
    /// and the PvP skill damage over time (Bleeding / Electrocuted / Burned). The mount status (also non-beneficial,
    /// never expires), buffs, elemental Burn/Chill and Medusa-bound effects are left alone.
    /// </summary>
    /// <returns>How many debuffs were removed.</returns>
    internal async Task<int> CleansePlayerDebuffsAndPublishAsync(
        ClientSession session,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var removed = 0;

        if (_elementalCombatSessions.TryGetValue(session, out var elemental))
        {
            // The player Burn pump collects SkillDots under this gate, so a tick is either already
            // collected or never emitted.
            lock (elemental.Gate)
            {
                removed += elemental.SkillDots.ActiveCount;
                elemental.SkillDots.Clear();
            }
        }

        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return removed;
        }

        var claims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var kinds = state.RuntimeStatuses.Values
                .Where(status => IsCleansableDebuff(status, now))
                .Select(static status => status.Kind)
                .ToArray();
            if (kinds.Length == 0)
            {
                return removed;
            }

            foreach (var kind in kinds)
            {
                state.RuntimeStatuses.Remove(kind);
            }

            // A new revision makes every pending expiry task of a removed status a no-op.
            state.Revision = checked(state.Revision + 1);
            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(session, state, now, reason,
                force: true, broadcast: true, cancellationToken, claimedDisconnects: claims);
            return removed + kinds.Length;
        }
        finally
        {
            state.Gate.Release();
            claims.CompleteAll(this);
        }
    }

    internal static bool IsCleansableDebuff(ActiveRuntimeStatus status, DateTimeOffset now) =>
        !status.Beneficial &&
        status.Kind != MountCatalog.RuntimeStatusKind &&
        status.ExpiresAt != DateTimeOffset.MaxValue &&
        status.ExpiresAt > now;
}
