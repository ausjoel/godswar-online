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
    private readonly ConcurrentDictionary<(WorldInstanceId Instance, int Island), DateTimeOffset> _wonderlandTitleLastLog = [];

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
        return _wonderlandTitlePending.GetOrAdd(key, request).RequestHash == request.RequestHash;
    }

    internal bool HasPendingWonderlandTitles(WorldInstanceId instanceId) =>
        _wonderlandTitlePending.Keys.Any(key => key.Instance == instanceId);

    /// <summary>
    /// Runs outside the world owner lane. The caller must await settlement
    /// before retiring the instance; queued evidence alone is not crash durable.
    /// </summary>
    internal async Task RetryWonderlandTitlesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await RetryWonderlandMedalsAsync(now, cancellationToken); // ECONOMY r10
        var store = _wonderlandTitles;
        if (store is null) return;
        foreach (var entry in _wonderlandTitlePending.OrderBy(pair => pair.Value.ClearedAtUtc))
        {
            if (!_wonderlandTitleInFlight.TryAdd(entry.Key, 0)) continue;
            try
            {
                if (_wonderlandTitleSettled.TryGetValue(entry.Key, out var settled) && settled == entry.Value.RequestHash)
                {
                    _wonderlandTitlePending.TryRemove(entry);
                    continue;
                }
                if (_wonderlandTitleAdmissions is { } admissions)
                    await admissions.RecordAdmissionsAsync(entry.Value.AdmissionReservationId,
                        entry.Value.AdmittedCharacterIds, cancellationToken);
                var receipt = await store.SettleAsync(entry.Value, cancellationToken);
                if (!receipt.Succeeded) throw new InvalidOperationException($"Title settlement rejected: {receipt.Status}.");
                QueueWonderlandMedals(entry.Value); // ECONOMY r10: paid by its own retry loop, never blocks titles
                await PublishWonderlandTitlesAsync(entry.Value, receipt);
                MarkWonderlandCompletionNoticeSettled(entry.Value);
                _wonderlandTitleSettled[entry.Key] = entry.Value.RequestHash;
                _wonderlandTitlePending.TryRemove(entry);
                _wonderlandTitleLastLog.TryRemove(entry.Key, out _);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (!_wonderlandTitleLastLog.TryGetValue(entry.Key, out var last) || now - last >= TimeSpan.FromSeconds(30))
                {
                    _wonderlandTitleLastLog[entry.Key] = now;
                    Console.Error.WriteLine($"[wonderland] title settlement will retry instance={entry.Key.Instance} " +
                        $"island={entry.Key.Island}: {error.Message}");
                }
            }
            finally { _wonderlandTitleInFlight.TryRemove(entry.Key, out _); }
        }
    }

    internal bool ForgetSettledWonderlandTitles(WorldInstanceId instanceId)
    {
        if (HasPendingWonderlandTitles(instanceId) || _wonderlandTitleInFlight.Keys.Any(key => key.Instance == instanceId))
            return false;
        foreach (var entry in _wonderlandTitleSettled.Where(pair => pair.Key.Instance == instanceId))
            _wonderlandTitleSettled.TryRemove(entry);
        return true;
    }
}
