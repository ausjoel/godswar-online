using Godswar.Server.Application.Finalise2;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// W2 F-46 Exchange Mentor / Alcippe (Sparta_052 / Athens_052, function 2, client NpcFunCredit.lua): the captured menu
/// [49, 50, 51] offers Masters Ethics -> B-Gold (311-313) and Reputation -> B-Gold (314-316), "only once per month".
/// Reputation is the character's honour balance (medusa_honor_points, the balance the battlefield awarder pays).
/// Prices from the client texts TS311-TS316 / BD3811-BD3816. TS314 says "920 Gold", its result line BD3814 says
/// "920 B-Gold": B-Gold is paid. One Reputation exchange per realm calendar month (the three tiers share it).
/// Masters Ethics (F-23, mentor.masters_ethics) pays 311-313 the same way from public.mentor_ethics; switch off = 3802.
/// Results: 3800-3802 / 3811-3816 at the result depth. Hot switch npc.reputation_exchange.
/// NOT BUILT: the older client's buttons 1-5 (100 rep -> 10,000 Silver ... 120 rep -> Ruby Piece): this client has no
/// Ruby Piece and only Level 4/5 Sapphire/Emerald Pieces, and the live menu never offers them.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int ReputationExchangeAlready = 3801;
    private const int ReputationExchangeNotEnough = 3802;

    private static readonly IReadOnlyDictionary<int, (int Cost, int BindingGold, int Result)> ReputationExchangeOffers =
        new Dictionary<int, (int, int, int)>
        {
            [314] = (1200, 920, 3814),
            [315] = (5000, 1600, 3815),
            [316] = (15000, 2300, 3816),
        };

    // W2 F-23: Masters Ethics -> B-Gold (client TS311-313 / BD3811-3813), once per realm month.
    private static readonly IReadOnlyDictionary<int, (int Cost, int BindingGold, int Result)> EthicsExchangeOffers =
        new Dictionary<int, (int, int, int)>
        {
            [311] = (8000, 1380, 3811),
            [312] = (25000, 2300, 3812),
            [313] = (70000, 3220, 3813),
        };

    private async Task<bool> TryHandleReputationExchangeAsync(uint npcId, int dialogIndex, int subId,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character || !Finalise2Rates.On(Finalise2RateKeys.ReputationExchange) ||
            subId is < 311 or > 316)
        {
            return false;
        }

        int result;
        if (subId <= 313)
        {
            result = Godswar.Server.Application.Mentor.MentorPolicy.MastersEthicsOn
                ? await ExchangeMastersEthicsAsync(EthicsExchangeOffers[subId], cancellationToken)
                : ReputationExchangeNotEnough;
        }
        else
        {
            var offer = ReputationExchangeOffers[subId];
            var local = _realmCalendar.ToRealmTime(DateTimeOffset.UtcNow);
            var month = new DateOnly(local.Year, local.Month, 1);
            result = await RunEventsNpcAsync(async tx =>
            {
                var used = await tx.ScalarAsync(
                    "SELECT used FROM public.finalise2_npc_daily WHERE character_id = @c AND day = @m AND counter = 'rep_bgold' FOR UPDATE;",
                    ("m", month));
                if (used is int n && n > 0) return ReputationExchangeAlready;
                var spent = await tx.ScalarAsync("""
                    UPDATE public.character_base
                    SET medusa_honor_points = medusa_honor_points - @n, medusa_reward_revision = medusa_reward_revision + 1
                    WHERE id = @c AND COALESCE(medusa_honor_points, 0) >= @n
                    RETURNING medusa_honor_points;
                    """, ("n", offer.Cost));
                if (spent is null) return ReputationExchangeNotEnough;
                await tx.ScalarAsync("""
                    INSERT INTO public.finalise2_npc_daily (character_id, day, counter, used) VALUES (@c, @m, 'rep_bgold', 1)
                    ON CONFLICT (character_id, day, counter) DO UPDATE SET used = finalise2_npc_daily.used + 1, updated_at = now();
                    """, ("m", month));
                await tx.AddBindingGoldAsync(offer.BindingGold);
                return offer.Result;
            }, ReputationExchangeNotEnough, cancellationToken);
        }

        Console.WriteLine($"[reputation-exchange] character={character.Name} sub={subId} result={result}");
        if (subId <= 313 && result == ReputationExchangeNotEnough &&
            Godswar.Server.Application.Mentor.MentorPolicy.MastersEthicsOn)
        {
            var balance = await RunEventsNpcAsync(async tx => Convert.ToInt32(await tx.ScalarAsync(
                "SELECT COALESCE((SELECT balance FROM public.mentor_ethics WHERE character_id = @c), 0);")), 0, cancellationToken);
            await SendEventsNpcNoticeAsync($"You have {balance:N0} Masters Ethics.", "MastersEthicsBalance", cancellationToken);
        }
        if (result is >= 3811 and <= 3816)
        {
            await RefreshAfterEventsNpcAsync([], cancellationToken);
            if (_character is { } refreshed && PacketBuilder.PlayerDetail(refreshed) is { Length: > 0 } detail)
            {
                await _session.SendAsync(detail, cancellationToken, "ReputationExchangeHonor", framed: false);
            }
        }
        await _session.SendAsync(PacketBuilder.NpcFunctionActionResponse(npcId, dialogIndex, [result]),
            cancellationToken, "ReputationExchangeResult");
        return true;
    }

    private async Task<int> ExchangeMastersEthicsAsync((int Cost, int BindingGold, int Result) offer,
        CancellationToken cancellationToken)
    {
        var local = _realmCalendar.ToRealmTime(DateTimeOffset.UtcNow);
        var month = new DateOnly(local.Year, local.Month, 1);
        return await RunEventsNpcAsync(async tx =>
        {
            var used = await tx.ScalarAsync(
                "SELECT used FROM public.finalise2_npc_daily WHERE character_id = @c AND day = @m AND counter = 'ethics_bgold' FOR UPDATE;",
                ("m", month));
            if (used is int n && n > 0) return ReputationExchangeAlready;
            if (await tx.ScalarAsync(
                    "SELECT to_regclass('public.mentor_ethics') IS NOT NULL;") is not true) return ReputationExchangeNotEnough;
            var spent = await tx.ScalarAsync("""
                UPDATE public.mentor_ethics SET balance = balance - @n, updated_at = now()
                WHERE character_id = @c AND balance >= @n RETURNING balance;
                """, ("n", offer.Cost));
            if (spent is null) return ReputationExchangeNotEnough;
            await tx.ScalarAsync("""
                INSERT INTO public.finalise2_npc_daily (character_id, day, counter, used) VALUES (@c, @m, 'ethics_bgold', 1)
                ON CONFLICT (character_id, day, counter) DO UPDATE SET used = finalise2_npc_daily.used + 1, updated_at = now();
                """, ("m", month));
            await tx.AddBindingGoldAsync(offer.BindingGold);
            return offer.Result;
        }, ReputationExchangeNotEnough, cancellationToken);
    }
}
