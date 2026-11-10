using Godswar.Server.Application.EventsNpc;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

// EVENTSNPC Lost Book (IGG igg-acid=161; wiki-12/41; client NpcFunOldMan/Remain/LostBook.lua). Every answer is
// one click deep, so each reply is the script's own Index-2 line.
internal sealed partial class GameClientHandler
{
    private enum LostBookOutcome { Done, Limit, Missing, BagFull }

    /// <summary>NpcFunRemain.lua (NPC_FLAG_SYS_REMAIN = 25). Its pages are answered by HandleAncientRelicAsync.</summary>
    private static readonly ScriptedNpcDialogue AncientRelicsDialogue = new(
        FunctionNumber: 25,
        OpeningMenu: [501],
        Steps: new Dictionary<int, int[]>());

    /// <summary>Mysterious Elder (function 23): 1 free Scroll 7 a day; 3/4/5 join chapters.</summary>
    private async Task<bool> HandleMysteriousElderAsync(NpcSpawnDefinition npc, int dialogIndex, int selection,
        CancellationToken cancellationToken)
    {
        if (selection is not (1 or 3 or 4 or 5))
        {
            return false; // the opening page and anything else keep the catalog's answer
        }

        var camp = _character!.Camp;
        int[] consume;
        int grant;
        switch (selection)
        {
            case 1:
                consume = [];
                grant = EventsNpcPolicy.Scroll(camp, 7);
                break;
            case 3:
                consume = [EventsNpcPolicy.Chapter(1), EventsNpcPolicy.Chapter(2), EventsNpcPolicy.Chapter(3)];
                grant = EventsNpcPolicy.IncompleteLostBook;
                break;
            case 4:
                consume = [EventsNpcPolicy.Chapter(4), EventsNpcPolicy.Chapter(5), EventsNpcPolicy.Chapter(6)];
                grant = EventsNpcPolicy.IncompleteLostBook;
                break;
            default:
                consume = [.. Enumerable.Range(1, 7).Select(EventsNpcPolicy.Chapter)];
                grant = EventsNpcPolicy.LostBook;
                break;
        }

        var outcome = await RunEventsNpcAsync(async tx =>
        {
            if (selection == 1 && tx.State.ElderScrolls >= 1)
            {
                return LostBookOutcome.Limit;
            }

            foreach (var chapter in consume)
            {
                if (!await tx.ConsumeAsync(chapter, 1))
                {
                    tx.Rollback();
                    return LostBookOutcome.Missing;
                }
            }

            if (!await tx.GrantAsync(grant, 1))
            {
                tx.Rollback();
                return LostBookOutcome.BagFull;
            }

            if (selection == 1)
            {
                tx.State = tx.State with { ElderScrolls = tx.State.ElderScrolls + 1 };
            }

            return LostBookOutcome.Done;
        }, LostBookOutcome.Missing, cancellationToken);

        // NF_L0_LT503 claimed, 505 daily limit, 501 bag full; 511/512 joined, 601 not enough chapters.
        var line = outcome switch
        {
            LostBookOutcome.Done => selection == 1 ? 503 : selection == 5 ? 512 : 511,
            LostBookOutcome.Limit => 505,
            LostBookOutcome.BagFull => 501,
            _ => 601
        };
        if (outcome == LostBookOutcome.Done)
        {
            await RefreshAfterEventsNpcAsync([(grant, 1)], cancellationToken);
        }

        await SendEventsNpcReplyAsync(npc, dialogIndex, [line], $"LostBookElder{outcome}", cancellationToken);
        return true;
    }

    /// <summary>Ancient Relics N (function 25): scroll N for chapter N plus EXP by level; 108 = all of them.</summary>
    private async Task<bool> HandleAncientRelicAsync(NpcSpawnDefinition npc, int dialogIndex, int selection,
        CancellationToken cancellationToken)
    {
        var relic = EventsNpcPolicy.RelicNumber(npc.NpcKey);
        if (relic == 0)
        {
            return false;
        }

        if (selection < 0)
        {
            // Page one: the greeting, this relic's own button (101-107 share one position) and "exchange all".
            await SendEventsNpcReplyAsync(npc, dialogIndex, [501, 100 + relic, 108], "RelicMenu", cancellationToken);
            return true;
        }

        if (selection != 100 + relic && selection != 108)
        {
            await SendEventsNpcReplyAsync(npc, dialogIndex, [200], "RelicWrongButton", cancellationToken);
            return true;
        }

        var all = selection == 108;
        var camp = _character!.Camp;
        // The faction's own Scroll N first, then the other faction's Scroll N: the Boogyman loot rules are keyed by
        // monster template, which both suburbs share, so a drop cannot follow the killer's faction.
        int[] scrolls = [EventsNpcPolicy.Scroll(camp, relic), EventsNpcPolicy.Scroll((byte)(camp == 1 ? 0 : 1), relic)];
        var band = EventsNpcPolicy.RelicExperienceBand(_character.Level);
        var lucky = !all && Random.Shared.Next(100) < EventsNpcPolicy.RelicLostBookChancePercent;
        var reward = lucky ? EventsNpcPolicy.LostBook : EventsNpcPolicy.Chapter(relic);
        var (outcome, count) = await RunEventsNpcAsync(async tx =>
        {
            var taken = 0;
            foreach (var scroll in scrolls)
            {
                var held = await tx.CountAsync(scroll);
                var take = all ? held : Math.Min(held, 1 - taken);
                if (take > 0 && await tx.ConsumeAsync(scroll, take))
                {
                    taken += take;
                }
            }

            if (taken <= 0)
            {
                return (LostBookOutcome.Missing, 0);
            }

            if (!await tx.GrantAsync(reward, taken))
            {
                tx.Rollback();
                return (LostBookOutcome.BagFull, 0);
            }

            return (LostBookOutcome.Done, taken);
        }, (LostBookOutcome.Missing, 0), cancellationToken);

        if (outcome != LostBookOutcome.Done)
        {
            // NF_L0_RM202 "You don't have the required Sheepskin Scroll", RM201 bag full.
            await SendEventsNpcReplyAsync(npc, dialogIndex, [outcome == LostBookOutcome.BagFull ? 201 : 202],
                $"Relic{outcome}", cancellationToken);
            return true;
        }

        await RefreshAfterEventsNpcAsync([(reward, count)], cancellationToken);
        await PayEventsNpcExperienceAsync(500L * band * count, "LostBookRelic", cancellationToken);
        var line = lucky
            ? EventsNpcPolicy.RelicLostBookLine + 1000 * band
            : all
                ? EventsNpcPolicy.RelicAllReply(relic, band, count)
                : EventsNpcPolicy.RelicSingleReply(relic, band);
        await SendEventsNpcReplyAsync(npc, dialogIndex, [line], lucky ? "RelicLostBook" : "RelicChapter",
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Wishing Pool page 24 (hook in HandleWishingPoolActionAsync): 1 = wish with an Incomplete Lost Book,
    /// 2 = with a Lost Book. IGG caps them at 5 and 2 a day.
    /// </summary>
    private async Task<bool> TryHandleLostBookWishAsync(uint npcId, int page, int selection,
        CancellationToken cancellationToken)
    {
        if (!EventsNpcRates.LostBookOn || _character is null || page != WishingPoolLostBookPage)
        {
            return false;
        }

        if (selection is not (1 or 2))
        {
            var state = await RunEventsNpcAsync(tx => Task.FromResult<Godswar.Server.Infrastructure.EventsNpc.EventsNpcState?>(tx.State), null, cancellationToken);
            var remaining = state is null
                ? 0
                : Math.Max(0, EventsNpcPolicy.IncompleteWishesPerDay - state.IncompleteWishes) +
                  Math.Max(0, EventsNpcPolicy.CompleteWishesPerDay - state.CompleteWishes);
            await SendWishingPoolPageAsync(npcId, page, [EventsNpcPolicy.WishHeader(remaining), 1, 2],
                "LostBookWishMenu", cancellationToken);
            return true;
        }

        var complete = selection == 2;
        var table = complete ? EventsNpcPolicy.CompleteWishes : EventsNpcPolicy.IncompleteWishes;
        var reward = table[Random.Shared.Next(table.Length)];
        var book = complete ? EventsNpcPolicy.LostBook : EventsNpcPolicy.IncompleteLostBook;
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            var used = complete ? tx.State.CompleteWishes : tx.State.IncompleteWishes;
            if (used >= (complete ? EventsNpcPolicy.CompleteWishesPerDay : EventsNpcPolicy.IncompleteWishesPerDay))
            {
                return LostBookOutcome.Limit;
            }

            if (!await tx.ConsumeAsync(book, 1))
            {
                tx.Rollback();
                return LostBookOutcome.Missing;
            }

            if (reward.ItemId > 0 && !await tx.GrantAsync(reward.ItemId, reward.Count))
            {
                tx.Rollback();
                return LostBookOutcome.BagFull;
            }

            if (reward.TalentPoints > 0)
            {
                await tx.AddTalentPointsAsync(reward.TalentPoints);
            }

            tx.State = complete
                ? tx.State with { CompleteWishes = tx.State.CompleteWishes + 1 }
                : tx.State with { IncompleteWishes = tx.State.IncompleteWishes + 1 };
            return LostBookOutcome.Done;
        }, LostBookOutcome.Missing, cancellationToken);

        // NF_L0_BK500 / BK800 daily limits, BK601 no book, BK600 no space.
        int[] reply = outcome switch
        {
            LostBookOutcome.Limit => [complete ? 800 : 500],
            LostBookOutcome.BagFull => [600],
            LostBookOutcome.Missing => [601],
            _ => [reward.Reply(_character.Level)]
        };
        if (outcome == LostBookOutcome.Done)
        {
            await RefreshAfterEventsNpcAsync(reward.ItemId > 0 ? [(reward.ItemId, reward.Count)] : [],
                cancellationToken);
            if (reward.ExperiencePerLevel > 0)
            {
                await PayEventsNpcExperienceAsync((long)reward.ExperiencePerLevel * _character.Level,
                    "LostBookWish", cancellationToken);
            }
        }

        await SendWishingPoolPageAsync(npcId, page, reply, $"LostBookWish{outcome}", cancellationToken);
        return true;
    }
}
