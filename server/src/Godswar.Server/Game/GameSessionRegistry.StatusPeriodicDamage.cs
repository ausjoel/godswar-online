// ROUND3: administrative audited HP loss; no fabricated player, PvP admission, proc or reward source.
using Godswar.Server.Application.Operators;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private bool IsRuntimeStatusSourceCurrent(ClientSession session, ActiveRuntimeStatus status) =>
        status.HpDamagePerPulse == 0 || (_playerLifeRevisions.TryGetValue(session, out var life) && status.DamageLifeRevision == life);

    private static bool IsLivingStatusDamageTarget(GameCharacter character)
    {
        lock (character.VitalsSync) return character.CurrentHp > 0;
    }

    private async Task AdvanceStatusPeriodicDamageOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (session, state) in _playerStatusStates.ToArray())
        {
            if (!Volatile.Read(ref state.PeriodicDamageActive)) continue;
            GameSessionContext? damaged = null;
            var life = -1L;
            var killed = false;
            var changed = false;
            Task interruption = Task.CompletedTask;
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                {
                    if (session.IsDisconnected || !_playerStatusStates.TryGetValue(session, out var currentState) || !ReferenceEquals(state, currentState) ||
                        !_sessions.TryGetValue(session, out var current) || !current.WorldReady ||
                        (current.Ownership.IsValid && !IsCurrentAccountSession(current.AccountId, session, current.Ownership)) ||
                        !_playerLifeRevisions.TryGetValue(session, out life)) continue;
                    foreach (var status in state.RuntimeStatuses.Values.Where(status => status.HpDamagePerPulse > 0 &&
                        (status.ExpiresAt <= now || status.DamageLifeRevision != life)).ToArray())
                    {
                        state.RuntimeStatuses.Remove(status.Kind);
                        changed = true;
                    }
                    var active = state.RuntimeStatuses.Values.Where(status => status.HpDamagePerPulse > 0).ToArray();
                    Volatile.Write(ref state.PeriodicDamageActive, active.Length > 0);
                    var due = active.Where(status => status.NextDamageAt <= now).ToArray();
                    foreach (var status in due)
                        state.RuntimeStatuses[status.Kind] = status with { NextDamageAt = now.AddSeconds(status.DamageIntervalSeconds) };
                    if (due.Length > 0 && !OperatorControls.IsInvulnerable(current.CharacterId))
                    {
                        lock (current.Character.VitalsSync)
                        {
                            if (current.Character.CurrentHp > 0)
                            {
                                var loss = Math.Min((long)current.Character.CurrentHp, due.Sum(status => (long)status.HpDamagePerPulse));
                                current.Character.CurrentHp -= checked((int)loss);
                                current.Character.MarkVitalsChanged();
                                damaged = current;
                                killed = current.Character.CurrentHp == 0;
                                if (killed)
                                {
                                    // Claim the cast and advance life synchronously before any checkpoint/socket await.
                                    interruption = RequestSkillCastInterruptionAsync(session, SkillCastInterruptionReason.Death, CancellationToken.None);
                                    life = AdvancePlayerLifeRevision(session, now);
                                    foreach (var status in state.RuntimeStatuses.Values.Where(status => status.HpDamagePerPulse > 0 ||
                                        status.Kind == MountCatalog.RuntimeStatusKind).ToArray())
                                        state.RuntimeStatuses.Remove(status.Kind);
                                    Volatile.Write(ref state.PeriodicDamageActive, false);
                                    changed = true;
                                }
                            }
                        }
                    }
                    if (changed)
                    {
                        state.Revision = checked(state.Revision + 1);
                        RefreshSkillCastControlSnapshot(state);
                    }
                }
            }
            finally { state.Gate.Release(); }

            var checkpointFailed = false;
            try
            {
                // A committed loss is never refunded/skipped because publication was cancelled.
                // Native checkpoint ownership and pending-potion settlement remain authoritative.
                if (damaged is not null) await FlushVitalsAsync(damaged.AccountId, damaged.Character, CancellationToken.None);
            }
            catch (Exception error)
            {
                // A failed/owner-lost checkpoint terminalizes only this exact recipient, never the global recovery loop.
                checkpointFailed = true;
                if (damaged is not null) FailClosedStatusDamageTarget(damaged, life);
                Console.WriteLine($"[status-damage] checkpoint refused character={damaged?.CharacterId}: {error.GetType().Name}");
            }
            finally
            {
                try { await interruption; }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                { if (damaged is not null) FailClosedStatusDamageTarget(damaged, life); }
                catch (OperationCanceledException) when (session.IsDisconnected) { }
                finally
                {
                    if (changed) await PublishStatusDamageCleanupAsync(session, state, life, now);
                }
            }
            if (!checkpointFailed && damaged is not null) await PublishStatusDamageVitalsAsync(damaged, life, killed, cancellationToken);
        }
    }

    private void FailClosedStatusDamageTarget(GameSessionContext expected, long life)
    {
        var claimed = false;
        lock (_gate)
        {
            if (IsCurrentWorldSessionSnapshot(expected.Session, expected) &&
                _playerLifeRevisions.TryGetValue(expected.Session, out var currentLife) && currentLife == life)
                claimed = expected.Session.TryClaimDisconnect();
        }
        if (claimed) CompleteClaimedExactStatusDisconnect(expected.Session);
    }

    private async Task PublishStatusDamageCleanupAsync(ClientSession session, PlayerStatusState state, long life, DateTimeOffset now)
    {
        var claims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(CancellationToken.None);
        try
        {
            lock (_gate)
            {
                if (session.IsDisconnected || !_playerStatusStates.TryGetValue(session, out var current) || !ReferenceEquals(current, state) ||
                    !_sessions.ContainsKey(session) || !_playerLifeRevisions.TryGetValue(session, out var currentLife) || currentLife != life)
                    return;
            }
            await PublishStatusSnapshotLockedAsync(session, state, now, "operator-periodic-life-cleanup",
                force: true, broadcast: true, CancellationToken.None, claimedDisconnects: claims);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { DisconnectExactStatusRecipient(session); }
        finally { state.Gate.Release(); claims.CompleteAll(this); }
    }

    private async Task PublishStatusDamageVitalsAsync(GameSessionContext target, long life, bool killed, CancellationToken cancellationToken)
    {
        if (!WorldInstances.TryFind(target.WorldInstanceId, out var runtime)) return;
        foreach (var recipient in GetWorldInstanceSessions(target.WorldInstanceId))
        {
            int health, mana;
            lock (_gate)
            {
                if (!IsCurrentWorldSessionSnapshot(target.Session, target) ||
                    !_playerLifeRevisions.TryGetValue(target.Session, out var currentLife) || currentLife != life) return;
                lock (target.Character.VitalsSync)
                {
                    health = target.Character.CurrentHp;
                    mana = target.Character.CurrentMp;
                }
            }
            var objectId = ReferenceEquals(recipient.Session, target.Session) ? LocalPlayerObjectId : target.ObjectId;
            try
            {
                await TrySendWorldInstancePacketAsync(runtime, recipient, PacketBuilder.PlayerVitalsUpdate(objectId, health, mana),
                    cancellationToken, "OperatorPeriodicHpLoss");
                if (killed && health == 0)
                    await TrySendWorldInstancePacketAsync(runtime, recipient, PacketBuilder.WonderlandPlayerDeath(objectId),
                        cancellationToken, "OperatorPeriodicDeath");
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { Remove(recipient.Session); }
        }
    }
}
