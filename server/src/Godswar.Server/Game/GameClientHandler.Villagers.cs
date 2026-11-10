using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Villagers;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// VILLAGERS (2026-11-03): Village Leader book re-issue and Festival Envoy weekend
// EXP claim + holiday gifts. Each is reached from a one-line hook
// (NpcDialogOpen.cs for the leader, NpcDialog.cs for the envoy) and does nothing
// unless its server_rates switch is on (Application/Villagers/VillagersRules.cs).
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Village Leader (Athens_057 Admes / Sparta_057 Stefanos): the stock client
    /// gives this NPC no function page (description and early quests only), so the
    /// books are handed over when a level-40+ character opens the NPC, and a line
    /// in the personal game log says so (no pop-up). Never blocks the open itself.
    /// </summary>
    private async Task TryReissueVillageLeaderBooksAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (!VillageLeaderRules.IsVillageLeader(npc.NpcKey) ||
            !VillagersSwitches.VillageLeaderBooksEnabled ||
            _account is null ||
            _character is not { } character ||
            character.Level < VillageLeaderRules.MinimumLevel ||
            _store is not IVillagersStore store)
        {
            return;
        }

        var books = VillageLeaderRules.BooksFor(character.Camp);
        if (books.Count == 0)
        {
            return;
        }

        try
        {
            var result = await store.ReissueVillageLeaderBooksAsync(
                _account.Id, character.Id, books, cancellationToken);
            switch (result.Status)
            {
                case VillageLeaderGrantStatus.Granted when result.Character is not null:
                    InstallUpdatedCharacter(result.Character);
                    _registry.UpdateCharacter(_session, _character!, advanceWorldRevision: false);
                    await SendKitBagRefreshAsync(cancellationToken);
                    foreach (var book in result.Granted)
                    {
                        await _session.SendAsync(
                            PacketBuilder.PersonalGameLog(
                                $"The Village Leader gives you {book.DisplayName}."),
                            cancellationToken,
                            "VillageLeaderBookLog");
                    }
                    Console.WriteLine(
                        $"[villagers] leader re-issued character={character.Name} " +
                        $"books={string.Join(',', result.Granted.Select(static b => b.BookItemId))}");
                    break;
                case VillageLeaderGrantStatus.InsufficientCapacity:
                    await _session.SendAsync(
                        PacketBuilder.PersonalGameLog(
                            "Village Leader: make room in your bag for the portal and riding books."),
                        cancellationToken,
                        "VillageLeaderBagFullLog");
                    Console.WriteLine(
                        $"[villagers] leader bag full character={character.Name}");
                    break;
                default:
                    Console.WriteLine(
                        $"[villagers] leader nothing to re-issue character={character.Name} " +
                        $"status={result.Status}");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[villagers] leader re-issue failed character={character.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Festival Envoy (Athens_084 / Sparta_084), function 28. Returns false, leaving
    /// the click to the code that answered it before, whenever the matching switch is
    /// off or the click is not one of the envoy's own buttons.
    /// </summary>
    private async Task<bool> TryHandleFestivalEnvoyActionAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (!FestivalEnvoyRules.IsFestivalEnvoy(npc.NpcKey) ||
            dialogIndex != FestivalEnvoyRules.FunctionNumber)
        {
            return false;
        }

        var path = FestivalEnvoyRules.ReadPath(arguments);
        if (subId == FestivalEnvoyRules.WeekendButton)
        {
            var claim = path == FestivalEnvoyRules.WeekendClaimPath.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (!VillagersSwitches.WeekendClaimEnabled)
            {
                if (claim)
                {
                    // Switched off: swallow a raw 501 too, so "off" means off.
                    Console.WriteLine(
                        $"[villagers] weekend claim switched off npc={npc.InteractionId}");
                    return true;
                }
                return false;
            }

            if (path == string.Empty)
            {
                // Captured reference page: claim button, rule text, then 4/5.
                await SendFestivalEnvoyPageAsync(npc, [501, 502, 4, 5], "FestivalEnvoyWeekendPage", cancellationToken);
                return true;
            }
            if (claim)
            {
                await HandleWeekendExperienceClaimAsync(npc.InteractionId, cancellationToken);
                return true;
            }
            return false;
        }

        if (subId is not (FestivalEnvoyRules.ClaimGiftButton or
                FestivalEnvoyRules.IntroButton or
                FestivalEnvoyRules.NextFestivalButton) ||
            path != string.Empty ||
            !VillagersSwitches.FestivalGiftsEnabled ||
            _store is not IVillagersStore store ||
            _account is null ||
            _character is null)
        {
            return false;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var today = _realmCalendar.GetDay(now);
            switch (subId)
            {
                case FestivalEnvoyRules.IntroButton:
                {
                    var festival = await store.ReadFestivalGiftDayAsync(today, cancellationToken) ??
                        await store.ReadNextFestivalGiftDayAsync(today, cancellationToken);
                    await SendFestivalEnvoyPageAsync(
                        npc,
                        [festival is null
                            ? FestivalEnvoyRules.NoGiftTodayText
                            : FestivalEnvoyRules.IntroText(festival.FestivalIndex)],
                        "FestivalEnvoyIntro",
                        cancellationToken);
                    return true;
                }
                case FestivalEnvoyRules.NextFestivalButton:
                {
                    var next = await store.ReadNextFestivalGiftDayAsync(today, cancellationToken);
                    await SendFestivalEnvoyPageAsync(
                        npc,
                        [next is null
                            ? FestivalEnvoyRules.NoGiftTodayText
                            : FestivalEnvoyRules.NextText(next.FestivalIndex)],
                        "FestivalEnvoyNext",
                        cancellationToken);
                    return true;
                }
                default:
                    await HandleFestivalGiftClaimAsync(npc, store, now, today, cancellationToken);
                    return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[villagers] festival envoy failed character={_character.Name} " +
                $"button={subId}: {ex.Message}");
            return true;
        }
    }

    private async Task HandleFestivalGiftClaimAsync(
        NpcSpawnDefinition npc,
        IVillagersStore store,
        DateTimeOffset now,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        var gift = await store.ReadFestivalGiftDayAsync(today, cancellationToken);
        int[] page;
        if (gift is null)
        {
            page = [FestivalEnvoyRules.NotFestivalDayText];
        }
        else if (character.Level < gift.MinimumLevel)
        {
            page = [FestivalEnvoyRules.BelowLevelText];
        }
        else if (FestivalEnvoyRules.IsTooLate(
                     TimeOnly.FromDateTime(_realmCalendar.ToRealmTime(now).DateTime)))
        {
            page = [FestivalEnvoyRules.TooLateText];
        }
        else
        {
            var claim = await store.ClaimFestivalGiftAsync(
                _account!.Id, character.Id, gift, cancellationToken);
            switch (claim.Status)
            {
                case FestivalGiftClaimStatus.AlreadyClaimed:
                    page = [FestivalEnvoyRules.AlreadyClaimedText];
                    break;
                case FestivalGiftClaimStatus.InsufficientCapacity:
                    page = [FestivalEnvoyRules.BagFullText];
                    break;
                case FestivalGiftClaimStatus.Granted when claim.Character is not null:
                {
                    InstallUpdatedCharacter(claim.Character);
                    _registry.UpdateCharacter(_session, _character!, advanceWorldRevision: false);
                    if (gift.ItemId > 0)
                    {
                        await SendKitBagRefreshAsync(cancellationToken);
                    }

                    // Same payout path and event multiplier as Zeus' Gift / Lucky Gods.
                    var rates = ServerRates.Current;
                    var paidExperience = rates.Multiply(
                        ServerRateKeys.EventExperience, gift.Experience, FestivalEnvoyRules.MaximumShownAmount);
                    var paidTalentPoints = rates.Multiply(
                        ServerRateKeys.EventExperience, gift.TalentPoints, FestivalEnvoyRules.MaximumShownAmount);
                    await PayZeusGiftProgressionAsync(
                        gift.Experience,
                        checked(gift.TalentPoints * 100),
                        "FestivalGift",
                        cancellationToken);
                    page = FestivalEnvoyRules.GrantedPage(
                        gift with { Experience = paidExperience, TalentPoints = paidTalentPoints });
                    Console.WriteLine(
                        $"[villagers] festival gift character={character.Name} day={today:yyyy-MM-dd} " +
                        $"festival={gift.FestivalIndex} exp={paidExperience} tp={paidTalentPoints} " +
                        $"item={gift.ItemId}x{gift.ItemCount}");
                    break;
                }
                default:
                    Console.Error.WriteLine(
                        $"[villagers] festival gift claimant missing character={character.Name}");
                    return;
            }
        }

        await SendFestivalEnvoyPageAsync(npc, page, "FestivalEnvoyGift", cancellationToken);
    }

    private Task SendFestivalEnvoyPageAsync(
        NpcSpawnDefinition npc,
        int[] subIds,
        string reason,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.CapturedNpcFunctionActionResponse(
                npc.InteractionId,
                FestivalEnvoyRules.FunctionNumber,
                subIds),
            cancellationToken,
            reason);
}
