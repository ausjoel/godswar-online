using Godswar.Server.Application.EventsNpc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;

namespace Godswar.Server.Game;

// EVENTSNPC GodsWar Marathon (IGG igg-acid=160; client NpcFunHealth.lua and NF_L0_H*). The client draws a reply
// on the page whose Index equals the number of replies since the window opened, so:
//   Trainer 1: [1000,1001,1003,1004] -> 1000 -> Index 2 [1006 rules, 1005 Start] -> 1005 -> Index 3 [1401 ...]
//   any trainer: 1001/1201 -> Index 2 sum form [1281, 1250+N, a*10000+50, b*10000+60] -> submit -> Index 3
//   [1401+N exchanged | 3292/3202 finished + time | 1280 wrong sum | 1104 six races | 1512 bag full].
// The submission's layout is UNPROVEN: it is recognised as the next request from the same window while the form
// is pending, and the typed sum is read from +0x38 (proven only for the guild altar's edit boxes).
internal sealed partial class GameClientHandler
{
    private enum RaceOutcome { Exchanged, Finished, NoManual, Limit, BagFull }

    private async Task<bool> HandleFitnessTrainerAsync(NpcSpawnDefinition npc, int dialogIndex, int selection,
        int[] path, int amount, CancellationToken cancellationToken)
    {
        var trainer = EventsNpcPolicy.TrainerNumber(npc.NpcKey);
        if (trainer == 0)
        {
            return false;
        }

        if (selection < 0 && amount > 0 && TakeEventsNpcForm(npc, EventsNpcPolicy.HealthFunction) is { } typed)
        {
            // UNPROVEN: a submission that carries only the typed number.
            await SubmitHealthManualAsync(npc, dialogIndex, trainer, typed, path, amount, cancellationToken);
            return true;
        }

        if (selection < 0)
        {
            // A fresh window: forget any unanswered form and draw this trainer's own first page.
            _eventsNpcForm = null;
            int[] menu = trainer == 1 ? [1000, 1001, 1003, 1004] : [1201, 1218];
            await SendEventsNpcReplyAsync(npc, dialogIndex, menu, "TrainerMenu", cancellationToken);
            return true;
        }

        var character = _character!;
        var now = EventsNpcNow;
        var realmTime = _realmCalendar.ToRealmTime(now);
        var form = TakeEventsNpcForm(npc, EventsNpcPolicy.HealthFunction);
        if (form is not null)
        {
            await SubmitHealthManualAsync(npc, dialogIndex, trainer, form, path, amount, cancellationToken);
            return true;
        }

        switch (selection)
        {
            case 1000 when trainer == 1:
            {
                // Index 2. Level gate (SM_Msg_01), race days (H1222), six races (1113 = H1104).
                var races = (await RunEventsNpcAsync(tx => Task.FromResult<Godswar.Server.Infrastructure.EventsNpc.EventsNpcState?>(tx.State), null, cancellationToken))?.Races
                    ?? EventsNpcPolicy.RacesPerDay;
                int[] reply = character.Level < EventsNpcPolicy.MinimumMarathonLevel ? [1511]
                    : !EventsNpcPolicy.IsRaceOpen(realmTime) ? [1222]
                    : races >= EventsNpcPolicy.RacesPerDay ? [1113]
                    : [1006, 1005];
                await SendEventsNpcReplyAsync(npc, dialogIndex, reply, "TrainerJoin", cancellationToken);
                return true;
            }
            case 1005 when trainer == 1:
                await StartRaceAsync(npc, dialogIndex, now, realmTime, cancellationToken);
                return true;
            case 1001 or 1201:
            {
                // Index 2: the trainer's sum, but only for the manual this trainer takes.
                var manual = 0;
                for (var number = EventsNpcPolicy.ManualCount; number >= 1 && manual == 0; number--)
                {
                    if (EventsNpcPolicy.TrainerFor(character.Camp, number) == trainer &&
                        CountBagItem(EventsNpcPolicy.Manual(character.Camp, number)) > 0)
                    {
                        manual = number;
                    }
                }

                if (!EventsNpcPolicy.IsRaceOpen(realmTime) || manual == 0)
                {
                    await SendEventsNpcReplyAsync(npc, dialogIndex, [EventsNpcPolicy.IsRaceOpen(realmTime) ? 1400 : 1222],
                        "TrainerNoManual", cancellationToken);
                    return true;
                }

                var left = Random.Shared.Next(1, 10);
                var right = Random.Shared.Next(1, 10);
                _eventsNpcForm = new EventsNpcForm(npc.InteractionId, EventsNpcPolicy.HealthFunction, selection, manual,
                    left, right, now.AddMinutes(5));
                await SendEventsNpcReplyAsync(npc, dialogIndex,
                    [1281, 1250 + manual, left * 10000 + 50, right * 10000 + 60], "TrainerQuestion", cancellationToken);
                return true;
            }
            case 1003 when trainer == 1:
                // Weekly top-ten B-Gold and titles are a wiki-only rule (not in the IGG guide): not built.
                await SendEventsNpcReplyAsync(npc, dialogIndex, [1108], "TrainerWeekly", cancellationToken);
                return true;
            case 1004 when trainer == 1:
            {
                var weekStart = _realmCalendar.GetStartOfDay(
                    Godswar.Server.Application.Realms.RealmCalendar.GetWeekStart(EventsNpcDay(now)));
                var best = EventsNpcRuntime.Store is { } store
                    ? await store.BestRaceSinceAsync(weekStart, cancellationToken)
                    : null;
                // Index 2: SubID % 100 == 11 draws "best individual score for the week" and (SubID-11)/100000 s.
                int[] reply = best is { } ms && ms < 20_000_000 ? [checked((int)(ms * 100 + 11))] : [1105];
                await SendEventsNpcReplyAsync(npc, dialogIndex, reply, "TrainerBest", cancellationToken);
                return true;
            }
            case 1218:
                await SendEventsNpcReplyAsync(npc, dialogIndex, [1006], "TrainerManualInfo", cancellationToken);
                return true;
            default:
                return false;
        }
    }

    private async Task StartRaceAsync(NpcSpawnDefinition npc, int dialogIndex, DateTimeOffset now,
        DateTimeOffset realmTime, CancellationToken cancellationToken)
    {
        var camp = _character!.Camp;
        if (_character.Level < EventsNpcPolicy.MinimumMarathonLevel || !EventsNpcPolicy.IsRaceOpen(realmTime))
        {
            await SendEventsNpcReplyAsync(npc, dialogIndex, [1600], "RaceClosed", cancellationToken);
            return;
        }

        var outcome = await RunEventsNpcAsync(async tx =>
        {
            if (tx.State.Races >= EventsNpcPolicy.RacesPerDay)
            {
                return RaceOutcome.Limit;
            }

            // A new race replaces any manual of an unfinished one.
            for (var number = 1; number <= EventsNpcPolicy.ManualCount; number++)
            {
                var manual = EventsNpcPolicy.Manual(camp, number);
                await tx.ConsumeAsync(manual, await tx.CountAsync(manual));
            }

            if (!await tx.GrantAsync(EventsNpcPolicy.Manual(camp, 1), 1))
            {
                tx.Rollback();
                return RaceOutcome.BagFull;
            }

            tx.State = tx.State with { RaceStartedAt = now };
            return RaceOutcome.Exchanged;
        }, RaceOutcome.NoManual, cancellationToken);

        // Index 3: H1401 claimed Manual 1, H1104 six races, H1312 bag full.
        int[] reply = outcome switch
        {
            RaceOutcome.Exchanged => [1401],
            RaceOutcome.Limit => [1104],
            RaceOutcome.BagFull => [1512],
            _ => [1600]
        };
        if (outcome == RaceOutcome.Exchanged)
        {
            await RefreshAfterEventsNpcAsync([(EventsNpcPolicy.Manual(camp, 1), 1)], cancellationToken);
        }

        await SendEventsNpcReplyAsync(npc, dialogIndex, reply, $"RaceStart{outcome}", cancellationToken);
    }

    private async Task SubmitHealthManualAsync(NpcSpawnDefinition npc, int dialogIndex, int trainer,
        EventsNpcForm form, int[] path, int amount, CancellationToken cancellationToken)
    {
        var camp = _character!.Camp;
        Console.WriteLine($"[eventsnpc] health answer npc={npc.NpcKey} manual={form.Manual} sum={form.Left}+" +
            $"{form.Right} typed={amount} path=[{string.Join(',', path)}] words:{_eventsNpcWords}");
        if (EventsNpcRates.MarathonChecksAnswer && amount != form.Left + form.Right)
        {
            await SendEventsNpcReplyAsync(npc, dialogIndex, [1280], "RaceWrongSum", cancellationToken);
            return;
        }

        var now = EventsNpcNow;
        var finishing = form.Manual == EventsNpcPolicy.ManualCount;
        var held = EventsNpcPolicy.Manual(camp, form.Manual);
        var next = finishing ? 0 : EventsNpcPolicy.Manual(camp, form.Manual + 1);
        var (outcome, bindingGold, milliseconds) = await RunEventsNpcAsync(async tx =>
        {
            if (EventsNpcPolicy.TrainerFor(camp, form.Manual) != trainer)
            {
                return (RaceOutcome.NoManual, 0, 0L);
            }

            if (finishing && tx.State.Races >= EventsNpcPolicy.RacesPerDay)
            {
                return (RaceOutcome.Limit, 0, 0L);
            }

            if (!await tx.ConsumeAsync(held, 1))
            {
                tx.Rollback();
                return (RaceOutcome.NoManual, 0, 0L);
            }

            if (!finishing)
            {
                if (!await tx.GrantAsync(next, 1))
                {
                    tx.Rollback();
                    return (RaceOutcome.BagFull, 0, 0L);
                }

                return (RaceOutcome.Exchanged, 0, 0L);
            }

            var gold = EventsNpcPolicy.RaceBindingGold(tx.State.Races);
            var elapsed = tx.State.RaceStartedAt is { } started && started <= now
                ? (long)(now - started).TotalMilliseconds
                : 0L;
            await tx.AddBindingGoldAsync(gold);
            await tx.RecordFinishAsync(now, elapsed, gold);
            tx.State = tx.State with { Races = tx.State.Races + 1, RaceStartedAt = null };
            return (RaceOutcome.Finished, gold, elapsed);
        }, (RaceOutcome.NoManual, 0, 0L), cancellationToken);

        int[] reply = outcome switch
        {
            RaceOutcome.Exchanged => [1401 + form.Manual],
            // H1112x (180) / H1112 (90), then the time as seconds (tail 1) and milliseconds (tail 2).
            RaceOutcome.Finished =>
            [
                bindingGold == EventsNpcPolicy.FirstRacesBindingGold ? 3292 : 3202,
                checked((int)Math.Min(milliseconds / 1000, 200_000)) * 10000 + 1,
                (int)(milliseconds % 1000) * 10000 + 2
            ],
            RaceOutcome.Limit => [1104],
            RaceOutcome.BagFull => [1512],
            _ => [1102]
        };
        if (outcome is RaceOutcome.Exchanged or RaceOutcome.Finished)
        {
            await RefreshAfterEventsNpcAsync(outcome == RaceOutcome.Exchanged ? [(next, 1)] : [], cancellationToken);
        }

        Console.WriteLine($"[eventsnpc] race character={_character.Name} trainer={trainer} manual={form.Manual} " +
            $"outcome={outcome} bgold={bindingGold} ms={milliseconds}");
        await SendEventsNpcReplyAsync(npc, dialogIndex, reply, $"Race{outcome}", cancellationToken);
    }

    private int CountBagItem(int itemId)
    {
        var total = 0;
        for (var slot = 0; slot < 96; slot++)
        {
            var item = State.KitBagSlots.GetItem(_character!.KitBag, slot);
            if (item.Id == (uint)itemId)
            {
                total += item.Stack;
            }
        }

        return total;
    }
}
