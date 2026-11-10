using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// W2 Rich Man Envoy "Rags to Riches" (Athens_129 / Sparta_129, function 33, client NpcFunLotto.lua, texts Lotto1-13):
/// every day 19:00-23:00 realm time hand in 10 Level 1 Crystals for 1 Exp Pill and enter a number 1-10,000. A round
/// closes at 100 entries; the lowest number entered by exactly one player wins (none unique = no winner). Every 20
/// entries a system line names the current most likely winner. Prizes are claimed with "Claim prizes", not between
/// 23:00 and 23:55. The prize is not stated by the client: GENERATED, hot rate npc.lottery_prize_gold (Gold).
/// Typed number = the dialog's amount field (as the Cadian Generals); the submit packet is logged raw.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int LotteryFunction = 33;
    private const int LotteryCrystal = 4230;
    private const int LotteryCrystalCount = 10;
    private const int LotteryExpPill = 4174;
    private const int LotteryRoundSize = 100;

    private async Task<bool> TryHandleLotteryAsync(NpcSpawnDefinition npc, int subId, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || !Finalise2Rates.On(Finalise2RateKeys.Lottery) ||
            npc.NpcKey is not ("Athens_129" or "Sparta_129"))
            return false;
        var path = DialogPath(packet);
        var amount = DialogAmount(packet);
        var local = _realmCalendar.ToRealmTime(DateTimeOffset.UtcNow).TimeOfDay;
        var open = local >= TimeSpan.FromHours(19) && local < TimeSpan.FromHours(23);
        // Rig tests only (never set on live): open outside the event hours.
        open |= Environment.GetEnvironmentVariable("GODSWAR_LOTTERY_ALWAYS_OPEN") == "1";
        if (subId == 102 && path is [] or [102] && amount <= 0)
        {
            // "Go for it!": the number page only while the event is open.
            await SendFinalise2ReplyAsync(npc, LotteryFunction, open ? [202, 203] : [204], "LotteryPage", cancellationToken);
            return true;
        }
        if (subId == 103 && path is [] or [103])
        {
            await ClaimLotteryAsync(npc, local, cancellationToken);
            return true;
        }
        if (amount == 0 && !(path.Contains(202) || path.Contains(203))) return false;

        Console.WriteLine($"[lottery] character={_character.Name} sub={subId} path=[{string.Join(',', path)}] amount={amount}");
        if (!open)
        {
            await SendFinalise2ReplyAsync(npc, LotteryFunction, [204], "LotteryClosed", cancellationToken);
            return true;
        }
        if (amount is < 1 or > 10000)
        {
            await SendFinalise2ReplyAsync(npc, LotteryFunction, [390], "LotteryRange", cancellationToken);
            return true;
        }
        var characterId = _character.Id;
        var (result, entries, hint) = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.CountAsync(LotteryCrystal) < LotteryCrystalCount) return (391, 0, (string?)null);
            if (!await tx.ConsumeAsync(LotteryCrystal, LotteryCrystalCount)) return (391, 0, null);
            if (!await tx.GrantAsync(LotteryExpPill, 1)) { tx.Rollback(); return (206, 0, null); }
            var round = (int)(await tx.ScalarAsync(
                "SELECT round_id FROM public.eventsnpc_lottery_rounds WHERE closed_at IS NULL ORDER BY round_id DESC LIMIT 1 FOR UPDATE;")
                ?? await tx.ScalarAsync("INSERT INTO public.eventsnpc_lottery_rounds DEFAULT VALUES RETURNING round_id;"))!;
            await tx.ScalarAsync("INSERT INTO public.eventsnpc_lottery_entries (round_id, character_id, number) VALUES (@r, @c, @n);",
                ("r", round), ("n", amount));
            var count = Convert.ToInt32(await tx.ScalarAsync(
                "SELECT count(*) FROM public.eventsnpc_lottery_entries WHERE round_id = @r;", ("r", round)));
            const string lowestUnique = """
                SELECT e.number || ':' || e.character_id FROM public.eventsnpc_lottery_entries e
                WHERE e.round_id = @r AND e.number IN (SELECT number FROM public.eventsnpc_lottery_entries
                    WHERE round_id = @r GROUP BY number HAVING count(*) = 1)
                ORDER BY e.number LIMIT 1;
                """;
            string? leader = await tx.ScalarAsync(lowestUnique, ("r", round)) as string;
            if (count >= LotteryRoundSize)
            {
                var parts = leader?.Split(':');
                await tx.ScalarAsync("""
                    UPDATE public.eventsnpc_lottery_rounds SET closed_at = now(), winning_number = @n, winner_character_id = @w
                    WHERE round_id = @r;
                    """, ("r", round), ("n", parts is null ? DBNull.Value : int.Parse(parts[0])),
                    ("w", parts is null ? DBNull.Value : int.Parse(parts[1])));
            }
            string? hintName = null;
            if (count % 20 == 0 && leader is not null)
                hintName = await tx.ScalarAsync("SELECT name FROM public.character_base WHERE id = @w;",
                    ("w", int.Parse(leader.Split(':')[1]))) as string;
            return (301, count, hintName);
        }, (391, 0, (string?)null), cancellationToken);
        Console.WriteLine($"[lottery] character={_character.Name} number={amount} result={result} entries={entries}");
        await RefreshAfterEventsNpcAsync(result == 301 ? [(LotteryExpPill, 1)] : [], cancellationToken);
        await SendFinalise2ReplyAsync(npc, LotteryFunction, [result], "LotteryEntry", cancellationToken);
        if (hint is not null)
        {
            var hintText = $"Rags to Riches: {entries} entries so far. {hint} holds the lowest unique number.";
            await _registry.BroadcastSystemNoticeAsync(hintText, cancellationToken);
            await _registry.MirrorToBoardAsync(Finalise2RateKeys.BoardLotteryHints, hintText, cancellationToken);
        }
        return true;
    }

    private async Task ClaimLotteryAsync(NpcSpawnDefinition npc, TimeSpan local, CancellationToken cancellationToken)
    {
        if (local >= TimeSpan.FromHours(23) && local < new TimeSpan(23, 55, 0))
        {
            await SendEventsNpcNoticeAsync("Prizes can be claimed after 11:55pm.", "LotteryClaimWindow", cancellationToken);
            return;
        }
        var prize = Finalise2Rates.Amount(Finalise2RateKeys.LotteryPrizeGold);
        var won = await RunEventsNpcAsync(async tx =>
        {
            var rounds = Convert.ToInt32(await tx.ScalarAsync("""
                WITH c AS (UPDATE public.eventsnpc_lottery_rounds SET claimed = true
                           WHERE winner_character_id = @c AND NOT claimed AND closed_at IS NOT NULL RETURNING 1)
                SELECT count(*) FROM c;
                """));
            if (rounds > 0)
                await tx.ScalarAsync("UPDATE public.character_base SET \"Stone\" = LEAST(\"Stone\"::bigint + @g, 2147483647)::integer, " +
                    "wallet_revision = wallet_revision + 1 WHERE id = @c;", ("g", (long)prize * rounds));
            return rounds;
        }, 0, cancellationToken);
        Console.WriteLine($"[lottery] claim character={_character!.Name} rounds={won} gold={prize * won}");
        if (won == 0)
        {
            await SendFinalise2ReplyAsync(npc, LotteryFunction, [207], "LotteryNoPrize", cancellationToken);
            return;
        }
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendEventsNpcNoticeAsync($"Rags to Riches: you claimed {prize * won:N0} Gold.", "LotteryClaimed", cancellationToken);
    }
}
