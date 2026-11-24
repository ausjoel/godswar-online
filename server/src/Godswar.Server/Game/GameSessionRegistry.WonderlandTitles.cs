using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private IWonderlandTitleStore? _wonderlandTitles;
    private ILegacyInstanceDailyEntryClaimStore? _wonderlandTitleAdmissions;
    private readonly ConcurrentDictionary<(WorldInstanceId Instance, int Island), WonderlandTitleRequest>
        _wonderlandTitlePending = [];
    private readonly ConcurrentDictionary<(WorldInstanceId Instance, int Island), string> _wonderlandTitleSettled = [];
    private readonly ConcurrentDictionary<(WorldInstanceId Instance, int Island), byte> _wonderlandTitleInFlight = [];
    // S5b: entries that stopped holding the run (deterministic rejection, or transient for too long).
    private readonly ConcurrentDictionary<(WorldInstanceId Instance, int Island), WonderlandTitleRequest>
        _wonderlandTitleParked = [];
    private readonly SettlementRetryTracker<(WorldInstanceId Instance, int Island)> _wonderlandTitleRetries = new();

    internal void ConfigureWonderlandTitles(IWonderlandTitleStore? store,
        ILegacyInstanceDailyEntryClaimStore? admissions = null)
    {
        if (store is null) return;
        var previous = Interlocked.CompareExchange(ref _wonderlandTitles, store, null);
        if (previous is not null && !ReferenceEquals(previous, store))
            throw new InvalidOperationException("Wonderland title persistence is already configured.");
        if (admissions is null) return;
        var previousAdmissions = Interlocked.CompareExchange(ref _wonderlandTitleAdmissions, admissions, null);
        if (previousAdmissions is not null && !ReferenceEquals(previousAdmissions, admissions))
            throw new InvalidOperationException("Wonderland title admissions are already configured.");
    }

    /// <summary>Call only with evidence frozen by the authoritative island-clear mutation.</summary>
    internal bool QueueWonderlandTitleMilestone(WonderlandTitleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = (request.WorldInstanceId, request.IslandNumber);
        if (_wonderlandTitleSettled.TryGetValue(key, out var settled)) return settled == request.RequestHash;
        if (_wonderlandTitleParked.TryGetValue(key, out var parked)) return parked.RequestHash == request.RequestHash;
        return _wonderlandTitlePending.GetOrAdd(key, request).RequestHash == request.RequestHash;
    }

    internal bool HasPendingWonderlandTitles(WorldInstanceId instanceId) =>
        _wonderlandTitlePending.Keys.Any(key => key.Instance == instanceId);

    /// <summary>S5b: settled, or parked so it no longer holds the run (final exit, retirement).</summary>
    internal bool IsWonderlandTitleResolved(WorldInstanceId instanceId, int island) =>
        _wonderlandTitleSettled.ContainsKey((instanceId, island)) || _wonderlandTitleParked.ContainsKey((instanceId, island));

    internal bool IsWonderlandTitleParked(WorldInstanceId instanceId, int island) =>
        _wonderlandTitleParked.ContainsKey((instanceId, island));

    /// <summary>
    /// Runs outside the world owner lane. The caller must await settlement
    /// before retiring the instance; queued evidence alone is not crash durable.
    /// </summary>
    internal async Task RetryWonderlandTitlesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await RetryWonderlandMedalsAsync(now, cancellationToken); // ECONOMY r10
        var store = _wonderlandTitles;
        if (store is null) return;
        foreach (var entry in _wonderlandTitlePending.OrderBy(pair => pair.Value.ClearedAtUtc).ToArray())
            await TrySettleWonderlandTitleAsync(store, entry.Key, entry.Value, now, background: false, cancellationToken);
        foreach (var entry in _wonderlandTitleParked.OrderBy(pair => pair.Value.ClearedAtUtc).ToArray())
            if (_wonderlandTitleRetries.BackgroundRetryDue(entry.Key, now))
                await TrySettleWonderlandTitleAsync(store, entry.Key, entry.Value, now, background: true, cancellationToken);
    }

    private async Task TrySettleWonderlandTitleAsync(IWonderlandTitleStore store,
        (WorldInstanceId Instance, int Island) key, WonderlandTitleRequest request, DateTimeOffset now, bool background,
        CancellationToken cancellationToken)
    {
        if (!_wonderlandTitleInFlight.TryAdd(key, 0)) return;
        WonderlandTitleStatus? rejected = null;
        try
        {
            if (_wonderlandTitleSettled.TryGetValue(key, out var settled) && settled == request.RequestHash)
            {
                _wonderlandTitlePending.TryRemove(key, out _);
                return;
            }
            if (_wonderlandTitleAdmissions is { } admissions)
            {
                // S5: each admission is recorded against the reservation that admitted it. A member
                // who joined after the seal is not on the party's reservation, so recording the whole
                // roster there never matched and the island retried forever (5 Nov party run).
                await admissions.RecordAdmissionsAsync(request.AdmissionReservationId,
                    request.PartyCharacterIds, cancellationToken);
                foreach (var (characterId, reservationId) in request.LateAdmissionReservations)
                    await admissions.RecordAdmissionsAsync(reservationId, [characterId], cancellationToken);
            }
            var receipt = await store.SettleAsync(request, cancellationToken);
            if (!receipt.Succeeded)
            {
                rejected = receipt.Status;
                throw new SettlementRejectedException(receipt.Status.ToString(),
                    $"Title settlement rejected: {receipt.Status}.");
            }
            QueueWonderlandMedals(request); // ECONOMY r10: paid by its own retry loop, never blocks titles
            await PublishWonderlandTitlesAsync(request, receipt);
            MarkWonderlandCompletionNoticeSettled(request);
            _wonderlandTitlePending.TryRemove(key, out _);
            if (_wonderlandTitleParked.TryRemove(key, out _))
            {
                ParkedSettlements.MarkRecovered("wonderland-title", WonderlandTitleKey(key), now);
                Console.WriteLine($"[wonderland] parked title settlement recovered instance={key.Instance} " +
                    $"island={key.Island} attempts={_wonderlandTitleRetries.AttemptsFor(key) + 1}");
            }
            // A background recovery may land after the run was forgotten; do not leak its settled marker then.
            if (!background || _wonderlandAdmissions.ContainsKey(key.Instance))
                _wonderlandTitleSettled[key] = request.RequestHash;
            _wonderlandTitleRetries.Forget(key);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var kind = SettlementRetryPolicy.Classify(error);
            var decision = _wonderlandTitleRetries.RecordFailure(key, now, kind, out var log);
            switch (decision)
            {
                case SettlementRetryDecision.Retry:
                    if (log)
                        Console.Error.WriteLine($"[wonderland] title settlement will retry instance={key.Instance} " +
                            $"island={key.Island} kind={kind} attempt={_wonderlandTitleRetries.AttemptsFor(key)}" +
                            $"{(background ? " background" : "")}: {error.Message}");
                    break;
                case SettlementRetryDecision.Park:
                case SettlementRetryDecision.ParkAndRetryInBackground:
                    ParkWonderlandTitle(key, request, now, kind, decision, rejected, error);
                    break;
                case SettlementRetryDecision.GiveUp:
                    _wonderlandTitleParked.TryRemove(key, out _);
                    _wonderlandTitleRetries.Forget(key);
                    Console.Error.WriteLine($"[wonderland] title settlement GAVE UP instance={key.Instance} " +
                        $"island={key.Island} kind={kind}: {error.Message} (evidence was logged when it was parked)");
                    break;
            }
        }
        finally { _wonderlandTitleInFlight.TryRemove(key, out _); }
    }

    private static string WonderlandTitleKey((WorldInstanceId Instance, int Island) key) => $"{key.Instance}/island-{key.Island}";

    private void ParkWonderlandTitle((WorldInstanceId Instance, int Island) key, WonderlandTitleRequest request,
        DateTimeOffset now, SettlementFailureKind kind, SettlementRetryDecision decision, WonderlandTitleStatus? status,
        Exception error)
    {
        _wonderlandTitleParked[key] = request;
        _wonderlandTitlePending.TryRemove(key, out _);
        var background = decision == SettlementRetryDecision.ParkAndRetryInBackground;
        var medals = _wonderlandMedals is null ? 0 : WonderlandMedalPolicy.Scaled(request.IslandNumber);
        var evidence = DescribeWonderlandTitleEvidence(request, status, medals);
        var attempts = _wonderlandTitleRetries.AttemptsFor(key);
        ParkedSettlements.Record(new ParkedSettlement("wonderland-title", WonderlandTitleKey(key), kind, now, attempts,
            background, error.Message, evidence));
        Console.Error.WriteLine($"[wonderland] title settlement PARKED instance={key.Instance} island={key.Island} " +
            $"kind={kind} attempts={attempts} background-retry={(background ? "yes" : "no")}: {error.Message} | {evidence}");
    }

    internal static string DescribeWonderlandTitleEvidence(WonderlandTitleRequest request, WonderlandTitleStatus? status,
        int unpaidMedalsEach) =>
        $"status={(status?.ToString() ?? "exception")} realm={request.RealmId.Value} title={request.Award.TitleId} " +
        $"started={request.StartedAtUtc:O} cleared={request.ClearedAtUtc:O} " +
        $"party-reservation={request.AdmissionReservationId} " +
        $"reservations=[{string.Join(',', request.AdmissionReservationIds)}] " +
        $"party=[{string.Join(',', request.PartyCharacterIds)}] " +
        $"late=[{string.Join(',', request.LateAdmissionReservations.Select(pair => $"{pair.Key}:{pair.Value}"))}] " +
        $"admitted=[{string.Join(',', request.AdmittedMembers.Select(member => $"{member.CharacterId}@{member.AccountId}"))}] " +
        $"paid=[{string.Join(',', request.FrozenMembers.Select(member => $"{member.CharacterId}@{member.AccountId}"))}] " +
        $"unpaid-medals-each={unpaidMedalsEach} run-hash={request.RunHash} request-hash={request.RequestHash}";

    internal bool ForgetSettledWonderlandTitles(WorldInstanceId instanceId)
    {
        if (HasPendingWonderlandTitles(instanceId) || _wonderlandTitleInFlight.Keys.Any(key => key.Instance == instanceId))
            return false;
        foreach (var entry in _wonderlandTitleSettled.Where(pair => pair.Key.Instance == instanceId))
            _wonderlandTitleSettled.TryRemove(entry);
        // S5b: a deterministic parked entry is done (its evidence is in ParkedSettlements and the log); a
        // background one keeps retrying after the run is gone.
        foreach (var entry in _wonderlandTitleParked.Where(pair => pair.Key.Instance == instanceId &&
                     !_wonderlandTitleRetries.IsInBackground(pair.Key)))
        {
            _wonderlandTitleParked.TryRemove(entry);
            _wonderlandTitleRetries.Forget(entry.Key);
        }
        return true;
    }
}
