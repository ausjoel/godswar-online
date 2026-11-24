using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // ECONOMY r10: Wonderland island-clear Medals (WonderlandMedalPolicy). Paid to the same frozen finishers the island
    // title settlement pays, once that title has settled, from a separate pending list that the Wonderland tick retries.
    // A medal failure therefore never delays a title, the completion notice or the run's retirement; it is logged and
    // retried (the store pays each run/island/character once). Like the title queue, the pending list is in memory.
    // Rate instance.wonderland_medals = 0 turns it off.
    private IWonderlandMedalStore? _wonderlandMedals;
    private readonly ConcurrentDictionary<(WorldInstanceId Instance, int Island), WonderlandTitleRequest> _wonderlandMedalPending = [];
    // S5b: a deterministic failure (or a transient one past SettlementRetryPolicy.TransientGiveUp) is parked once
    // with its evidence instead of logging every 30 s until restart.
    private readonly SettlementRetryTracker<(WorldInstanceId Instance, int Island)> _wonderlandMedalRetries = new();

    private void QueueWonderlandMedals(WonderlandTitleRequest request)
    {
        if (_wonderlandMedals is null) return;
        _wonderlandMedalPending.TryAdd((request.WorldInstanceId, request.IslandNumber), request);
    }

    private async Task RetryWonderlandMedalsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var entry in _wonderlandMedalPending.OrderBy(pair => pair.Value.ClearedAtUtc).ToArray())
        {
            if (_wonderlandMedalRetries.IsInBackground(entry.Key) && !_wonderlandMedalRetries.BackgroundRetryDue(entry.Key, now))
                continue;
            try
            {
                await SettleWonderlandMedalsAsync(entry.Value, cancellationToken);
                _wonderlandMedalPending.TryRemove(entry);
                if (_wonderlandMedalRetries.IsInBackground(entry.Key))
                    ParkedSettlements.MarkRecovered("wonderland-medals", WonderlandTitleKey(entry.Key), now);
                _wonderlandMedalRetries.Forget(entry.Key);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var kind = SettlementRetryPolicy.Classify(error);
                var decision = _wonderlandMedalRetries.RecordFailure(entry.Key, now, kind, out var log);
                var attempts = _wonderlandMedalRetries.AttemptsFor(entry.Key);
                if (decision == SettlementRetryDecision.Retry)
                {
                    if (log)
                        Console.Error.WriteLine($"[wonderland] medals will retry instance={entry.Key.Instance} " +
                            $"island={entry.Key.Island} kind={kind} attempt={attempts} " +
                            $"characters={string.Join(',', entry.Value.CharacterIds)}: {error.Message}");
                    continue;
                }
                // Medals never hold the run, so a long transient failure keeps retrying quietly in the background
                // (parked once for the alert); a deterministic one, or one past the give-up time, stops here.
                var background = decision == SettlementRetryDecision.ParkAndRetryInBackground;
                if (!background)
                {
                    _wonderlandMedalPending.TryRemove(entry);
                    _wonderlandMedalRetries.Forget(entry.Key);
                }
                if (decision == SettlementRetryDecision.GiveUp)
                {
                    Console.Error.WriteLine($"[wonderland] medals GAVE UP instance={entry.Key.Instance} " +
                        $"island={entry.Key.Island}: {error.Message} (evidence was logged when it was parked)");
                    continue;
                }
                var evidence = DescribeWonderlandTitleEvidence(entry.Value, null,
                    WonderlandMedalPolicy.Scaled(entry.Value.IslandNumber));
                ParkedSettlements.Record(new ParkedSettlement("wonderland-medals", WonderlandTitleKey(entry.Key), kind,
                    now, attempts, background, error.Message, evidence));
                Console.Error.WriteLine($"[wonderland] medals PARKED instance={entry.Key.Instance} " +
                    $"island={entry.Key.Island} kind={kind} attempts={attempts} " +
                    $"background-retry={(background ? "yes" : "no")}: {error.Message} | {evidence}");
            }
        }
    }

    internal void ConfigureWonderlandMedals(IWonderlandMedalStore? store)
    {
        if (store is null) return;
        var previous = Interlocked.CompareExchange(ref _wonderlandMedals, store, null);
        if (previous is not null && !ReferenceEquals(previous, store))
            throw new InvalidOperationException("Wonderland medal persistence is already configured.");
    }

    private async Task SettleWonderlandMedalsAsync(WonderlandTitleRequest request, CancellationToken cancellationToken)
    {
        var store = _wonderlandMedals;
        var medals = WonderlandMedalPolicy.Scaled(request.IslandNumber);
        if (store is null || medals <= 0) return;
        var awards = await store.AwardAsync(request, medals, cancellationToken);
        lock (_gate)
        {
            foreach (var award in awards)
            {
                var current = _sessions.Values.FirstOrDefault(context => context.AccountId == award.AccountId &&
                    context.CharacterId == award.CharacterId && context.RealmId == request.RealmId &&
                    IsCurrentAccountSession(context.AccountId, context.Session, context.Ownership));
                // The Point Exchanger reads the balance from memory when it opens, so the in-memory value follows
                // the database. A later shop purchase re-reads the row under lock, so this can never overspend.
                if (current is not null) current.Character.ExchangeMedal = award.MedalsAfter;
            }
        }

        foreach (var award in awards.Where(award => award.NewlyAwarded))
            Console.WriteLine($"[wonderland] medals island={request.IslandNumber} character={award.CharacterId} " +
                $"+{award.Medals} balance={award.MedalsAfter}");
    }
}
