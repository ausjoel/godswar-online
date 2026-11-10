using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public long GetPlayerLifeRevision(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerLifeRevisions.TryGetValue(
            session,
            out var lifeRevision)
            ? lifeRevision
            : -1;
    }

    public bool TryGetPlayerLifeRevision(
        ClientSession session,
        out long lifeRevision)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerLifeRevisions.TryGetValue(
            session,
            out lifeRevision);
    }

    public long AdvancePlayerLifeRevision(ClientSession session) =>
        AdvancePlayerLifeRevision(
            session,
            DateTimeOffset.UtcNow);

    internal long AdvancePlayerLifeRevision(
        ClientSession session,
        DateTimeOffset advancedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.ContainsKey(session) ||
                !_playerLifeRevisions.TryGetValue(
                    session,
                    out var currentRevision))
            {
                return -1;
            }
            var nextRecoveryAt =
                advancedAt + PlayerRecoveryInterval;
            var recoveryDeadline =
                GetOrCreatePlayerRecoveryDeadlineLocked(
                    session);
            var revision = checked(currentRevision + 1);
            if (!_playerLifeRevisions.TryUpdate(
                    session,
                    revision,
                    currentRevision))
            {
                return -1;
            }
            ApplyPlayerLifeAdvanceSideEffectsLocked(
                session,
                nextRecoveryAt,
                recoveryDeadline,
                advancedAt,
                resetIncomingDamage: true);

            return revision;
        }
    }

    private void ApplyPlayerLifeAdvanceSideEffectsLocked(
        ClientSession session,
        DateTimeOffset nextRecoveryAt,
        PlayerRecoveryDeadline recoveryDeadline,
        DateTimeOffset lifeAdvancedAt,
        bool resetIncomingDamage)
    {
        if (!_sessions.TryGetValue(session, out var context))
        {
            return;
        }

        ClearBoundMedusaEffectsForExpiredLifeLocked(
            context,
            lifeAdvancedAt);
        ClearElementalCombatLifeState(session);
        ClearTrainingDummyHostileStatusesLocked(session);
        recoveryDeadline.Write(nextRecoveryAt);
        ResetPlayerRecoveryEcs(session);
        if (resetIncomingDamage)
        {
            ResetPlayerVitalsDamageEcs(session);
        }
    }

    private PlayerRecoveryDeadline
        GetOrCreatePlayerRecoveryDeadlineLocked(
            ClientSession session)
    {
        if (!_sessions.TryGetValue(session, out var context))
        {
            throw new InvalidOperationException(
                "Player recovery requires a joined session.");
        }

        return _nextPlayerRecoveryAt.GetOrAdd(
            context.CharacterId,
            static _ => new PlayerRecoveryDeadline(
                DateTimeOffset.UnixEpoch));
    }
}
