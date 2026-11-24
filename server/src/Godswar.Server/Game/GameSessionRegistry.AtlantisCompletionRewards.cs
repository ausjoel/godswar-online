using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private IAtlantisCompletionRewardStore? _atlantisCompletionRewards;
    private ILegacyInstanceDailyEntryClaimStore? _atlantisCompletionAdmissions;
    private readonly ConcurrentDictionary<Guid, WorldInstanceId> _atlantisAdmissionInstances = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _atlantisCompletionRewardSettled = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _atlantisCompletionRewardInFlight = [];
    // S5b: bounded retries. A deterministic rejection is parked (logged once, alerted) and the run treated as settled
    // so it can retire; a transient failure past the hold limit is parked too but keeps retrying in the background.
    private readonly SettlementRetryTracker<WorldInstanceId> _atlantisRewardRetries = new();
    private readonly ConcurrentDictionary<WorldInstanceId, AtlantisCompletionRewardRequest> _atlantisRewardBackground = [];

    internal void ConfigureAtlantisCompletionRewards(IAtlantisCompletionRewardStore? store,
        ILegacyInstanceDailyEntryClaimStore? admissionStore = null)
    {
        if (store is null) return;
        var current = Interlocked.CompareExchange(ref _atlantisCompletionRewards, store, null);
        if (current is not null && !ReferenceEquals(current, store))
            throw new InvalidOperationException("Atlantis completion rewards are already configured.");
        if (admissionStore is not null)
        {
            var admissions = Interlocked.CompareExchange(ref _atlantisCompletionAdmissions, admissionStore, null);
            if (admissions is not null && !ReferenceEquals(admissions, admissionStore))
                throw new InvalidOperationException("Atlantis completion admissions are already configured.");
        }
    }

    internal void RecordAtlantisRewardAdmissions(Guid reservationId, IReadOnlyCollection<int> characterIds)
    {
        if (_atlantisAdmissionInstances.TryGetValue(reservationId, out var instanceId) &&
            WorldInstances.TryFind(instanceId, out var runtime))
        {
            InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                map.RecordAtlantisRewardAdmissions(reservationId, characterIds);
                return true;
            });
        }
    }

    private void ForgetAtlantisCompletionRewards(WorldInstanceId instanceId)
    {
        foreach (var entry in _atlantisAdmissionInstances.Where(pair => pair.Value == instanceId))
            _atlantisAdmissionInstances.TryRemove(entry);
        _atlantisCompletionRewardSettled.TryRemove(instanceId, out _);
        _atlantisCompletionRewardInFlight.TryRemove(instanceId, out _);
        // A background retry (parked after a long transient failure) outlives the runtime on purpose.
        if (!_atlantisRewardBackground.ContainsKey(instanceId)) _atlantisRewardRetries.Forget(instanceId);
    }

    internal bool IsAtlantisCompletionRewardParked(WorldInstanceId instanceId) =>
        ParkedSettlements.Snapshot().Any(entry => entry.Kind == "atlantis-completion" && entry.Key == instanceId.ToString());

    private async Task<bool> SettleAtlantisCompletionRewardsAsync(AtlantisRunDelivery delivery,
        CancellationToken cancellationToken)
    {
        var instanceId = delivery.Runtime.InstanceId;
        var store = _atlantisCompletionRewards;
        if (store is null || _atlantisCompletionRewardSettled.ContainsKey(instanceId)) return true;
        if (!_atlantisCompletionRewardInFlight.TryAdd(instanceId, 0)) return false;
        AtlantisCompletionRewardRequest? request = null;
        AtlantisCompletionRewardStatus? rejected = null;
        try
        {
            var evidence = InvokeWorldOwner(delivery.Runtime, map => map.GetAtlantisCompletionEvidence());
            if (evidence is null)
                throw new InvalidOperationException("The completed run has no frozen admission evidence.");
            if (evidence.Request is null)
            {
                // A delayed committed kill can finish after every member left.
                // Nobody present at completion means no entitlement or notice.
                _atlantisCompletionRewardSettled.TryAdd(instanceId, 0);
                return true;
            }
            if (_atlantisCompletionAdmissions is { } admissions)
            {
                // Admission writes may have failed after an actual transfer.
                // Repair only this original world owner's recorded entrants;
                // the durable reward store still rejects unadmitted claims.
                await admissions.RecordAdmissionsAsync(evidence.Request.AdmissionReservationId,
                    evidence.Request.AdmittedCharacterIds, cancellationToken);
            }
            request = evidence.Request;
            var receipt = await store.SettleAsync(evidence.Request, cancellationToken);
            if (!receipt.Succeeded)
            {
                rejected = receipt.Status;
                throw new SettlementRejectedException(receipt.Status.ToString(),
                    $"Durable completion rejected: {receipt.Status}.");
            }
            await PublishAtlantisCompletionRewardsAsync(evidence, receipt, cancellationToken);
            _atlantisCompletionRewardSettled.TryAdd(instanceId, 0);
            _atlantisRewardRetries.Forget(instanceId);
            Console.WriteLine($"[atlantis] completion reward instance={instanceId} status={receipt.Status} " +
                $"members={receipt.Members.Count} hard-points={receipt.Award.HardPoints} title={receipt.Award.TitleName}");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            var kind = SettlementRetryPolicy.Classify(error);
            var decision = _atlantisRewardRetries.RecordFailure(instanceId, delivery.ObservedAt, kind, out var log);
            var attempts = _atlantisRewardRetries.AttemptsFor(instanceId);
            if (decision == SettlementRetryDecision.Retry)
            {
                if (log)
                    Console.Error.WriteLine($"[atlantis] completion reward will retry instance={instanceId} " +
                        $"kind={kind} attempt={attempts}: {error.Message}");
                return false;
            }
            // Parked: the run is released (members leave, the runtime retires). The evidence is logged once.
            _atlantisCompletionRewardSettled.TryAdd(instanceId, 0);
            var background = decision == SettlementRetryDecision.ParkAndRetryInBackground && request is not null;
            if (background) _atlantisRewardBackground[instanceId] = request!;
            var evidence = DescribeAtlantisCompletionEvidence(request, rejected);
            ParkedSettlements.Record(new ParkedSettlement("atlantis-completion", instanceId.ToString(), kind,
                delivery.ObservedAt, attempts, background, error.Message, evidence));
            Console.Error.WriteLine($"[atlantis] completion reward PARKED instance={instanceId} kind={kind} " +
                $"attempts={attempts} background-retry={(background ? "yes" : "no")}: {error.Message} | {evidence}");
            if (!background) _atlantisRewardRetries.Forget(instanceId);
            return true;
        }
        finally
        {
            _atlantisCompletionRewardInFlight.TryRemove(instanceId, out _);
        }
    }

    /// <summary>
    /// S5b: background retries of Atlantis completions parked after a long transient failure. They run from the
    /// frozen request alone (the runtime may be gone) and only write the durable rows; members who are still online
    /// see the new honour/title on their next login.
    /// </summary>
    internal async Task RetryParkedAtlantisCompletionRewardsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var store = _atlantisCompletionRewards;
        if (store is null) return;
        foreach (var (instanceId, request) in _atlantisRewardBackground.ToArray())
        {
            if (!_atlantisRewardRetries.BackgroundRetryDue(instanceId, now)) continue;
            try
            {
                if (_atlantisCompletionAdmissions is { } admissions)
                    await admissions.RecordAdmissionsAsync(request.AdmissionReservationId,
                        request.AdmittedCharacterIds, cancellationToken);
                var receipt = await store.SettleAsync(request, cancellationToken);
                if (!receipt.Succeeded)
                    throw new SettlementRejectedException(receipt.Status.ToString(),
                        $"Durable completion rejected: {receipt.Status}.");
                _atlantisRewardBackground.TryRemove(instanceId, out _);
                _atlantisRewardRetries.Forget(instanceId);
                ParkedSettlements.MarkRecovered("atlantis-completion", instanceId.ToString(), now);
                Console.WriteLine($"[atlantis] parked completion reward recovered instance={instanceId} " +
                    $"status={receipt.Status} members={receipt.Members.Count}");
            }
            catch (Exception error) when (error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                var kind = SettlementRetryPolicy.Classify(error);
                var decision = _atlantisRewardRetries.RecordFailure(instanceId, now, kind, out var log);
                if (decision == SettlementRetryDecision.GiveUp)
                {
                    _atlantisRewardBackground.TryRemove(instanceId, out _);
                    _atlantisRewardRetries.Forget(instanceId);
                    Console.Error.WriteLine($"[atlantis] completion reward GAVE UP instance={instanceId} kind={kind}: " +
                        $"{error.Message} (evidence was logged when it was parked)");
                }
                else if (log)
                    Console.Error.WriteLine($"[atlantis] parked completion reward will retry instance={instanceId} " +
                        $"kind={kind}: {error.Message}");
            }
        }
    }

    internal static string DescribeAtlantisCompletionEvidence(AtlantisCompletionRewardRequest? request,
        AtlantisCompletionRewardStatus? status) => request is null
        ? $"status={(status?.ToString() ?? "exception")} request=none"
        : $"status={(status?.ToString() ?? "exception")} realm={request.RealmId.Value} score={request.FinalScore} " +
          $"hard-points={request.Award.HardPoints} title={request.Award.TitleId} " +
          $"started={request.StartedAtUtc:O} completed={request.CompletedAtUtc:O} " +
          $"party-reservation={request.AdmissionReservationId} " +
          $"reservations=[{string.Join(',', request.AdmissionReservationIds)}] " +
          $"admitted=[{string.Join(',', request.AdmittedMembers.Select(member => $"{member.CharacterId}@{member.AccountId}"))}] " +
          $"paid=[{string.Join(',', request.FrozenMembers.Select(member => $"{member.CharacterId}@{member.AccountId}"))}] " +
          $"request-hash={request.RequestHash}";
}
