using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// KELSIS (F-15, 2026-11-09): Delighted Kelsis' lowest-stat dog contest on Lelantine Farm. Rules, words and sources:
// Domain/World/Content/KelsisContestPolicy.cs and server/docs/kelsis-contest/README.md. Hot switch npc.kelsis_contest,
// default 0 because a submitted dog is taken from the player (the row is snapshotted first, so it can be restored).
// Every request is logged with all its words ("[kelsis] words ...") so the first rig session confirms the click path.
internal sealed partial class GameClientHandler
{
    private const string KelsisTable = "public.farm_kelsis_submissions";

    // Rows that hold a value in the round: taken dogs, and a submission still in flight (a crash between the two
    // transactions leaves 'pending'; it stops counting after two minutes).
    private const string KelsisLiveRows =
        "(status = 'taken' OR (status = 'pending' AND submitted_at > now() - interval '2 minutes'))";

    private static readonly ScriptedNpcDialogue KelsisDialogue = new(
        FunctionNumber: KelsisContestPolicy.FunctionNumber,
        OpeningMenu: KelsisContestPolicy.OpeningMenu,
        Steps: new Dictionary<int, int[]>());

    private static bool KelsisContestOn => Finalise2Rates.On(Finalise2RateKeys.KelsisContest);

    private async Task<bool> TryHandleKelsisContestAsync(NpcSpawnDefinition npc, int dialogIndex, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (npc.NpcKey != KelsisContestPolicy.NpcKey || dialogIndex != KelsisContestPolicy.FunctionNumber ||
            _character is not { } character)
        {
            return false;
        }

        var path = DialogPath(packet);
        var click = KelsisContestPolicy.Classify(path);
        Console.WriteLine($"[kelsis] words character={character.Name} npc={npc.InteractionId} dialog={dialogIndex} " +
            $"len={packet.Length} click={click} path=[{string.Join(',', path)}]{EventsNpcWords(packet)}");
        if (!KelsisContestOn)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        // UTC: Npgsql writes only offset-0 DateTimeOffset values to timestamptz.
        var roundStart = LelantineFarmRoundPolicy.LatestWindow(now, _realmCalendar).Start.ToUniversalTime();
        var stat = KelsisContestPolicy.StatForRound(roundStart);
        try
        {
            switch (click)
            {
                case KelsisClick.PageRequest:
                    await SendKelsisReplyAsync(npc, KelsisContestPolicy.OpeningMenu, "KelsisMenu", cancellationToken);
                    return true;
                case KelsisClick.Ask:
                    await SendKelsisReplyAsync(npc, KelsisContestPolicy.RequestReply(stat), "KelsisRequest",
                        cancellationToken);
                    return true;
                case KelsisClick.Give:
                    await SendKelsisReplyAsync(npc, await BuildKelsisSubmitPageAsync(roundStart, stat, cancellationToken),
                        "KelsisSubmitPage", cancellationToken);
                    return true;
                case KelsisClick.Submit:
                    await SendKelsisReplyAsync(npc, [await SubmitKelsisDogAsync(now, roundStart, stat, cancellationToken)],
                        "KelsisSubmit", cancellationToken);
                    return true;
                default:
                    // Not a button this window draws: nothing is sent, the words above show what arrived.
                    Console.WriteLine($"[kelsis] unregistered click character={character.Name} path=[{string.Join(',', path)}]");
                    return true;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[kelsis] fault character={character.Name} click={click}: " +
                $"{error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    private Task SendKelsisReplyAsync(NpcSpawnDefinition npc, int[] reply, string reason,
        CancellationToken cancellationToken) =>
        SendEventsNpcReplyAsync(npc, KelsisContestPolicy.FunctionNumber, reply, reason, cancellationToken);

    /// <summary>The summoned Loyal Dog's value for <paramref name="stat"/>, from the loaded pet state.</summary>
    private decimal? SummonedDogValue(int stat)
    {
        var pet = _characterLoadSnapshot?.Pets.SingleOrDefault(static p => p.IsCarried && p.IsSummoned);
        if (pet is null || pet.SpeciesId != KelsisContestPolicy.DogSpecies)
        {
            return null;
        }

        var code = KelsisContestPolicy.StatCode(stat);
        return code == 0
            ? pet.Rank
            : pet.StatValues.FirstOrDefault(value => value.StatCode == code)?.InitialSavvy;
    }

    private async Task<int[]> BuildKelsisSubmitPageAsync(DateTimeOffset roundStart, int stat,
        CancellationToken cancellationToken)
    {
        var (best, mine) = await RunEventsNpcAsync(async tx =>
        {
            var lowest = await tx.ScalarAsync(
                $"SELECT min(value) FROM {KelsisTable} WHERE round_start = @r AND {KelsisLiveRows};", ("r", roundStart));
            var own = await tx.ScalarAsync(
                $"SELECT min(value) FROM {KelsisTable} WHERE round_start = @r AND character_id = @c AND {KelsisLiveRows};",
                ("r", roundStart));
            return (lowest as decimal?, own as decimal?);
        }, ((decimal?)null, (decimal?)null), cancellationToken);
        bool? mineIsBest = mine is { } value ? value == best : null;
        return KelsisContestPolicy.SubmitPageReply(stat, best is not null, mineIsBest, mine ?? SummonedDogValue(stat));
    }

    /// <summary>
    /// Takes the summoned Loyal Dog when its stat is lower than every dog already given this round. Order: one
    /// transaction checks the dog and the rule, snapshots the pet rows into farm_kelsis_submissions (pending) and
    /// hands over the Morning Dew; then the pet goes through the same durable discard as the Pet window's Abandon
    /// (pet list, companion model and owner stats update); then the row is marked taken. If the discard fails the
    /// row is marked aborted and the dew taken back.
    /// </summary>
    private async Task<int> SubmitKelsisDogAsync(DateTimeOffset now, DateTimeOffset roundStart, int stat,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!LelantineFarmRoundPolicy.IsOpen(now, _realmCalendar))
        {
            Console.WriteLine($"[kelsis] submit refused character={character.Name} reason=round-closed");
            return KelsisContestPolicy.NotEligible;
        }

        var pet = _characterLoadSnapshot?.Pets.SingleOrDefault(static p => p.IsCarried && p.IsSummoned);
        if (pet is null)
        {
            return KelsisContestPolicy.NotSummoned;
        }

        if (pet.SpeciesId != KelsisContestPolicy.DogSpecies)
        {
            return KelsisContestPolicy.NotADog;
        }

        // The durable discard refuses every pet while any pet of the character is merged into its owner.
        if (pet.ActivityState != "owned" ||
            _characterLoadSnapshot!.Pets.Any(static p => p.ContributesToCharacter))
        {
            Console.WriteLine($"[kelsis] submit refused character={character.Name} pet={pet.PetId} reason=merged-or-away");
            return KelsisContestPolicy.NotEligible;
        }

        var code = KelsisContestPolicy.StatCode(stat);
        decimal value = 0m;
        decimal? best = null;
        long submissionId = 0;
        var result = await RunEventsNpcAsync(async tx =>
        {
            // One submission at a time server-wide, so two dogs cannot both pass the lowest-only rule.
            await tx.ScalarAsync("SELECT pg_advisory_xact_lock(hashtext('farm_kelsis_submissions'));");
            var read = code == 0
                ? await tx.ScalarAsync(
                    """
                    SELECT rank FROM public.character_pets
                    WHERE id = @p AND user_id = @c AND species_id = @dog AND is_summoned AND activity_state = 'owned'
                      AND NOT contributes_to_character
                    FOR UPDATE;
                    """, ("p", pet.PetId), ("dog", KelsisContestPolicy.DogSpecies))
                : await tx.ScalarAsync(
                    """
                    SELECT s.initial_savvy FROM public.character_pets p
                    JOIN public.character_pet_stat_values s ON s.pet_id = p.id AND s.stat_code = @code
                    WHERE p.id = @p AND p.user_id = @c AND p.species_id = @dog AND p.is_summoned
                      AND p.activity_state = 'owned' AND NOT p.contributes_to_character
                    FOR UPDATE OF p;
                    """, ("p", pet.PetId), ("dog", KelsisContestPolicy.DogSpecies), ("code", code));
            if (read is not decimal petValue)
            {
                return KelsisContestPolicy.NotSummoned;
            }

            value = petValue;
            if (await tx.ScalarAsync("SELECT EXISTS (SELECT 1 FROM public.sealed_pet_items WHERE pet_id = @p);",
                    ("p", pet.PetId)) is true)
            {
                return KelsisContestPolicy.NotEligible;
            }

            best = await tx.ScalarAsync(
                $"SELECT min(value) FROM {KelsisTable} WHERE round_start = @r AND {KelsisLiveRows};",
                ("r", roundStart)) as decimal?;
            if (!KelsisContestPolicy.Accepts(value, best))
            {
                return KelsisContestPolicy.NotEligible;
            }

            if (!await tx.GrantAsync(KelsisContestPolicy.MorningDewItem, 1, bound: true))
            {
                tx.Rollback();
                return KelsisContestPolicy.BagFull;
            }

            submissionId = Convert.ToInt64(await tx.ScalarAsync(
                $"""
                INSERT INTO {KelsisTable} (round_start, stat, character_id, pet_id, value, status, pet_snapshot)
                SELECT @r, @stat, @c, p.id, @v, 'pending', jsonb_build_object(
                    'pet', to_jsonb(p),
                    'stats', COALESCE((SELECT jsonb_agg(to_jsonb(s)) FROM public.character_pet_stat_values s
                        WHERE s.pet_id = p.id), '[]'::jsonb),
                    'bonuses', COALESCE((SELECT jsonb_agg(to_jsonb(b)) FROM public.character_pet_character_bonuses b
                        WHERE b.pet_id = p.id), '[]'::jsonb),
                    'skills', COALESCE((SELECT jsonb_agg(to_jsonb(k)) FROM public.character_pet_skills k
                        WHERE k.pet_id = p.id), '[]'::jsonb))
                FROM public.character_pets p WHERE p.id = @p AND p.user_id = @c
                RETURNING id;
                """, ("r", roundStart), ("stat", (short)stat), ("v", value), ("p", pet.PetId)));
            return KelsisContestPolicy.Submitted;
        }, KelsisContestPolicy.NotEligible, cancellationToken);
        Console.WriteLine($"[kelsis] submit check character={character.Name} pet={pet.PetId} round={roundStart:u} " +
            $"stat={stat}({KelsisContestPolicy.StatNames[stat - 1]}) value={value} best={best?.ToString() ?? "none"} " +
            $"result={result} submission={submissionId}");
        if (result != KelsisContestPolicy.Submitted)
        {
            return result;
        }

        // The same durable discard as the Pet window's Abandon, with the server-session identity the login call-out
        // uses. Abandon's projection also repaints the owner for players nearby (existing behaviour of that click).
        PetDurableReceipt? receipt = null;
        try
        {
            receipt = await HandleDurablePetPresenceAsync(
                PetCommandOperationIdentity.ServerSessionLifecycle(Guid.NewGuid(), _commandConnectionId),
                pet.PetId, PetPresenceOperation.Delete, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[kelsis] discard fault character={character.Name} pet={pet.PetId}: {error.Message}");
        }

        var taken = receipt is { Succeeded: true };
        await RunEventsNpcAsync(async tx =>
        {
            await tx.ScalarAsync($"UPDATE {KelsisTable} SET status = @s WHERE id = @id;",
                ("s", taken ? "taken" : "aborted"), ("id", submissionId));
            if (!taken)
            {
                await tx.ConsumeAsync(KelsisContestPolicy.MorningDewItem, 1);
            }

            return true;
        }, false, CancellationToken.None);
        await RefreshAfterEventsNpcAsync(taken ? [(KelsisContestPolicy.MorningDewItem, 1)] : [], cancellationToken);
        Console.WriteLine($"[kelsis] submit {(taken ? "taken" : "aborted")} character={character.Name} pet={pet.PetId} " +
            $"submission={submissionId} receipt={receipt?.Status.ToString() ?? "none"}");
        return taken ? KelsisContestPolicy.Submitted : KelsisContestPolicy.NotEligible;
    }

    /// <summary>
    /// Battlefield Awarder page 3 entry 73 "*Special Reward: Kelsis' Pet": 208 pays the 2,300 B-Gold to the owner of
    /// a settled round's winning dog (lowest value, earliest on a tie), once; 204 already paid; 219 the player's dog
    /// was not the final choice; 207 no dog given (or the switch is off). Only settled rounds count; rounds older
    /// than the Awarder's 32-day look-back are ignored (GENERATED: the client's 23:50 same-day deadline is not used).
    /// </summary>
    private async Task<int[]> ClaimKelsisPrizeAtAwarderAsync(CancellationToken cancellationToken)
    {
        if (!KelsisContestOn || _character is not { } character)
        {
            return [BattlefieldAwarderReplies.FarmNoData];
        }

        var now = DateTimeOffset.UtcNow;
        var (latestStart, _) = LelantineFarmRoundPolicy.LatestWindow(now, _realmCalendar);
        // A round still running is not settled; when no round is running the latest one is.
        var cut = (LelantineFarmRoundPolicy.IsOpen(now, _realmCalendar) ? latestStart : latestStart.AddMicroseconds(1))
            .ToUniversalTime();
        var since = now - BattlefieldRewardLookBack;
        var result = await RunEventsNpcAsync(async tx =>
        {
            const string winnerOf =
                "(SELECT w.id FROM " + KelsisTable + " w WHERE w.round_start = s.round_start AND w.status = 'taken' " +
                "ORDER BY w.value, w.id LIMIT 1)";
            var unpaid = await tx.ScalarAsync(
                $"""
                SELECT s.id FROM {KelsisTable} s
                WHERE s.character_id = @c AND s.status = 'taken' AND s.round_start < @cut AND s.round_start >= @since
                  AND s.prize_claimed_at IS NULL AND s.id = {winnerOf}
                ORDER BY s.round_start LIMIT 1 FOR UPDATE;
                """, ("cut", cut), ("since", since));
            if (unpaid is long id)
            {
                await tx.ScalarAsync($"UPDATE {KelsisTable} SET prize_claimed_at = now() WHERE id = @id;", ("id", id));
                await tx.AddBindingGoldAsync(KelsisContestPolicy.PrizeBindingGold);
                return KelsisContestPolicy.AwarderWon;
            }

            var newest = await tx.ScalarAsync(
                $"""
                SELECT CASE WHEN count(*) = 0 THEN NULL WHEN bool_or(s.id = {winnerOf}) THEN 1 ELSE 0 END
                FROM {KelsisTable} s
                WHERE s.character_id = @c AND s.status = 'taken' AND s.round_start = (
                    SELECT max(m.round_start) FROM {KelsisTable} m WHERE m.character_id = @c AND m.status = 'taken'
                      AND m.round_start < @cut AND m.round_start >= @since);
                """, ("cut", cut), ("since", since));
            return newest switch
            {
                1 => BattlefieldAwarderReplies.FarmAlreadyClaimed,
                0 => KelsisContestPolicy.AwarderLost,
                _ => BattlefieldAwarderReplies.FarmNoData
            };
        }, BattlefieldAwarderReplies.FarmNoData, cancellationToken);
        Console.WriteLine($"[kelsis] awarder character={character.Name} cut={cut:u} result={result}");
        if (result == KelsisContestPolicy.AwarderWon)
        {
            await RefreshAfterEventsNpcAsync([], cancellationToken);
        }

        return [result];
    }
}
