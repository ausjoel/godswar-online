using Godswar.Server.Application.Progression;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using S = Godswar.Server.Application.Seasons.HalloweenMayDaySeason;

namespace Godswar.Server.Game;

/// <summary>
/// W3 seasons halloween / may day: the Halloween Envoy (function 113, NpcFunHallo.lua) and the May Day Envoy (55,
/// NpcFunWorkday.lua, plus 56 NpcFunWorker.lua on the same actor), only while their season is open and their switch is
/// on (<see cref="Application.Seasons.HalloweenMayDaySeason"/>). The client draws a reply on the page whose Index equals
/// the replies since the window opened, so every click gets exactly one reply, and only result lines follow an
/// exchange (no pop-up, the admin's rule). Nothing here is sent at world entry: every frame answers a click.
/// <para>
/// Halloween: open [1,2,3,4] (or 105 under Lv20); page 2 suit 100/101/108, wand 102/104/108 (103 when no suit is worn:
/// the script draws Hallo101 there, so a plain chat line says what is missing), exchange [0,5,6,7,8,n*1000+1] or 201;
/// page 3 106/107/109. May Day 55: open [101,102]; page 2 submit 230/250/260/270/280, claim 230/410/420/430/440/480.
/// May Day 56 (asked for its entries = claim): 210/220/230/240/260 on page 1.
/// </para>
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleHalloweenMayDayAsync(NpcSpawnDefinition npc, int dialogIndex, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null) return false;
        var now = DateTimeOffset.UtcNow;
        var kind = dialogIndex switch
        {
            S.HalloweenEnvoyFunction when S.IsHalloweenEnvoy(npc.NpcKey) && S.HalloweenOpen(now) => 1,
            S.MayDayEnvoyFunction when S.IsMayDayEnvoy(npc.NpcKey) && S.MayDayOpen(now) => 2,
            S.FertiliserSupplierFunction when S.IsMayDayEnvoy(npc.NpcKey) && S.MayDayOpen(now) => 3,
            _ => 0
        };
        if (kind == 0) return false;

        // Read the payload before the first await (it is a span).
        var path = DialogPath(packet);
        var words = EventsNpcWords(packet);
        Console.WriteLine($"[halloween-mayday] npc={npc.NpcKey} function={dialogIndex} character={_character.Name} " +
            $"path=[{string.Join(',', path)}]{words}");
        try
        {
            switch (kind)
            {
                case 1:
                    await HalloweenEnvoyAsync(npc, path, cancellationToken);
                    break;
                case 2:
                    await MayDayEnvoyAsync(npc, path, now, cancellationToken);
                    break;
                default:
                    await FertiliserSupplierAsync(npc, now, cancellationToken);
                    break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An event fault must never end the session; the transaction did not commit.
            Console.Error.WriteLine($"[halloween-mayday] fault npc={npc.NpcKey}: {error.GetType().Name}: {error.Message}");
        }

        return true;
    }

    private Task HalloweenMayDayReplyAsync(NpcSpawnDefinition npc, int function, int[] reply, string reason,
        CancellationToken token) => SendEventsNpcReplyAsync(npc, function, reply, reason, token);

    // ---- Halloween Envoy, function 113 ------------------------------------------------------------------------------

    private async Task HalloweenEnvoyAsync(NpcSpawnDefinition npc, int[] path, CancellationToken token)
    {
        const int f = S.HalloweenEnvoyFunction;
        var character = _character!;
        if (path.Length == 0)
        {
            await HalloweenMayDayReplyAsync(npc, f,
                character.Level < S.HalloweenMinimumLevel ? [105] : [1, 2, 3, 4], "HalloweenOpen", token);
            return;
        }

        if (character.Level < S.HalloweenMinimumLevel)
        {
            Console.WriteLine($"[halloween-mayday] below Lv{S.HalloweenMinimumLevel} click ignored");
            return;
        }

        if (path.Length == 1)
        {
            switch (path[0])
            {
                case 2:
                    await HalloweenSuitAsync(npc, token);
                    return;
                case 3:
                    await HalloweenWandAsync(npc, token);
                    return;
                case 4:
                {
                    var used = await RunEventsNpcAsync(tx => tx.DailyUsedAsync("halloween_candy"), S.CandyExchangesPerDay,
                        token);
                    await HalloweenMayDayReplyAsync(npc, f, S.ExchangeMenu(used), "HalloweenExchangeMenu", token);
                    return;
                }
            }
        }
        else if (path[0] == 4 && path[^1] is >= 5 and <= 8)
        {
            await HalloweenCandyExchangeAsync(npc, (S.CandyPrize)path[^1], token);
            return;
        }

        Console.WriteLine($"[halloween-mayday] halloween click not answered path=[{string.Join(',', path)}]");
    }

    private async Task HalloweenSuitAsync(NpcSpawnDefinition npc, CancellationToken token)
    {
        var year = S.SeasonYear(EventsNpcNow);
        var result = await RunEventsNpcAsync(async tx =>
        {
            // "Each player is limited to receiving one Halloween suit" (Hallo1), "on this day each year" (the NPC text):
            // one per character and season year.
            if (await tx.ScalarAsync(
                    "INSERT INTO public.halloween_suit_claims (character_id, season_year) VALUES (@c, @y) " +
                    "ON CONFLICT DO NOTHING RETURNING 1;", ("y", year)) is null)
                return 101;
            if (await tx.GrantAsync(S.HalloweenSuit, 1, bound: null)) return 100;
            tx.Rollback();
            return 108;
        }, 108, token);
        Console.WriteLine($"[halloween-mayday] suit character={_character!.Name} year={year} result={result}");
        if (result == 100) await RefreshAfterEventsNpcAsync([], token);
        await HalloweenMayDayReplyAsync(npc, S.HalloweenEnvoyFunction, [result], "HalloweenSuit", token);
    }

    private async Task HalloweenWandAsync(NpcSpawnDefinition npc, CancellationToken token)
    {
        var character = _character!;
        if (!S.WearsHalloweenSuit(character.Equipment))
        {
            // 103 means "put on the suit first" but the script draws Hallo101's text there; the chat line says it.
            await HalloweenMayDayReplyAsync(npc, S.HalloweenEnvoyFunction, [103], "HalloweenWandNoSuit", token);
            await SendEventsNpcNoticeAsync("Put on your Halloween suit first, then come back for the Halloween wand.",
                "HalloweenWandNoSuit", token);
            return;
        }

        var result = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.DailyUsedAsync("halloween_wand") >= 1) return 104;
            if (!await tx.GrantAsync(S.HalloweenWand, 1, bound: true))
            {
                tx.Rollback();
                return 108;
            }

            await tx.DailyAddAsync("halloween_wand", 1);
            return 102;
        }, 108, token);
        Console.WriteLine($"[halloween-mayday] wand character={character.Name} result={result}");
        if (result == 102) await RefreshAfterEventsNpcAsync([], token);
        await HalloweenMayDayReplyAsync(npc, S.HalloweenEnvoyFunction, [result], "HalloweenWand", token);
    }

    private async Task HalloweenCandyExchangeAsync(NpcSpawnDefinition npc, S.CandyPrize prize, CancellationToken token)
    {
        const int f = S.HalloweenEnvoyFunction;
        var character = _character!;
        if (prize == S.CandyPrize.PetExperience && !HasSummonedPet())
        {
            await HalloweenMayDayReplyAsync(npc, f, [109], "HalloweenCandyNoPet", token);
            return;
        }

        var amount = S.CandyPrizeAmount(prize, character.Level);
        if (prize == S.CandyPrize.ZodiacEnergy)
        {
            // GENERATED: energy above the storage limit is cut; a full store pays the EXP prize instead.
            var room = Math.Max(0L, (long)ZodiacEnergyCatalog.GetStorageLimit(character.ZodiacLevel) -
                character.ZodiacEnergy - (character.ZodiacEnergyRemainderX100 > 0 ? 1 : 0));
            amount = Math.Min(amount, room);
            if (amount <= 0)
                (prize, amount) = (S.CandyPrize.Experience, S.CandyPrizeAmount(S.CandyPrize.Experience, character.Level));
        }

        var result = await RunEventsNpcAsync(async tx =>
        {
            // The menu checks the daily cap (201); a second window opened before this one could still overrun it.
            if (await tx.DailyUsedAsync("halloween_candy") >= S.CandyExchangesPerDay) return 107;
            if (!await tx.ConsumeAsync(S.PumpkinCandy, S.CandyPerExchange)) return 107;
            // Talent Points are paid in the same transaction as the candy (like the Christmas / Easter envoys).
            if (prize == S.CandyPrize.TalentPoints) await tx.AddTalentPointsAsync(checked((int)amount));
            await tx.DailyAddAsync("halloween_candy", 1);
            return 106;
        }, 107, token);
        Console.WriteLine($"[halloween-mayday] candy character={character.Name} prize={prize} amount={amount} " +
            $"result={result}");
        if (result != 106)
        {
            await HalloweenMayDayReplyAsync(npc, f, [107], "HalloweenCandy", token);
            return;
        }

        await RefreshAfterEventsNpcAsync([], token);
        await HalloweenMayDayReplyAsync(npc, f, [106], "HalloweenCandy", token);
        await PayHalloweenPrizeAsync(prize, amount, token);
    }

    private async Task PayHalloweenPrizeAsync(S.CandyPrize prize, long amount, CancellationToken token)
    {
        if (amount <= 0) return;
        switch (prize)
        {
            case S.CandyPrize.Experience:
                await PayEventsNpcExperienceAsync(amount, "HalloweenCandy", token);
                break;
            case S.CandyPrize.TalentPoints:
                // Already added with the candy; the reload above carries it, this frame shows the gain.
                await _session.SendAsync(PacketBuilder.TalentPointGain(checked((int)amount)), token, "HalloweenTalent");
                break;
            case S.CandyPrize.ZodiacEnergy:
                // The shared progression path (the offline prize's): storage limit, 10xxx frames and Zodiac sync. If it
                // refuses, the candy is not lost: the EXP prize is paid instead (GENERATED).
                if (!await AddOfflinePrizeProgressionAsync(DeveloperProgressionOperation.AddZodiacEnergy,
                        checked((int)amount), token))
                    await PayEventsNpcExperienceAsync(
                        S.CandyPrizeAmount(S.CandyPrize.Experience, _character!.Level), "HalloweenCandyZodiacFallback", token);
                break;
            case S.CandyPrize.PetExperience:
                try
                {
                    var pet = await _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(_account!.Id, _character!.Id,
                        Guid.NewGuid(), checked((int)amount), token);
                    if (pet is { HasPetProjection: true })
                        await _session.SendAsync(PacketBuilder.PetExperience(pet.PetId!.Value, pet.TotalExperience!.Value),
                            token, "HalloweenPetExperience");
                    else
                        Console.WriteLine($"[halloween-mayday] pet experience not applied status={pet?.Status}");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[halloween-mayday] pet experience failed: {error.Message}");
                }

                break;
        }
    }
}
