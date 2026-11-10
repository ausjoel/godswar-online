using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W3 Magic Bell (F-136, the admin 2026-11-09): the Quest Log's "Send Pet". The client has already checked the summoned pet,
/// its Quest Dispatch talent, the chosen quest and the bell (0x69d800) and sends C2S 10254 [u32 quest]. The server checks
/// the same again, takes one Magic Bell and stores the dispatch in public.pet_dispatch_state (runtime DDL) with its due
/// time: 10-70 minutes by pet rank (<see cref="PetQuestDispatchPolicy.DispatchMinutes(decimal)"/>, GENERATED formula).
/// The answer is S2C 10259 code 5. On code 5 the client puts the pet's model away (0x69ec50 -> recall branch), so the
/// call-out pair the pet-bind refresh uses follows at once: the admin's "the pet stays usable". When the time is up the
/// 15 s ticker asks this handler (operator sink, under the character gate) to finish the quest: the quest's normal
/// reward is paid (EXP/TP/Silver/Gold through the hand-in's rates and appraisal, its first reward item into the bag),
/// the quest leaves the log as on a Del (MSG_NPC_CANCELQUEST) and counts as today's completion. Switch
/// pet.magic_bell_dispatch.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>Quests whose reward item did not fit, already told this session (one line, not one per tick).</summary>
    private readonly HashSet<uint> _petDispatchBagFullTold = [];

    private async Task HandlePetQuestDispatchAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is not { } character) return;
        Console.WriteLine($"[pet-dispatch] request character={character.Name} len={packet.Length} hex={packet.ToHexPreview(16)}");
        if (!PetQuestDispatchPolicy.TryReadRequest(packet.Payload, out var questId)) return;

        async Task RefuseAsync(PetQuestDispatchPolicy.Result result, string why, string? line = null)
        {
            Console.WriteLine($"[pet-dispatch] refused character={character.Name} quest={questId} code={(byte)result} {why}");
            await _session.SendAsync(PetQuestDispatchPolicy.ResultPacket(questId, result), cancellationToken, "PetQuestDispatchRefused");
            if (line is not null) await SendInfoLineAsync(line, InfoTabOther, cancellationToken);
        }

        if (!Finalise2Rates.On(Finalise2RateKeys.MagicBell))
        {
            await RefuseAsync(PetQuestDispatchPolicy.Result.NotDispatchable, "switch off");
            return;
        }

        var pet = _characterLoadSnapshot?.Pets.FirstOrDefault(static p => p.IsCarried && p.IsSummoned);
        if (pet is null)
        {
            await RefuseAsync(PetQuestDispatchPolicy.Result.Failed, "no summoned pet", "Summon the pet you want to send first.");
            return;
        }
        if ((pet.TalentMask & PetQuestDispatchTalentBit) == 0)
        {
            await RefuseAsync(PetQuestDispatchPolicy.Result.Failed, "no Quest Dispatch talent",
                "Your pet doesn't have the Quest Dispatch talent.");
            return;
        }
        if (pet.ContributesToCharacter)
        {
            await RefuseAsync(PetQuestDispatchPolicy.Result.Failed, "pet merged with owner",
                "A pet merged with you cannot be sent on a quest.");
            return;
        }

        var step = StarterQuestChain.Find(questId);
        var carried = FindCarriedQuest(questId);
        if (step is not { } quest || carried is null || quest.Camp != ChainCampFor(character.Camp))
        {
            await RefuseAsync(PetQuestDispatchPolicy.Result.Failed, "quest not carried");
            return;
        }
        if (!PetQuestDispatchPolicy.IsDispatchable(quest))
        {
            await RefuseAsync(PetQuestDispatchPolicy.Result.NotDispatchable, $"sort={quest.UIQuestSort} level={quest.MinLevel}");
            return;
        }
        if (!HasDailyAllowance(character, quest, QuestDailyState.Today()) || AreQuestRequirementsSatisfied(character, carried))
        {
            await RefuseAsync(PetQuestDispatchPolicy.Result.AlreadyCompleted, "done today or objectives already met");
            return;
        }

        var minutes = PetQuestDispatchPolicy.DispatchMinutes(pet.Rank);
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            var busy = await tx.ScalarAsync(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM public.pet_dispatch_state WHERE character_id = @c AND quest_id = @q) " +
                "THEN 'quest' WHEN EXISTS (SELECT 1 FROM public.pet_dispatch_state WHERE character_id = @c AND pet_id = @p) " +
                "THEN 'pet' ELSE '' END;", ("q", (int)questId), ("p", pet.PetId)) as string;
            if (!string.IsNullOrEmpty(busy)) { tx.Rollback(); return busy; }
            if (!await tx.ConsumeAsync(PetQuestDispatchPolicy.MagicBellItemId, 1)) { tx.Rollback(); return "bell"; }
            await tx.ScalarAsync(
                "INSERT INTO public.pet_dispatch_state (character_id, quest_id, pet_id, pet_name, due_at) " +
                "VALUES (@c, @q, @p, @n, now() + make_interval(mins => @m)) RETURNING 1;",
                ("q", (int)questId), ("p", pet.PetId), ("n", pet.Name), ("m", minutes));
            return "ok";
        }, "unavailable", cancellationToken);

        switch (outcome)
        {
            case "quest":
                await RefuseAsync(PetQuestDispatchPolicy.Result.AlreadyDispatched, "quest already dispatched");
                return;
            case "pet":
                await RefuseAsync(PetQuestDispatchPolicy.Result.Failed, "pet already on a quest",
                    $"{pet.Name} is already on a quest. Each pet does one at a time.");
                return;
            case "bell":
                await RefuseAsync(PetQuestDispatchPolicy.Result.NoBell, "no Magic Bell");
                return;
            case not "ok":
                await RefuseAsync(PetQuestDispatchPolicy.Result.Failed, "store unavailable");
                return;
        }

        Console.WriteLine($"[pet-dispatch] sent character={character.Name} quest={questId} pet={pet.PetId} " +
                          $"rank={pet.Rank} minutes={minutes}");
        await RefreshAfterEventsNpcAsync([], cancellationToken); // the bell leaves the bag; no notice for a used item
        await _session.SendAsync(PetQuestDispatchPolicy.ResultPacket(questId, PetQuestDispatchPolicy.Result.Accepted),
            cancellationToken, "PetQuestDispatchAccepted");
        // Code 5 makes the client recall the pet's model locally; call it out again so it stays with the player.
        var petId = checked((uint)pet.PetId);
        await _session.SendAsync(PacketBuilder.PetOperationResult(petId, PetOperationResultCode.CallOutSucceeded),
            cancellationToken, "PetQuestDispatchCallOut");
        await _session.SendAsync(PacketBuilder.PetWorldPresence(petId, LocalPlayerObjectId),
            cancellationToken, "PetQuestDispatchWorldPresence");
        await SendInfoLineAsync($"{pet.Name} will be back with the quest done in {minutes} minutes.",
            InfoTabQuest, cancellationToken);
    }

    /// <summary>Talent bit of Quest Dispatch (PetTalentCatalog, client test 0x69d84c: pet +0x80 &amp; 2).</summary>
    private const short PetQuestDispatchTalentBit = 2;

    /// <summary>Run by the ticker (operator sink): finishes every dispatch of this character whose time is up.</summary>
    private async Task<string> CompleteDuePetDispatchesAsync(CancellationToken cancellationToken)
    {
        if (_character is not { } character || !Finalise2Rates.On(Finalise2RateKeys.MagicBell)) return "off";
        var due = await RunEventsNpcAsync(async tx => await tx.ScalarAsync(
            "SELECT string_agg(quest_id::text, ',' ORDER BY due_at) FROM public.pet_dispatch_state " +
            "WHERE character_id = @c AND due_at <= now();") as string, null, cancellationToken);
        var finished = 0;
        foreach (var part in (due ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(part, out var questId) &&
                await CompletePetDispatchAsync(character, questId, cancellationToken)) finished++;
        }
        return $"pet dispatches finished={finished}";
    }

    private async Task<bool> CompletePetDispatchAsync(GameCharacter character, uint questId, CancellationToken cancellationToken)
    {
        var step = StarterQuestChain.Find(questId);
        var carried = FindCarriedQuest(questId);
        var payable = step is { } s && carried is not null && s.Camp == ChainCampFor(character.Camp) &&
                      HasDailyAllowance(character, s, QuestDailyState.Today());
        var rewardItem = payable
            ? QuestRewardItemCatalog.Resolve(questId).OrderBy(static i => i.SlotIndex).Select(static i => i.ItemId)
                .FirstOrDefault()
            : 0u;

        // Claim the row (one payout however many triggers race) and put the reward item in the bag, in one transaction.
        var claim = await RunEventsNpcAsync(async tx =>
        {
            var petName = await tx.ScalarAsync(
                "DELETE FROM public.pet_dispatch_state WHERE character_id = @c AND quest_id = @q AND due_at <= now() " +
                "RETURNING pet_name;", ("q", (int)questId)) as string;
            if (petName is null) { tx.Rollback(); return (Claimed: false, PetName: "", BagFull: false); }
            if (rewardItem != 0 && !await tx.GrantAsync((int)rewardItem, 1, bound: null))
            {
                tx.Rollback();
                return (Claimed: false, PetName: petName, BagFull: true);
            }
            return (Claimed: true, PetName: petName, BagFull: false);
        }, (Claimed: false, PetName: "", BagFull: false), cancellationToken);

        var name = string.IsNullOrWhiteSpace(claim.PetName) ? "Your pet" : claim.PetName;
        if (claim.BagFull)
        {
            if (_petDispatchBagFullTold.Add(questId))
            {
                await SendInfoLineAsync($"{name} is back from quest {questId}: make room in your bag for the reward.",
                    InfoTabQuest, cancellationToken);
            }
            return false;
        }
        if (!claim.Claimed) return false;
        _petDispatchBagFullTold.Remove(questId);
        // The row is gone: the payout must finish even if the operator action's 10 s budget runs out now.
        cancellationToken = CancellationToken.None;

        if (!payable || step is not { } quest || carried is null)
        {
            Console.WriteLine($"[pet-dispatch] returned unpaid character={character.Name} quest={questId}");
            await SendInfoLineAsync($"{name} came back, but quest {questId} was no longer in your Quest Log (or was done today).",
                InfoTabQuest, cancellationToken);
            return true;
        }

        // The hand-in's money/EXP path: the GM override or the quest's own values, the quest rates, the appraisal.
        var rates = ServerRates.Current;
        var payout = QuestRewardContentCatalog.Current.Resolve(
            quest.QuestId, new QuestRewardPayout(quest.Experience, quest.TalentPoints, quest.Silver, quest.Gold));
        payout = new QuestRewardPayout(
            payout.Experience,
            rates.Multiply(ServerRateKeys.QuestTalent, payout.TalentPoints, QuestRateCap),
            rates.Multiply(ServerRateKeys.QuestSilver, payout.Silver, QuestRateCap),
            rates.Multiply(ServerRateKeys.QuestGold, payout.Gold, QuestRateCap));
        var experience = rates.Multiply(
            ServerRateKeys.QuestExperience,
            await ScaleQuestRewardExperienceAsync(payout.Experience, cancellationToken),
            QuestExperienceCap);
        CharacterProgressionResult? progression = null;
        try
        {
            progression = await _store.ApplyMonsterKillRewardAsync(
                _account?.Id ?? 0, character.Id, experience, payout.TalentPoints, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pet-dispatch] EXP/TP payment FAILED character={character.Name} quest={questId} " +
                                    $"exp={experience} tp={payout.TalentPoints} {ex.GetType().Name}: {ex.Message}");
        }
        var levelUps = progression?.LevelUps ?? [];
        if (progression is not null)
        {
            character.Level = progression.CurrentLevel;
            character.Experience = progression.CurrentExperience;
            character.TalentExperience = progression.CurrentTalentExperience;
            character.TalentPoints = progression.CurrentTalentPoints;
        }
        var paidSilver = 0;
        var paidGold = 0;
        try
        {
            var wallet = await _store.GrantQuestCurrencyAsync(
                _account?.Id ?? 0, character.Id, payout.Silver, payout.Gold, cancellationToken);
            if (wallet is not null)
            {
                character.Silver = wallet.Silver;
                character.Gold = wallet.Gold;
                paidSilver = payout.Silver;
                paidGold = payout.Gold;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pet-dispatch] currency FAILED character={character.Name} quest={questId} " +
                                    $"{ex.GetType().Name}: {ex.Message}");
        }

        character.Quests.Remove(carried);
        if (!character.QuestCompletedIds.Contains(quest.QuestId))
            character.QuestCompletedIds = [.. character.QuestCompletedIds, quest.QuestId];
        if (_offeredQuestId == quest.QuestId) _offeredQuestId = 0;
        var countedToday = RecordDailyCompletion(quest);
        await SaveQuestStateAsync(cancellationToken);
        if (countedToday) await SaveQuestDailyCompletionsAsync(cancellationToken);
        if (levelUps.Count > 0) await RefreshLevelUpStatsAsync(character, cancellationToken);
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "PetDispatchStatus");
        foreach (var levelUp in levelUps) // one 10030 per level, as on a hand-in (004AC835 without it)
        {
            var maximum = PlayerExperienceCatalog.GetClientExperienceMaximum(levelUp.Level, character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(LocalPlayerObjectId, levelUp.Level, maximum, levelUp.CurrentExperience,
                    character.MaxHp, character.CurrentHp, character.MaxMp, character.CurrentMp),
                cancellationToken, "PetDispatchLevelUp");
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerLevelUp(CurrentPlayerObjectId, levelUp.Level, maximum, levelUp.CurrentExperience,
                    character.MaxHp, character.CurrentHp, character.MaxMp, character.CurrentMp),
                cancellationToken, _session, "PetDispatchLevelUpWorld");
        }

        // The quest leaves the client's log as on a Del: MSG_NPC_CANCELQUEST {giver, responder, quest} (004E15DF).
        var giver = await ResolveQuestNpcIdForSnapshotAsync(quest.GiverKey, cancellationToken);
        var responder = await ResolveQuestNpcIdForSnapshotAsync(quest.ResponderKey, cancellationToken);
        await _session.SendAsync(
            PacketBuilder.QuestCancelAnswer(
                giver == LocalPlayerObjectId ? 0u : giver,
                responder == LocalPlayerObjectId ? 0u : responder,
                quest.QuestId),
            cancellationToken, "PetDispatchQuestRemoved", framed: false);
        if (levelUps.Count > 0) await SendQuestLevelUpRefreshAsync(cancellationToken);
        else await SendQuestRefreshAsync(quest.QuestId, cancellationToken);
        var reachedLevel = levelUps.Count > 0 ? character.Level : 0;
        // Last: this reloads the character from the database (everything above is already saved) and repaints the bag.
        if (rewardItem != 0) await RefreshAfterEventsNpcAsync([((int)rewardItem, 1)], cancellationToken);

        var items = rewardItem != 0 ? new List<(uint ItemId, int Quantity)> { (rewardItem, 1) } : null;
        await SendInfoLineAsync($"{name} completed quest {quest.QuestId} and came back to you.", InfoTabQuest, cancellationToken);
        await SendInfoLineAsync(QuestRewardLine(progression?.ExperienceGained ?? 0, payout.TalentPoints, paidSilver, paidGold,
            items, reachedLevel), InfoTabQuest, cancellationToken);
        Console.WriteLine($"[pet-dispatch] completed character={character.Name} quest={quest.QuestId} exp={experience} " +
                          $"tp={payout.TalentPoints} silver={paidSilver} gold={paidGold} item={rewardItem} " +
                          $"levelUps={levelUps.Count}");
        await OnEventsNpcQuestHandedInAsync(quest.QuestId, cancellationToken); // EVENTSNPC chains count it like a hand-in
        return true;
    }
}
