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
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset> _atlantisRewardLastLog = [];

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
        _atlantisRewardLastLog.TryRemove(instanceId, out _);
    }

    private async Task<bool> SettleAtlantisCompletionRewardsAsync(AtlantisRunDelivery delivery,
        CancellationToken cancellationToken)
    {
        var instanceId = delivery.Runtime.InstanceId;
        var store = _atlantisCompletionRewards;
        if (store is null || _atlantisCompletionRewardSettled.ContainsKey(instanceId)) return true;
        if (!_atlantisCompletionRewardInFlight.TryAdd(instanceId, 0)) return false;
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
            var receipt = await store.SettleAsync(evidence.Request, cancellationToken);
            if (!receipt.Succeeded)
                throw new InvalidOperationException($"Durable completion rejected: {receipt.Status}.");
            await PublishAtlantisCompletionRewardsAsync(evidence, receipt, cancellationToken);
            _atlantisCompletionRewardSettled.TryAdd(instanceId, 0);
            Console.WriteLine($"[atlantis] completion reward instance={instanceId} status={receipt.Status} " +
                $"members={receipt.Members.Count} hard-points={receipt.Award.HardPoints} title={receipt.Award.TitleName}");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            if (!_atlantisRewardLastLog.TryGetValue(instanceId, out var last) ||
                delivery.ObservedAt - last >= TimeSpan.FromSeconds(30))
            {
                _atlantisRewardLastLog[instanceId] = delivery.ObservedAt;
                Console.Error.WriteLine($"[atlantis] completion reward will retry instance={instanceId}: {error.Message}");
            }
            return false;
        }
        finally
        {
            _atlantisCompletionRewardInFlight.TryRemove(instanceId, out _);
        }
    }
}
