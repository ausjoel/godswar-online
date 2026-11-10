using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // Tracking survives effect removal so the next full snapshot also clears
    // icons after an island transition, death, or terminal run state.
    private readonly ConcurrentDictionary<ClientSession, byte> _wonderlandStatusSessions = [];
    internal Action? WonderlandStatusProjectionCapturedHook { get; set; }
    internal Action? WonderlandStatusPublishedHook { get; set; }

    private PlayerStatusSnapshot MergeWonderlandStatusOverlay(GameSessionContext expected,
        PlayerStatusSnapshot baseline, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(expected.Session, out var current) ||
                current != expected || !TryGetWonderlandEffectsLocked(expected.Session, ref now, out var effects))
                return baseline;

            var presentations = new List<ClientStatusPresentation>();
            var identities = new List<string>();
            Add(WonderlandClientStatusIds.PetbirdBlessing, effects.AttackUntil, beneficial: true);
            Add(WonderlandClientStatusIds.PutridBirdBlessing, effects.HitUntil, beneficial: true);
            Add(WonderlandClientStatusIds.Stunned, effects.StunUntil, control: true);
            Add(WonderlandClientStatusIds.Silenced, effects.SilenceUntil, control: true);
            if (effects.ArmorPenaltyBasisPoints == 1500)
                Add(WonderlandClientStatusIds.InternalInjury, effects.ArmorPenaltyUntil);
            if (effects.ArmorPenaltyBasisPoints is 4000 or 5000)
                Add(effects.ArmorPenaltyBasisPoints == 5000 ? WonderlandClientStatusIds.ArmorRend :
                    WonderlandClientStatusIds.SpearBlast, effects.ArmorPenaltyUntil);
            if (presentations.Count == 0) return baseline;

            // These timers are presentation only. The encounter's authoritative
            // effects remain in WonderlandPlayerEffects, not RuntimeStatuses.
            // Every 10167 producer and its final admission fence merge this layer.
            return PlayerStatusCapacityPolicy.Apply(baseline with
            {
                Presentations = baseline.Presentations.Concat(presentations).ToArray(),
                Fingerprint = $"{baseline.Fingerprint}#wonderland:{effects.LifeRevision}:" +
                    string.Join('|', identities)
            });

            void Add(uint id, DateTimeOffset until, bool beneficial = false, bool control = false)
            {
                if (until <= now) return;
                var seconds = checked((uint)Math.Clamp((long)Math.Ceiling((until - now).TotalSeconds), 1, uint.MaxValue));
                presentations.Add(new(new ClientStatusEffect(id, seconds), beneficial, 1,
                    control ? ClientStatusPresentationClass.AuthoritativeControl :
                        ClientStatusPresentationClass.AuthoritativeBaseline));
                identities.Add($"{id}:{until.UtcTicks}");
            }
        }
    }

    internal async Task<int> ReconcileWonderlandStatusesOnceAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var published = 0;
        foreach (var session in _wonderlandStatusSessions.Keys)
        {
            if (!TryGetOrCreatePlayerStatusState(session, out var state))
            {
                _wonderlandStatusSessions.TryRemove(session, out _);
                continue;
            }
            var claims = new ExactStatusDisconnectClaims();
            var disconnected = false;
            // Never acquire the status gate inside an encounter/vitals lock.
            // Combat only marks the session; the world pump publishes afterward.
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                bool forceClear;
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(session, out var current) ||
                        !IsCurrentAccountSession(current.AccountId, session, current.Ownership))
                    {
                        _wonderlandStatusSessions.TryRemove(session, out _);
                        continue;
                    }
                    forceClear = !HasActiveWonderlandStatusLocked(session, now);
                }
                // Self/viewer queries also expose this layer but do not update
                // LastFingerprint. An inactive tracked effect therefore needs
                // one admitted clear even when that cache already says baseline.
                var sent = await PublishStatusSnapshotLockedAsync(session, state, now, "wonderland-effects",
                    force: forceClear, broadcast: true, cancellationToken, claimedDisconnects: claims);
                if (sent)
                {
                    published++;
                    WonderlandStatusPublishedHook?.Invoke();
                }
                lock (_gate)
                    if (sent && !HasActiveWonderlandStatusLocked(session, now) &&
                        state.LastFingerprint?.Contains("#wonderland:", StringComparison.Ordinal) != true)
                        _wonderlandStatusSessions.TryRemove(session, out _);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                disconnected = true;
            }
            finally
            {
                state.Gate.Release();
                claims.CompleteAll(this);
            }
            if (disconnected) Remove(session);
        }
        return published;
    }

    private bool HasActiveWonderlandStatusLocked(ClientSession session, DateTimeOffset now) =>
        TryGetWonderlandEffectsLocked(session, ref now, out var effects) &&
        (effects.AttackUntil > now || effects.HitUntil > now || effects.StunUntil > now ||
         effects.SilenceUntil > now || effects.ArmorPenaltyUntil > now);
}
