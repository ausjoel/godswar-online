using System.Collections.Concurrent;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W1 prize claims: every 15 s (the notice ticker) each eligible world-ready character earns 15 s of beginner-gift
/// online time, and each connection gets one 10323 status (outside the login and map-change sequences). A character
/// that has claimed every gift gets no status, so its window stays empty.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<ClientSession, byte> _timePrizeAnnounced = new();

    internal async Task AccrueTimePrizesAsync(CancellationToken cancellationToken)
    {
        foreach (var gone in _timePrizeAnnounced.Keys.Where(static s => s.IsDisconnected).ToArray())
            _timePrizeAnnounced.TryRemove(gone, out _);
        if (!Finalise2Rates.On(Finalise2RateKeys.BeginnerGifts) || EventsNpcRuntime.Store is not { } store) return;
        var maxLevel = Finalise2Rates.Amount(Finalise2RateKeys.BeginnerGiftsMaxLevel);
        var tiers = TimePrizeCatalog.BeginnerTiers.Count;
        var eligible = SnapshotWorldReadySessions().Where(c => c.Character.Level <= maxLevel).ToArray();
        if (eligible.Length == 0) return;
        // One statement for every eligible world-ready character, no character_base row locks (advisor 2026-11-08).
        string? rows;
        try
        {
            rows = await store.ExecuteScalarAsync("""
                WITH up AS (
                    INSERT INTO public.time_prize_state (character_id, online_seconds)
                    SELECT id, @s FROM unnest(@ids) AS ids(id)
                    ON CONFLICT (character_id) DO UPDATE
                    SET online_seconds = LEAST(public.time_prize_state.online_seconds + @s, 2000000000), updated_at = now()
                    WHERE public.time_prize_state.beginner_claimed < @t
                    RETURNING character_id, online_seconds, beginner_claimed)
                SELECT string_agg(character_id || ':' || online_seconds || ':' || beginner_claimed, ',') FROM up;
                """, cancellationToken, ("s", TimePrizeCatalog.AccrualSeconds), ("t", (short)tiers),
                ("ids", eligible.Select(c => c.CharacterId).Distinct().ToArray())) as string;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[time-prize] accrual skipped: {error.GetType().Name}");
            return;
        }
        var state = new Dictionary<int, (int Seconds, int Claimed)>();
        foreach (var part in (rows ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            state[int.Parse(bits[0])] = (int.Parse(bits[1]), int.Parse(bits[2]));
        }
        foreach (var context in eligible)
        {
            if (!state.TryGetValue(context.CharacterId, out var row) || !_timePrizeAnnounced.TryAdd(context.Session, 0)) continue;
            try
            {
                await context.Session.SendAsync(TimePrizeCatalog.Status(row.Seconds, 0, row.Claimed, 0), cancellationToken, "TimePrizeStatus");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[time-prize] status skipped character={context.CharacterId}: {error.GetType().Name}");
            }
        }
    }
}
