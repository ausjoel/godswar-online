using Godswar.Server.Application.Finalise2;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>W1 prize claims: the beginner-gift claim (see <see cref="TimePrizeCatalog"/>).</summary>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleTimePrizePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (packet.Opcode is not (TimePrizeCatalog.BeginnerClaimOpcode or TimePrizeCatalog.WeeklyClaimOpcode)) return false;
        if (_character is not { } character || !Finalise2Rates.On(Finalise2RateKeys.BeginnerGifts)) return true;
        if (packet.Opcode == TimePrizeCatalog.WeeklyClaimOpcode)
        {
            await _session.SendAsync(TimePrizeCatalog.ReturnCode(TimePrizeCatalog.AllClaimed), cancellationToken, "TimePrizeWeekly");
            return true;
        }

        var tiers = TimePrizeCatalog.BeginnerTiers;
        var (code, tier) = await RunEventsNpcAsync(async tx =>
        {
            await tx.ScalarAsync("INSERT INTO public.time_prize_state (character_id) VALUES (@c) ON CONFLICT DO NOTHING;");
            var row = await tx.ScalarAsync(
                "SELECT online_seconds::bigint * 1000 + beginner_claimed FROM public.time_prize_state WHERE character_id = @c FOR UPDATE;");
            var packed = Convert.ToInt64(row);
            var seconds = (int)(packed / 1000);
            var claimed = (int)(packed % 1000);
            if (claimed >= tiers.Count) return (TimePrizeCatalog.AllClaimed, 0);
            var next = tiers[claimed];
            if (seconds + TimePrizeCatalog.GraceSeconds < next.Minutes * 60) return (TimePrizeCatalog.NotYet, 0);
            foreach (var item in next.Items)
            {
                if (!await tx.GrantAsync(item.ItemId, item.Count, item.Bound))
                {
                    tx.Rollback();
                    return (TimePrizeCatalog.BagFull, 0);
                }
            }
            var updated = await tx.ScalarAsync(
                "UPDATE public.time_prize_state SET beginner_claimed = beginner_claimed + 1, updated_at = now() " +
                "WHERE character_id = @c AND beginner_claimed = @k RETURNING beginner_claimed;", ("k", (short)claimed));
            if (updated is null) { tx.Rollback(); return (TimePrizeCatalog.AllClaimed, 0); }
            return ((byte)0, next.Index);
        }, (TimePrizeCatalog.NotYet, 0), cancellationToken);

        Console.WriteLine($"[time-prize] character={character.Name} code={code} tier={tier}");
        if (code != 0)
        {
            await _session.SendAsync(TimePrizeCatalog.ReturnCode(code), cancellationToken, "TimePrizeCode");
            return true;
        }
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await _session.SendAsync(TimePrizeCatalog.Claimed(TimePrizeCatalog.BeginnerClaimOpcode), cancellationToken, "TimePrizeClaimed");
        return true;
    }
}
