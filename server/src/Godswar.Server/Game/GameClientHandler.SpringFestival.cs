using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Seasons;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Rules = Godswar.Server.Application.Seasons.SpringFestivalRules;

namespace Godswar.Server.Game;

/// <summary>
/// ROUND3 W3 Spring Festival, only while the "spring_festival" season is open (public.seasonal_events, editor).
/// <list type="bullet">
/// <item>Spring Festival Envoy (Athens/Sparta_092, function 48, NpcFunNewYear.lua): the 2010 exchanges of the gold
/// plates, Preserved Hams, Rice Cakes, Chocolates and Perfect Roses, and the couple exchange (rules:
/// <see cref="SpringFestivalRules"/>). The single-plate page is a one-slot form; its submit is read from argument 6
/// (page*100 + slot) like the Thanksgiving Envoy's. UNPROVEN on this page: the submit is logged word by word.</item>
/// <item>Lunar Beast Slayer (Athens/Sparta_112, function 89, NpcFunFire.lua): the script has one page and no menu,
/// so opening the function turns in one Spring Festival Firecracker: 40 bound Exp Pills (client text), a GENERATED
/// lucky find, and the season's server-wide count in the second line.</item>
/// </list>
/// The Spring Merrymaker (Athens/Sparta_111) is left alone: its firecracker swap was quest 572/1572, which the client's
/// Quest.xml no longer carries, and the same NPC key is the General's Award giver. Firecrackers drop instead.
/// </summary>
internal sealed partial class GameClientHandler
{
    private static readonly ScriptedNpcDialogue LunarBeastSlayerDialogue = new(
        FunctionNumber: Rules.SlayerFunction,
        OpeningMenu: [],
        Steps: new Dictionary<int, int[]>());

    /// <summary>The single-plate form the envoy sent and is waiting to see submitted.</summary>
    private sealed record SpringPlateForm(uint NpcId, DateTimeOffset Expires);

    private SpringPlateForm? _springPlateForm;

    private static bool IsSpringFestivalNpc(NpcSpawnDefinition npc, string suffix) =>
        npc.NpcKey == "Athens" + suffix || npc.NpcKey == "Sparta" + suffix;

    private async Task<bool> TryHandleSpringFestivalAsync(NpcSpawnDefinition npc, int dialogIndex, GamePacket packet,
        CancellationToken cancellationToken)
    {
        var now = EventsNpcNow;
        if (_character is null || !SeasonalEvents.IsOpen(Rules.Season, now)) return false;
        var envoy = dialogIndex == Rules.EnvoyFunction && IsSpringFestivalNpc(npc, "_092");
        var slayer = dialogIndex == Rules.SlayerFunction && IsSpringFestivalNpc(npc, "_112");
        if (!envoy && !slayer) return false;
        // Read everything from the payload before the first await (it is a span).
        var click = ResolveNpcFunctionSelection(packet.Payload);
        var args = MountFeederArguments(packet);
        var words = EventsNpcWords(packet);
        try
        {
            if (slayer)
            {
                if (click >= 0)
                {
                    Console.WriteLine($"[spring] slayer click={click} ignored (one page) words:{words}");
                    return true;
                }

                await LightFirecrackerAsync(npc, now, cancellationToken);
                return true;
            }

            await HandleSpringEnvoyAsync(npc, now, click, args, words, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was committed (the transaction rolled back); the session stays.
            Console.Error.WriteLine($"[spring] fault npc={npc.NpcKey} click={click}: {error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    // ---- Spring Festival Envoy ------------------------------------------------------------------------------
    private async Task HandleSpringEnvoyAsync(NpcSpawnDefinition npc, DateTimeOffset now, int click, int[] args,
        string words, CancellationToken cancellationToken)
    {
        var pending = _springPlateForm is { } form && form.NpcId == npc.InteractionId && form.Expires >= now;
        var slot = SlotOf(args[6]);
        var isButton = click is >= 100 and <= 105 or >= 200 and <= 212;
        if (pending && click >= 0 && (slot >= 0 || !isButton))
        {
            _springPlateForm = null;
            Console.WriteLine($"[spring] plate submit character={_character!.Name} click={click} slot={slot} words:{words}");
            await SubmitSinglePlateAsync(npc, slot, cancellationToken);
            return;
        }

        _springPlateForm = null;
        if (click < 0)
        {
            await SendSpringReplyAsync(npc, Rules.EnvoyFunction, Rules.OpeningMenu, "SpringEnvoyOpen", cancellationToken);
            return;
        }

        if (Rules.TopicPage(click) is { } topic)
        {
            await SendSpringReplyAsync(npc, Rules.EnvoyFunction, topic, "SpringEnvoyTopic", cancellationToken);
            return;
        }

        if (click == 200)
        {
            _springPlateForm = new SpringPlateForm(npc.InteractionId, now.AddMinutes(5));
            await SendSpringReplyAsync(npc, Rules.EnvoyFunction, [Rules.PlateForm], "SpringEnvoyPlateForm", cancellationToken);
            return;
        }

        if (click is >= 201 and <= 212)
        {
            await RunSpringExchangeAsync(npc, click, cancellationToken);
            return;
        }

        Console.WriteLine($"[spring] envoy unknown click={click} character={_character!.Name} words:{words}");
    }

    private sealed record SpringOutcome(int Result, int Experience = 0, int PetExperience = 0, int ZodiacEnergy = 0,
        bool PlayerDetail = false);

    private async Task SubmitSinglePlateAsync(NpcSpawnDefinition npc, int slot, CancellationToken cancellationToken)
    {
        var level = _character!.Level;
        var counter = Rules.DailyCounter(200);
        var outcome = slot < 0
            ? new SpringOutcome(Rules.FormEmpty)
            : await RunEventsNpcAsync(async tx =>
            {
                if (await tx.ReadSlotAsync(slot) is not { } placed) return new SpringOutcome(Rules.FormEmpty);
                if (Array.IndexOf(Rules.Plates, placed.ItemId) < 0) return new SpringOutcome(Rules.FormNoItems);
                if (await tx.DailyUsedAsync(counter) >= Rules.Exchanges[200].DailyCap)
                    return new SpringOutcome(Rules.FormTomorrow);
                if (!await tx.ConsumeAsync(placed.ItemId, 1)) return new SpringOutcome(Rules.FormNoItems);
                var (experience, talent) = Rules.PlateReward(200, level);
                await tx.AddTalentPointsAsync(talent);
                await tx.DailyAddAsync(counter, 1);
                return new SpringOutcome(Rules.FormExchanged, experience);
            }, new SpringOutcome(Rules.FormNoItems), cancellationToken);
        await FinishSpringExchangeAsync(npc, 200, outcome, Rules.FormExchanged, cancellationToken);
    }

    private async Task RunSpringExchangeAsync(NpcSpawnDefinition npc, int button, CancellationToken cancellationToken)
    {
        var character = _character!;
        var exchange = Rules.Exchanges[button];
        var level = character.Level;
        var gender = character.Gender;
        var seasonDay = Rules.SeasonDay(EventsNpcNow);
        var hasPet = button == 202;
        var zodiacAvailable = _developerProgressionCommands is not null;
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            if (Rules.RequiredGender(button) is >= 0 and var required && gender != required)
                return new SpringOutcome(Rules.WrongGender);
            var couple = button is 211 or 212;
            if (couple)
            {
                if (await tx.ScalarAsync(
                        "SELECT used FROM public.finalise2_npc_daily WHERE character_id = @c AND day = @d AND counter = @k " +
                        "FOR UPDATE;", ("d", seasonDay), ("k", Rules.CoupleCounter)) is int made && made > 0)
                    return new SpringOutcome(Rules.AlreadyMade);
            }
            else if (await tx.DailyUsedAsync(Rules.DailyCounter(button)) >= exchange.DailyCap)
            {
                return new SpringOutcome(Rules.Tomorrow);
            }

            foreach (var (itemId, count) in exchange.Takes)
                if (await tx.CountAsync(itemId) < count) return new SpringOutcome(Rules.NoItems);
            if (button == 205 && !zodiacAvailable) return new SpringOutcome(Rules.NoItems); // nothing taken
            foreach (var (itemId, count) in exchange.Takes)
            {
                if (await tx.ConsumeAsync(itemId, count)) continue;
                tx.Rollback();
                return new SpringOutcome(Rules.NoItems);
            }

            var outcome = new SpringOutcome(Rules.Exchanged);
            switch (button)
            {
                case 201 or 202:
                {
                    var (experience, talent) = Rules.PlateReward(button, level);
                    await tx.AddTalentPointsAsync(talent);
                    outcome = outcome with
                    {
                        Experience = experience, PetExperience = hasPet ? Rules.SetCardPetExperience : 0
                    };
                    break;
                }
                case 203:
                    await tx.ScalarAsync("""
                        UPDATE public.character_base
                        SET medusa_honor_points = LEAST(COALESCE(medusa_honor_points, 0)::bigint + @n, 2147483647)::integer,
                            medusa_reward_revision = medusa_reward_revision + 1
                        WHERE id = @c;
                        """, ("n", Rules.HamHonor));
                    outcome = outcome with { PlayerDetail = true };
                    break;
                case 204:
                    await tx.ScalarAsync("""
                        UPDATE public.character_base
                        SET exchange_point = LEAST(COALESCE(exchange_point, 0)::bigint + @n, 2147483647)::integer,
                            wallet_revision = wallet_revision + 1
                        WHERE id = @c;
                        """, ("n", Rules.HamHardPoints));
                    outcome = outcome with { PlayerDetail = true };
                    break;
                case 205:
                    // Paid after the commit through the progression executor (the zodiac accrual owns the column).
                    outcome = outcome with { ZodiacEnergy = Rules.RiceZodiacEnergy };
                    break;
                case 211 or 212:
                {
                    var (experience, talent) = Rules.CoupleReward(level);
                    await tx.AddTalentPointsAsync(talent);
                    outcome = outcome with { Experience = experience };
                    break;
                }
                default:
                    if (Rules.ItemReward(button) is var item and > 0 && !await tx.GrantAsync(item, 1, bound: null))
                    {
                        tx.Rollback(); // a full bag must not eat the goods
                        return new SpringOutcome(Rules.BagFull);
                    }

                    break;
            }

            if (couple)
                await tx.ScalarAsync("""
                    INSERT INTO public.finalise2_npc_daily (character_id, day, counter, used) VALUES (@c, @d, @k, 1)
                    ON CONFLICT (character_id, day, counter) DO UPDATE SET used = finalise2_npc_daily.used + 1,
                        updated_at = now();
                    """, ("d", seasonDay), ("k", Rules.CoupleCounter));
            else
                await tx.DailyAddAsync(Rules.DailyCounter(button), 1);
            return outcome;
        }, new SpringOutcome(Rules.NoItems), cancellationToken);
        await FinishSpringExchangeAsync(npc, button, outcome, Rules.Exchanged, cancellationToken);
    }

    private async Task FinishSpringExchangeAsync(NpcSpawnDefinition npc, int button, SpringOutcome outcome, int success,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[spring] exchange character={_character!.Name} button={button} result={outcome.Result} " +
            $"exp={outcome.Experience} petExp={outcome.PetExperience} zodiac={outcome.ZodiacEnergy}");
        if (outcome.Result != success)
        {
            await SendSpringReplyAsync(npc, Rules.EnvoyFunction, [outcome.Result], "SpringEnvoyResult", cancellationToken);
            return;
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken);
        if (outcome.PlayerDetail && _character is { } refreshed && PacketBuilder.PlayerDetail(refreshed) is { Length: > 0 } detail)
            await _session.SendAsync(detail, cancellationToken, "SpringEnvoyPoints", framed: false);
        await SendSpringReplyAsync(npc, Rules.EnvoyFunction, [outcome.Result], "SpringEnvoyResult", cancellationToken);
        if (outcome.Experience > 0)
            await PayEventsNpcExperienceAsync(outcome.Experience, "SpringFestivalExchange", cancellationToken);
        if (outcome.PetExperience > 0)
            await PaySpringPetExperienceAsync(outcome.PetExperience, cancellationToken);
        if (outcome.ZodiacEnergy > 0 && !await AddSpringZodiacEnergyAsync(outcome.ZodiacEnergy, cancellationToken))
        {
            // The energy could not be stored (full or unavailable): the Rice Cakes go back.
            await RunEventsNpcAsync(async tx =>
            {
                await tx.DailyAddAsync(Rules.DailyCounter(205), -1); // the refused exchange does not count
                return await tx.GrantAsync(Rules.RiceCake, 10, bound: null);
            }, false, cancellationToken);
            await RefreshAfterEventsNpcAsync([], cancellationToken);
            await SendEventsNpcNoticeAsync("Your Stored Energy is full, so the Rice Cakes were returned.",
                "SpringZodiacRefund", cancellationToken);
        }
    }

    /// <summary>The set + card exchange's pet EXP, to the summoned pet (none summoned = forfeited, as the farm's).</summary>
    private async Task PaySpringPetExperienceAsync(int experience, CancellationToken cancellationToken)
    {
        if (_character is null || _account is null) return;
        try
        {
            var pet = await _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(_account.Id, _character.Id,
                Guid.NewGuid(), experience, cancellationToken);
            if (pet is { HasPetProjection: true })
                await _session.SendAsync(PacketBuilder.PetExperience(pet.PetId!.Value, pet.TotalExperience!.Value),
                    cancellationToken, "SpringFestivalPetExperience");
            Console.WriteLine($"[spring] pet experience character={_character.Name} awarded={pet?.AwardedExperience ?? 0}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[spring] pet experience failed character={_character.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stored Energy through the same durable path as the GM command (/zodiac add), so the next accrual settlement
    /// cannot overwrite it. False when it was not stored.
    /// </summary>
    private async Task<bool> AddSpringZodiacEnergyAsync(int amount, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || _developerProgressionCommands is null) return false;
        if (!TryCaptureCurrentPlayerOwnership(out var ownership)) return false;
        var request = new DeveloperProgressionCommandRequest(new CommandSubject(_account.Id, _character.Id),
            _processRealmId, ownership,
            new DeveloperProgressionCommand(DeveloperProgressionOperation.AddZodiacEnergy, amount));
        DeveloperProgressionMutationResult result;
        try
        {
            result = await _registry.ExecuteDeveloperProgressionAsync(_session, _developerProgressionCommands, request,
                _character, cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            return false;
        }

        if (!result.IsSuccess || result.Previous is not { } previous || result.Current is not { } current)
        {
            Console.WriteLine($"[spring] zodiac energy refused character={_character.Name} status={result.Status}");
            if (result.Current is { } projection)
            {
                GameSessionRegistry.ApplyDeveloperProgressionProjection(_character, projection);
                await SendZodiacFullSyncAsync(cancellationToken);
            }

            return false;
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        var gainedX100 = checked((int)(((long)current.ZodiacEnergy * 100L + current.ZodiacEnergyRemainderX100) -
                                       ((long)previous.ZodiacEnergy * 100L + previous.ZodiacEnergyRemainderX100)));
        if (result.Changed)
            await _session.SendAsync(PacketBuilder.ZodiacEnergyIncrease(current.ZodiacEnergy, gainedX100),
                cancellationToken, "SpringZodiacEnergyGain");
        await SendZodiacFullSyncAsync(cancellationToken);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "SpringZodiacStatus");
        return true;
    }

    // ---- Lunar Beast Slayer ---------------------------------------------------------------------------------
    private sealed record FirecrackerOutcome(int Result, int Lit = 0, int BonusItem = 0, bool DroveOffBeast = false);

    private async Task LightFirecrackerAsync(NpcSpawnDefinition npc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pills = Math.Clamp(Finalise2Rates.Amount(Rules.ExpPillsPerFirecracker), 0, 99);
        var roll = Random.Shared.Next(10_000);
        var seasonDay = Rules.SeasonDay(now);
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.DailyUsedAsync(Rules.DailyFirecrackers) >= Rules.FirecrackersPerDay)
                return new FirecrackerOutcome(Rules.SlayerDailyLimit);
            if (await tx.CountAsync(Rules.Firecracker) < 1) return new FirecrackerOutcome(Rules.SlayerNoFirecracker);
            var used = Convert.ToInt32(await tx.ScalarAsync(
                "SELECT count(*) FROM public.character_items WHERE user_id = @c AND item_location = 1;"));
            if (96 - used < Rules.FreeSlotsNeeded) return new FirecrackerOutcome(Rules.SlayerBagFull);
            if (!await tx.ConsumeAsync(Rules.Firecracker, 1)) return new FirecrackerOutcome(Rules.SlayerNoFirecracker);
            var (bonus, item, count) = Rules.Bonus(roll);
            var bound = item is Rules.FlyingCarpet ? true : (bool?)null; // 2011: carpet bound, Yeti jade tradable
            if (!await tx.GrantAsync(Rules.ExpPill, pills, bound: true) ||
                item > 0 && !await tx.GrantAsync(item, count, bound))
            {
                tx.Rollback();
                return new FirecrackerOutcome(Rules.SlayerBagFull);
            }

            await tx.DailyAddAsync(Rules.DailyFirecrackers, 1);
            // The season's server-wide count: character 0, 1 January of the season year (one row, upserted).
            var lit = (int)(await tx.ScalarAsync("""
                INSERT INTO public.finalise2_npc_daily (character_id, day, counter, used) VALUES (0, @d, @k, 1)
                ON CONFLICT (character_id, day, counter) DO UPDATE SET used = finalise2_npc_daily.used + 1,
                    updated_at = now()
                RETURNING used;
                """, ("d", seasonDay), ("k", Rules.LitCounter)))!;
            var beast = Rules.DrivesOffLunarBeast(lit);
            if (beast && !await tx.GrantAsync(Rules.FlyingCarpet, 1, bound: true))
            {
                tx.Rollback();
                return new FirecrackerOutcome(Rules.SlayerBagFull);
            }

            return new FirecrackerOutcome(bonus, lit, item, beast);
        }, new FirecrackerOutcome(Rules.SlayerNoFirecracker), cancellationToken);
        Console.WriteLine($"[spring] firecracker character={_character!.Name} result={outcome.Result} lit={outcome.Lit} " +
            $"bonus={outcome.BonusItem} beast={outcome.DroveOffBeast}");
        if (outcome.Result is Rules.SlayerNoFirecracker or Rules.SlayerBagFull or Rules.SlayerDailyLimit)
        {
            await SendSpringReplyAsync(npc, Rules.SlayerFunction, [outcome.Result], "SpringSlayerRefused", cancellationToken);
            return;
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendSpringReplyAsync(npc, Rules.SlayerFunction, [outcome.Result, Rules.CountLine(outcome.Lit)],
            "SpringSlayerLit", cancellationToken);
        if (outcome.DroveOffBeast)
        {
            var everyone = _registry.ResolveChatRecipients(_session, camp: null, characterIds: null);
            var text = $"{_character.Name} lit the {outcome.Lit:N0}th Spring Festival Firecracker and drove the Lunar " +
                       "Beast away! A Flying Carpet is the reward. Happy Spring Festival!";
            if (ServerMessageRouting.ToTopBox) // the admin 2026-11-10: top box, not the chat box (shorter: 3 board lines)
                await _registry.PublishBoardMessageAsync($"{_character.Name} lit the {outcome.Lit:N0}th Firecracker and " +
                    "drove the Lunar Beast away! Reward: a Flying Carpet.", cancellationToken);
            else
                await _registry.DeliverChatAsync(everyone, recipient => PacketBuilder.ChatMessage(recipient.SenderObjectId,
                    EventsNpcNoticeChannelWord, text), "SpringLunarBeast", cancellationToken);
            Console.WriteLine($"[spring] lunar beast driven off by {_character.Name} at {outcome.Lit}");
        }
    }

    private Task SendSpringReplyAsync(NpcSpawnDefinition npc, int function, int[] reply, string reason,
        CancellationToken cancellationToken) =>
        SendEventsNpcReplyAsync(npc, function, reply, reason, cancellationToken);
}
