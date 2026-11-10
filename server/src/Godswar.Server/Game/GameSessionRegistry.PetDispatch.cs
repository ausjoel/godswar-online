using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;

namespace Godswar.Server.Game;

/// <summary>W3 Magic Bell: ask the handler of each world-ready character with a dispatch whose time is up to finish it.</summary>
internal sealed record PetDispatchDueAction(string ActorName) : OperatorSessionAction(ActorName);

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Every 15 s (the notice ticker). One read for all world-ready characters; the payout itself runs on the player's
    /// own handler under its character gate, which claims each row once. Offline characters finish after their next
    /// world entry, at the first tick.
    /// </summary>
    internal async Task FinishDuePetDispatchesAsync(CancellationToken cancellationToken)
    {
        if (!Finalise2Rates.On(Finalise2RateKeys.MagicBell) || EventsNpcRuntime.Store is not { } store) return;
        var ready = SnapshotWorldReadySessions();
        if (ready.Length == 0) return;
        string? due;
        try
        {
            due = await store.ExecuteScalarAsync(
                "SELECT string_agg(DISTINCT character_id::text, ',') FROM public.pet_dispatch_state " +
                "WHERE due_at <= now() AND character_id = ANY(@ids);",
                cancellationToken, ("ids", ready.Select(static c => c.CharacterId).Distinct().ToArray())) as string;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[pet-dispatch] due scan skipped: {error.GetType().Name}");
            return;
        }

        var characters = (due ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(static id => int.TryParse(id, out var value) ? value : 0).ToHashSet();
        foreach (var context in ready.Where(c => characters.Contains(c.CharacterId)).DistinctBy(static c => c.CharacterId))
        {
            try
            {
                var result = await RunOperatorActionAsync(context.Session, new PetDispatchDueAction("pet-dispatch"), cancellationToken);
                Console.WriteLine($"[pet-dispatch] due character={context.CharacterId} {result}");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[pet-dispatch] finish skipped character={context.CharacterId}: {error.GetType().Name}");
            }
        }
    }
}
