using Godswar.Server.Application.EventsNpc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// EVENTSNPC Random Quest Manager and Treasure Map (IGG igg-acid=198, wiki-14; client NpcFunQuest.lua NF_L0_QT*).
// Chain: the manager gives a Quest Sack (Lv70+). A sack holds Exp Pills (one per finished chain quest, so the
// manager's first sack holds none) and one random faction Random Quest Scroll; the scroll starts its quest
// through the existing quest-scroll path. Each finished chain quest gives the next sack; the 10th gives a
// Treasure Map, 3 Talent Stone 3 and 10 Exp Pills instead (wiki-14). The manager also takes Exp Pills.
internal sealed partial class GameClientHandler
{
    private enum ChainOutcome { Done, Busy, BagFull, Missing, Level, Owed }

    /// <summary>
    /// A Random Quest Scroll quest while the event is on: wiki-14 opens the chain to every level from 70, so the
    /// shared nine-level accept band (which would shut it at level 80) does not apply. Off: false, band unchanged.
    /// </summary>
    internal static bool IsEventsNpcChainStep(StarterQuestChain.Step step) =>
        step.IsQuestScroll && EventsNpcPolicy.IsChainQuest(step.QuestId) && EventsNpcRates.RandomQuestOn;

    private async Task<bool> HandleRandomQuestManagerAsync(NpcSpawnDefinition npc, int dialogIndex, int selection,
        int[] path, int amount, CancellationToken cancellationToken)
    {
        var form = TakeEventsNpcForm(npc, EventsNpcPolicy.QuestFunction);
        if (form is not null && (selection >= 0 || amount > 0))
        {
            await TakeExpPillsAsync(npc, dialogIndex, path, amount, cancellationToken);
            return true;
        }

        if (selection == 102)
        {
            // Index 2: NF_L0_QT202, the quantity box.
            _eventsNpcForm = new EventsNpcForm(npc.InteractionId, EventsNpcPolicy.QuestFunction, 102, 0, 0, 0,
                EventsNpcNow.AddMinutes(5));
            await SendEventsNpcReplyAsync(npc, dialogIndex, [202], "RandomQuestPillForm", cancellationToken);
            return true;
        }

        if (selection != 101)
        {
            if (selection < 0)
            {
                _eventsNpcForm = null;
            }

            return false;
        }

        var character = _character!;
        var camp = character.Camp;
        var sack = EventsNpcPolicy.QuestSack(camp);
        // Review fix 1: the "busy" test runs inside the transaction against the stored chain state, not the bag
        // alone (a sack in the warehouse is not in the bag). Review fix 2: an owed chain reward is paid first.
        var (outcome, granted) = character.Level < EventsNpcPolicy.MinimumRandomQuestLevel
            ? (ChainOutcome.Level, (IReadOnlyList<(int, int)>)[])
            : await RunEventsNpcAsync(async tx =>
            {
                var state = tx.State;
                if (state.RewardStep > 0)
                {
                    var owed = await GrantChainRewardAsync(tx, camp, state.RewardStep);
                    if (owed is null)
                    {
                        tx.Rollback();
                        return (ChainOutcome.BagFull, (IReadOnlyList<(int, int)>)[]);
                    }

                    tx.State = AdvanceChain(state, state.RewardStep);
                    return (state.RewardStep >= EventsNpcPolicy.ChainLength ? ChainOutcome.Owed : ChainOutcome.Done,
                        owed);
                }

                if (state.SackPending)
                {
                    // A sack is out for this step. Holding it: busy. Not in the bag (stashed, dropped, deleted): a
                    // copy for the SAME step; only one open can pay, so copies are worth nothing extra.
                    if (await tx.CountAsync(sack) > 0)
                    {
                        return (ChainOutcome.Busy, (IReadOnlyList<(int, int)>)[]);
                    }
                }
                else if (state.ChainQuestId != 0)
                {
                    // The drawn scroll or its quest still held: busy. Abandoned: a new chain from step 0 (keeping
                    // the step would let a stashed scroll be read after a re-issued sack paid the step again).
                    var quest = (uint)state.ChainQuestId;
                    var item = EventsNpcPolicy.ScrollItemFirst(camp) + (int)(quest - EventsNpcPolicy.ScrollQuestFirst(camp));
                    if (character.Quests.Any(carried => carried.QuestId == quest) || await tx.CountAsync(item) > 0)
                    {
                        return (ChainOutcome.Busy, (IReadOnlyList<(int, int)>)[]);
                    }
                }

                if (!await tx.GrantAsync(sack, 1, bound: null))
                {
                    tx.Rollback();
                    return (ChainOutcome.BagFull, (IReadOnlyList<(int, int)>)[]);
                }

                tx.State = state.SackPending
                    ? state
                    : state with { ChainStep = 0, ChainQuestId = 0, SackPending = true };
                return (ChainOutcome.Done, (IReadOnlyList<(int, int)>)[(sack, 1)]);
            }, (ChainOutcome.Missing, (IReadOnlyList<(int, int)>)[]), cancellationToken);

        // NF_L0_QT250 claimed, 220 level, 230 still holding chain work, 240 bag full; 210 (neutral) after an owed
        // 10th-step reward, whose items are announced like any grant.
        var line = outcome switch
        {
            ChainOutcome.Done => 250,
            ChainOutcome.Owed => 210,
            ChainOutcome.Level => 220,
            ChainOutcome.BagFull => 240,
            _ => 230
        };
        if (outcome is ChainOutcome.Done or ChainOutcome.Owed)
        {
            await RefreshAfterEventsNpcAsync(granted, cancellationToken);
        }

        await SendEventsNpcReplyAsync(npc, dialogIndex, [line], $"RandomQuestSack{outcome}", cancellationToken);
        return true;
    }

    /// <summary>
    /// The reward for finishing chain step <paramref name="step"/>: the next Quest Sack, or after the 10th the
    /// Treasure Map, 3 Talent Stone 3 and the step's Exp Pills (wiki-14). Bindings follow each item template
    /// (review fix 3: wiki-14 says the map is tradable). Null when the bag is full; the caller rolls back.
    /// </summary>
    private static async Task<IReadOnlyList<(int, int)>?> GrantChainRewardAsync(
        Infrastructure.EventsNpc.EventsNpcTransaction tx, byte camp, int step)
    {
        IReadOnlyList<(int ItemId, int Count)> grants = step >= EventsNpcPolicy.ChainLength
            ? [(EventsNpcPolicy.TreasureMap(camp), 1), (EventsNpcPolicy.TalentStone3, 3),
                (EventsNpcPolicy.ExpPill, EventsNpcPolicy.PillsForStep(step))]
            : [(EventsNpcPolicy.QuestSack(camp), 1)];
        foreach (var (itemId, count) in grants)
        {
            if (!await tx.GrantAsync(itemId, count, bound: null))
            {
                return null;
            }
        }

        return grants;
    }

    /// <summary>Only the one sack the state says is out, before its scroll is drawn, pays (review fix 1).</summary>
    internal static bool SackOpens(Infrastructure.EventsNpc.EventsNpcState state) =>
        state.SackPending && state.ChainQuestId == 0 && state.RewardStep == 0;

    /// <summary>The state after step <paramref name="step"/> was paid: a sack is out, or the chain is over.</summary>
    internal static Infrastructure.EventsNpc.EventsNpcState AdvanceChain(Infrastructure.EventsNpc.EventsNpcState state,
        int step) =>
        step >= EventsNpcPolicy.ChainLength
            ? state with { ChainStep = 0, ChainQuestId = 0, SackPending = false, RewardStep = 0 }
            : state with { ChainStep = step, ChainQuestId = 0, SackPending = true, RewardStep = 0 };

    private async Task TakeExpPillsAsync(NpcSpawnDefinition npc, int dialogIndex, int[] path, int amount,
        CancellationToken cancellationToken)
    {
        var held = CountBagItem(EventsNpcPolicy.ExpPill);
        var character = _character!;
        // UNPROVEN: the typed count is read from +0x38. With nothing readable there the turn-in is refused (310):
        // an unproven layout must not become a silent spend. Every word is logged so one real-client try settles it.
        var count = amount;
        Console.WriteLine($"[eventsnpc] pills typed={amount} held={held} path=[{string.Join(',', path)}] " +
            $"words:{_eventsNpcWords}");
        var experience = (long)Math.Max(count, 0) * PlayerExperienceItemPolicy.RatedExperiencePerPill;
        int line;
        if (held <= 0)
        {
            line = 330;
        }
        else if (count <= 0 || count > EventsNpcPolicy.MaxPillsPerTurnIn || count > held ||
                 experience > int.MaxValue || // only above exp.pill 21: refused, nothing taken

                 !PlayerExperienceItemPolicy.TryApply(character.Level, character.Experience,
                     character.FighterLevelSealed, (int)experience, out _))
        {
            // Like the stock pill: nothing is taken when the EXP cannot all be received (level or EXP cap).
            line = 310;
        }
        else
        {
            var taken = await RunEventsNpcAsync(async tx => await tx.ConsumeAsync(EventsNpcPolicy.ExpPill, count),
                false, cancellationToken);
            line = taken ? 320 : 310;
            if (taken)
            {
                await RefreshAfterEventsNpcAsync([], cancellationToken);
                // Review fix 5: exactly the amount TryApply validated (exp.pill only, like using the pill); no
                // exp.events and no 100M event cap on top.
                await PayEventsNpcExperienceAsync(experience, "RandomQuestPills", cancellationToken, eventRate: false);
            }
        }

        // Index 3: QT310 invalid/insufficient, QT320 taken, QT330 no pills.
        await SendEventsNpcReplyAsync(npc, dialogIndex, [line], "RandomQuestPills", cancellationToken);
    }

    /// <summary>
    /// Right-click on a Quest Sack or a Treasure Map (hook in InventoryActivation). False when the event is
    /// off or the item is neither, so the base path (the generated sack pack) runs unchanged.
    /// </summary>
    private async Task<bool> TryUseEventsNpcItemAsync(int itemId, CancellationToken cancellationToken)
    {
        if (!EventsNpcRates.RandomQuestOn || _character is not { } character ||
            !EventsNpcPolicy.IsQuestSack(itemId) && !EventsNpcPolicy.IsTreasureMap(itemId))
        {
            return false;
        }

        if (EventsNpcPolicy.IsTreasureMap(itemId))
        {
            // GENERATED loot. Only reached with item.treasure_map_digs = 0: the dig-spot handler
            // (GameClientHandler.TreasureMapDigs.cs) takes the maps first when it is on. Guardian demons are not built.
            var (lootId, lootCount) = EventsNpcPolicy.DrawTreasure(Random.Shared.Next());
            var dug = character.CurrentMap > 1 && await RunEventsNpcAsync(async tx =>
            {
                if (!await tx.ConsumeAsync(itemId, 1) || !await tx.GrantAsync(lootId, lootCount, bound: null))
                {
                    tx.Rollback();
                    return false;
                }

                return true;
            }, false, cancellationToken);
            Console.WriteLine($"[eventsnpc] treasure map character={character.Name} map={character.CurrentMap} " +
                $"dug={dug} loot={lootId}x{lootCount}");
            await RefreshAfterEventsNpcAsync([], cancellationToken);
            return true;
        }

        var pool = RandomQuestPool(character);
        var scroll = pool.Count > 0 ? pool[Random.Shared.Next(pool.Count)] : default;
        var opened = pool.Count > 0 && await RunEventsNpcAsync(async tx =>
        {
            // Review fix 1: a sack is a one-shot ticket. Only the sack the state says is out (and whose step has
            // not drawn its scroll yet) pays; any other copy stays in the bag and gives nothing.
            if (!SackOpens(tx.State))
            {
                return false;
            }

            var pills = tx.State.ChainStep;
            if (!await tx.ConsumeAsync(itemId, 1) || !await tx.GrantAsync(scroll.ItemId, 1, bound: null) ||
                !await tx.GrantAsync(EventsNpcPolicy.ExpPill, pills, bound: null))
            {
                tx.Rollback();
                return false;
            }

            tx.State = tx.State with { ChainQuestId = scroll.QuestId, SackPending = false };
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[eventsnpc] quest sack character={character.Name} opened={opened} pool={pool.Count} " +
            $"scroll={scroll.ItemId} quest={scroll.QuestId}");
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        return true;
    }

    /// <summary>The faction's Random Quest Scrolls whose quest this character could accept now.</summary>
    private static List<(int ItemId, uint QuestId)> RandomQuestPool(GameCharacter character)
    {
        var pool = new List<(int, uint)>();
        var today = QuestDailyState.Today();
        for (var offset = 0; offset < EventsNpcPolicy.ScrollCount; offset++)
        {
            var itemId = EventsNpcPolicy.ScrollItemFirst(character.Camp) + offset;
            if (!QuestRuntimeRequirements.ScrollItems.TryGetValue((uint)itemId, out var questId) ||
                questId is 406u or 1406u || // the admin 2026-11-01: "leave until tried"
                StarterQuestChain.Find(questId) is not { IsQuestScroll: true } step ||
                !CanAcceptQuest(character, step, today, fromScroll: true) ||
                QuestNpcSharesPlayerObjectId(step.ResponderKey) ||
                !Infrastructure.WorldContent.CapturedNpcPlacements.TryFind(step.ResponderKey, out _) &&
                Infrastructure.WorldContent.CapturedNpcPlacements.HasCaptureFor(step.ResponderMapId))
            {
                continue; // a responder the captured map drops could never take the hand-in
            }

            pool.Add((itemId, questId));
        }

        return pool;
    }

    /// <summary>After a quest hand-in (hook at the end of HandleQuestHandInAsync): pay the chain.</summary>
    private async Task OnEventsNpcQuestHandedInAsync(uint questId, CancellationToken cancellationToken)
    {
        if (!EventsNpcRates.RandomQuestOn || _character is not { } character || !EventsNpcPolicy.IsChainQuest(questId))
        {
            return;
        }

        // A chain quest must be repeatable in a later chain: a one-time scroll quest stays in the completed list
        // for good otherwise. Review fix 2: removed whatever the payout below does.
        character.QuestCompletedIds = [.. character.QuestCompletedIds.Where(id => id != questId)];
        await SaveQuestStateAsync(cancellationToken);

        var camp = character.Camp;
        var (paid, step, grants) = await RunEventsNpcAsync(async tx =>
        {
            if (tx.State.ChainQuestId != questId)
            {
                return (false, 0, (IReadOnlyList<(int, int)>?)null);
            }

            var next = tx.State.ChainStep + 1;
            var granted = await GrantChainRewardAsync(tx, camp, next);
            if (granted is null)
            {
                tx.Rollback();
                return (false, next, null);
            }

            tx.State = AdvanceChain(tx.State, next);
            return (true, next, granted);
        }, (false, 0, (IReadOnlyList<(int, int)>?)null), cancellationToken);
        if (!paid)
        {
            Console.WriteLine($"[eventsnpc] chain quest {questId} not paid character={character.Name} step={step}");
            if (step > 0)
            {
                // Bag full: the reward is owed, not lost. The manager pays it on the next "Claim Quest Sack".
                var owed = await RunEventsNpcAsync(async tx =>
                {
                    if (tx.State.ChainQuestId != questId)
                    {
                        return false;
                    }

                    tx.State = tx.State with { ChainQuestId = 0, SackPending = false, RewardStep = step };
                    return await Task.FromResult(true);
                }, false, cancellationToken);
                if (owed)
                {
                    await SendEventsNpcNoticeAsync(
                        "Random Quest:Your bag is full. Make room, then talk to the Random Quest Manager to collect " +
                        "your chain reward.", "RandomQuestRewardOwed", cancellationToken);
                }
            }

            return;
        }

        Console.WriteLine($"[eventsnpc] chain quest {questId} paid character={character.Name} step={step}");
        await RefreshAfterEventsNpcAsync(grants!, cancellationToken);
    }
}
