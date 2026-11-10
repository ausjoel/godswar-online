using System.Buffers.Binary;
using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rates;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>MARRIAGE: the partner's own handler re-reads its wallet, bag and titles after a couple change.</summary>
internal sealed record OperatorMarriageRefreshAction(string ActorName) : OperatorSessionAction(ActorName);

/// <summary>The words of a 10069 click that the marriage dialogues read (copied before any await).</summary>
internal readonly record struct MarriageRequestWords(int[] Path, int FirstBox, int SecondBox);

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// MARRIAGE: the Avatar of Pan (<c>Pans_Labyrinth_010</c>) hands out the Whelk Necklace. Its lines are
    /// Index-1 entries of Cupid's own script (<c>NpcFunMaterial.lua</c> MOON100-400: "You cannot claim that
    /// before you get engaged", "Not enough empty space", "Congratulations, you've gained a Whelk Necklace"),
    /// so it is bound to the same function 41. The opening is answered from the character's state; nothing
    /// is advertised or answered when the marriage switch is off (an empty table, like the old routing gap).
    /// </summary>
    private static readonly ScriptedNpcDialogue PanNecklaceDialogue = new(
        FunctionNumber: MarriagePolicy.CupidFunction,
        OpeningMenu: [],
        Steps: new Dictionary<int, int[]>());

    private enum MarriagePendingKind
    {
        Proposal,
        GroomVows,
        EndEngagement,
        EndMarriage
    }

    /// <summary>A step one partner took that the other partner's own click completes (in memory only).</summary>
    private sealed record MarriagePending(int FromId, long MarriageId, DateTimeOffset ExpiresUtc);

    private static readonly ConcurrentDictionary<(MarriagePendingKind Kind, int ToId), MarriagePending> MarriagePendings = new();

    /// <summary>The click path at <c>+12</c>..<c>+28</c> and the two form boxes at <c>+0x38</c> / <c>+0x3C</c>.</summary>
    private static MarriageRequestWords ReadMarriageWords(GamePacket packet)
    {
        var payload = packet.Payload;
        var path = new int[5];
        for (var index = 0; index < path.Length; index++)
        {
            var offset = 12 + index * 4;
            path[index] = offset + 4 <= payload.Length
                ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4))
                : -1;
        }

        var first = DialogAmountOffset + 4 <= payload.Length
            ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(DialogAmountOffset, 4))
            : -1;
        var second = DialogAmountOffset + 8 <= payload.Length
            ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(DialogAmountOffset + 4, 4))
            : -1;
        return new MarriageRequestWords(path, first, second);
    }

    /// <summary>
    /// MARRIAGE: Cupid (function 41), Cupid's Shadow (function 43) and Pan. Returns false - and the dialogue's
    /// old table answers - when the switch is off, the store is missing, or the click is one this feature
    /// does not own (Cupid's and the Shadow's opening pages stay the old ones).
    /// </summary>
    private async Task<bool> TryHandleMarriageAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        MarriageRequestWords words,
        CancellationToken cancellationToken)
    {
        if (dialogue.FunctionNumber is not (MarriagePolicy.CupidFunction or MarriagePolicy.ShadowFunction) ||
            _character is null ||
            _account is null ||
            !MarriagePolicy.IsEnabled())
        {
            return false;
        }

        if (MarriageControls.Store is not { } store)
        {
            Console.WriteLine($"[marriage] store unavailable npc={npc.InteractionId}");
            return false;
        }

        var click = MarriagePolicy.ResolveClick(words.Path);
        var context = new MarriageTurn(this, npc, dialogue, dialogIndex, click, store, cancellationToken);
        Console.WriteLine(
            $"[marriage] click npc={npc.NpcKey} function={dialogue.FunctionNumber} character={_character.Name} " +
            $"path=[{string.Join(',', click.Path)}] zeros={click.TrailingZeros} boxes={words.FirstBox},{words.SecondBox}");
        try
        {
            if (ReferenceEquals(dialogue, PanNecklaceDialogue))
            {
                return await HandlePanNecklaceAsync(context);
            }

            return dialogue.FunctionNumber == MarriagePolicy.CupidFunction
                ? await HandleCupidAsync(context, words)
                : await HandleCupidsShadowAsync(context);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[marriage] failed character={_character?.Name}: {error.Message}");
            return true;
        }
    }

    private readonly record struct MarriageTurn(
        GameClientHandler Handler,
        NpcSpawnDefinition Npc,
        ScriptedNpcDialogue Dialogue,
        int DialogIndex,
        MarriageClick Click,
        IMarriageStore Store,
        CancellationToken Token)
    {
        public Task Reply(params int[] numbers) =>
            Handler.SendScriptedNpcDialogueReplyAsync(Npc, Dialogue, DialogIndex, Click.Clicked, numbers, Token);
    }

    // ---- Pan -----------------------------------------------------------------------------------

    private async Task<bool> HandlePanNecklaceAsync(MarriageTurn turn)
    {
        if (turn.Click.Path.Length != 0)
        {
            return true;
        }

        var result = await turn.Store.ClaimNecklaceAsync(_character!.Id, turn.Token);
        switch (result.Outcome)
        {
            case MarriageOutcome.Done:
                await ApplyMarriageRefreshAsync(turn.Token);
                await turn.Reply(MarriagePolicy.PanNecklaceGiven);
                break;
            case MarriageOutcome.BagFull:
                await turn.Reply(MarriagePolicy.PanBagFull);
                break;
            case MarriageOutcome.AlreadyClaimed:
                // No client line says "already given": a game-log line (generated text) instead.
                await SendMarriageLogAsync("Pan has already given you a Whelk Necklace for this engagement.", turn.Token);
                await turn.Reply();
                break;
            default:
                await turn.Reply(MarriagePolicy.PanNotEngaged);
                break;
        }

        Console.WriteLine($"[marriage] necklace character={_character!.Name} outcome={result.Outcome}");
        return true;
    }

    // ---- Cupid ---------------------------------------------------------------------------------

    private const int CupidEntryBuyNecklace = 7;      // F-130: client 1.4.5 page-one button
    private const int CupidNecklaceNoGold = 410;      // F-130: client 1.4.5 text "You need ... Gold"

    /// <summary>F-130 (the admin): Cupid sells the Whelk Necklace for Gold (npc.cupid_necklace_gold, 2,300).</summary>
    private async Task BuyNecklaceAtCupidAsync(MarriageTurn turn)
    {
        var price = Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
            Godswar.Server.Application.Finalise2.Finalise2RateKeys.CupidNecklaceGold);
        var result = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.TrySpendGoldAsync(price) is null) { tx.Rollback(); return CupidNecklaceNoGold; }
            if (!await tx.GrantAsync(MarriagePolicy.WhelkNecklaceItemId, 1, bound: true)) { tx.Rollback(); return MarriagePolicy.PanBagFull; }
            return MarriagePolicy.PanNecklaceGiven;
        }, -1, turn.Token);
        Console.WriteLine($"[marriage] cupid necklace sale character={_character!.Name} price={price} result={result}");
        if (result == MarriagePolicy.PanNecklaceGiven) await RefreshAfterEventsNpcAsync([], turn.Token);
        if (result == CupidNecklaceNoGold)
            await SendMarriageLogAsync($"A Whelk Necklace costs {price:N0} Gold.", turn.Token);
        await turn.Reply(result < 0 ? [] : [result]);
    }

    private async Task<bool> HandleCupidAsync(MarriageTurn turn, MarriageRequestWords words)
    {
        var click = turn.Click;
        if (click.Path.Length == 0)
        {
            return false;
        }

        var me = _character!;
        var row = await turn.Store.GetActiveAsync(me.Id, turn.Token);
        var page = click.PageOneEntry(
            MarriagePolicy.EntryEngage, MarriagePolicy.EntryBook, MarriagePolicy.EntryCeremony);

        // The booking form's submission: the path that opened it plus the client's confirm key.
        if (click.Clicked == MarriagePolicy.EntryBook && click.TrailingZeros > 0 ||
            click.Clicked == MarriagePolicy.PageBookingForm && page == MarriagePolicy.EntryBook)
        {
            if (click.TrailingZeros > 1)
            {
                return true;   // the companion packet a submission sends after itself
            }

            await SubmitBookingAsync(turn, row, words);
            return true;
        }

        switch (click.Clicked)
        {
            case MarriagePolicy.EntryEngage:
                await turn.Reply(TryPeekPending(MarriagePendingKind.Proposal, me.Id, out var proposal) &&
                                 PartyPartner() is { } suitor && suitor.CharacterId == proposal.FromId
                    ? [MarriagePolicy.TextEngagement, MarriagePolicy.ButtonAgree, MarriagePolicy.ButtonDisagree]
                    : [MarriagePolicy.TextEngagement, MarriagePolicy.ButtonWishEngaged]);
                return true;
            case MarriagePolicy.ButtonWishEngaged when page == MarriagePolicy.EntryEngage:
                await ProposeAsync(turn, row);
                return true;
            case MarriagePolicy.ButtonAgree when page == MarriagePolicy.EntryEngage:
                await AcceptProposalAsync(turn);
                return true;
            case MarriagePolicy.ButtonDisagree when page == MarriagePolicy.EntryEngage:
                if (TryTakePending(MarriagePendingKind.Proposal, me.Id, out var declined))
                {
                    await SendMarriageLogToAsync(declined.FromId, $"{SafeName(me.Name)} declined your proposal.", turn.Token);
                }

                return true;
            case MarriagePolicy.EntryBook:
                await OpenBookingAsync(turn, row);
                return true;
            case MarriagePolicy.ButtonAbandonSlot when page == MarriagePolicy.EntryBook:
                if (row is { IsMarried: false, CeremonySlot: not null } && row.GroomId == me.Id &&
                    (await turn.Store.AbandonSlotAsync(row.Id, me.Id, turn.Token)).Outcome == MarriageOutcome.Done)
                {
                    await turn.Reply(MarriagePolicy.SlotDeleted);
                }

                return true;
            case MarriagePolicy.EntryCeremony:
                await OpenCeremonyAsync(turn, row);
                return true;
            case MarriagePolicy.ButtonAgree when page == MarriagePolicy.EntryCeremony:
                await SayIDoAsync(turn, row);
                return true;
            case MarriagePolicy.ButtonDisagree when page == MarriagePolicy.EntryCeremony:
                if (row is not null)
                {
                    MarriagePendings.TryRemove((MarriagePendingKind.GroomVows, row.BrideId), out _);
                    await SendMarriageLogToAsync(
                        row.PartnerOf(me.Id), $"{SafeName(me.Name)} did not say yes. The ceremony did not take place.",
                        turn.Token);
                }

                return true;
            case MarriagePolicy.EntryGift:
                await ClaimGiftAsync(turn, row);
                return true;
            case CupidEntryBuyNecklace when Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.CupidNecklaceGold) > 0:
                await BuyNecklaceAtCupidAsync(turn);
                return true;
            case MarriagePolicy.EntryInquire:
                var state = row is { IsMarried: false }
                    ? MarriagePolicy.SlotState(row.CeremonySlot, DateTimeOffset.UtcNow)
                    : MarriageSlotState.None;
                await turn.Reply(row is null or { IsMarried: true }
                    ? MarriagePolicy.InquireNotEligible
                    : state switch
                    {
                        MarriageSlotState.None => MarriagePolicy.InquireNothing,
                        MarriageSlotState.Expired => MarriagePolicy.InquireExpired,
                        _ => MarriagePolicy.EncodeSlot(_realmCalendar.ToRealmTime(row.CeremonySlot!.Value))
                    });
                return true;
            default:
                return false;
        }
    }

    private async Task ProposeAsync(MarriageTurn turn, MarriageRow? row)
    {
        var me = _character!;
        if (me.Gender != 1)
        {
            await turn.Reply(MarriagePolicy.EngageMenOnly);
            return;
        }

        if (row is not null)
        {
            await turn.Reply(MarriagePolicy.EngageAlreadyLinked);
            return;
        }

        if (PartyPartner() is not { } partner || partner.Character.Gender != 0)
        {
            await turn.Reply(MarriagePolicy.EngageOppositeGender);
            return;
        }

        if (await turn.Store.GetActiveAsync(partner.CharacterId, turn.Token) is not null)
        {
            await turn.Reply(MarriagePolicy.EngageAlreadyLinked);
            return;
        }

        var price = MarriagePolicy.EngagementSilver(ServerRates.Current);
        if ((await turn.Store.WalletAsync(me.Id, turn.Token)).Silver < price)
        {
            await turn.Reply(MarriagePolicy.EngageNotEnoughSilver);
            return;
        }

        MarriagePendings[(MarriagePendingKind.Proposal, partner.CharacterId)] =
            new MarriagePending(me.Id, 0, DateTimeOffset.UtcNow + MarriagePolicy.PendingLifetime);
        await SendMarriageLogToAsync(
            partner.CharacterId,
            $"{SafeName(me.Name)} asks you to get engaged. Talk to Cupid and choose Apply to get engaged to answer.",
            turn.Token);
        await turn.Reply(MarriagePolicy.EngageWaitForBride);
        Console.WriteLine($"[marriage] proposal groom={me.Name} bride={partner.CharacterName}");
    }

    private async Task AcceptProposalAsync(MarriageTurn turn)
    {
        var me = _character!;
        if (!TryTakePending(MarriagePendingKind.Proposal, me.Id, out var proposal))
        {
            await SendMarriageLogAsync("The proposal has expired. He must ask again.", turn.Token);
            return;
        }

        var price = MarriagePolicy.EngagementSilver(ServerRates.Current);
        var result = await turn.Store.EngageAsync(proposal.FromId, me.Id, price, turn.Token);
        Console.WriteLine($"[marriage] engage groom={proposal.FromId} bride={me.Name} outcome={result.Outcome}");
        switch (result.Outcome)
        {
            case MarriageOutcome.Done:
                await RefreshMarriageSelfAsync(turn.Token);
                await RefreshMarriagePartnerAsync(proposal.FromId, turn.Token);
                var groomName = NameOf(proposal.FromId);
                await SendMarriageLogAsync($"You are now engaged to {groomName}.", turn.Token);
                await SendMarriageLogToAsync(proposal.FromId, $"{SafeName(me.Name)} said yes! You are now engaged.", turn.Token);
                break;
            case MarriageOutcome.NotEnoughSilver:
                await SendMarriageLogAsync($"He does not have the {price} Silver for the engagement.", turn.Token);
                await SendMarriageLogToAsync(proposal.FromId, $"You need {price} Silver to get engaged.", turn.Token);
                break;
            default:
                await turn.Reply(MarriagePolicy.EngageAlreadyLinked);
                break;
        }
    }

    private async Task OpenBookingAsync(MarriageTurn turn, MarriageRow? row)
    {
        var me = _character!;
        if (row is null or { IsMarried: true })
        {
            await turn.Reply(MarriagePolicy.NotEngaged);
            return;
        }

        if (me.Gender != 1 || row.GroomId != me.Id)
        {
            await turn.Reply(MarriagePolicy.OnlyMaleBooks);
            return;
        }

        if (MarriagePolicy.NecklaceRequired(ServerRates.Current) &&
            !await turn.Store.HasBagItemAsync(me.Id, MarriagePolicy.WhelkNecklaceItemId, turn.Token))
        {
            await turn.Reply(MarriagePolicy.NoNecklace);
            return;
        }

        var state = MarriagePolicy.SlotState(row.CeremonySlot, DateTimeOffset.UtcNow);
        await turn.Reply(state is MarriageSlotState.Waiting or MarriageSlotState.Open
            ? [MarriagePolicy.EncodeSlot(_realmCalendar.ToRealmTime(row.CeremonySlot!.Value)), MarriagePolicy.ButtonAbandonSlot]
            : [MarriagePolicy.PageBookingForm]);
    }

    private async Task SubmitBookingAsync(MarriageTurn turn, MarriageRow? row, MarriageRequestWords words)
    {
        var me = _character!;
        if (row is null or { IsMarried: true } || row.GroomId != me.Id)
        {
            await turn.Reply(MarriagePolicy.BothMustBeEngaged);
            return;
        }

        if (MarriagePolicy.NecklaceRequired(ServerRates.Current) &&
            !await turn.Store.HasBagItemAsync(me.Id, MarriagePolicy.WhelkNecklaceItemId, turn.Token))
        {
            await turn.Reply(MarriagePolicy.BothNeedNecklace);
            return;
        }

        if (MarriagePolicy.SlotState(row.CeremonySlot, DateTimeOffset.UtcNow) is MarriageSlotState.Waiting or MarriageSlotState.Open)
        {
            await turn.Reply(MarriagePolicy.BookAlreadyBooked);
            return;
        }

        var nowRealm = _realmCalendar.ToRealmTime(DateTimeOffset.UtcNow);
        if (!MarriagePolicy.TryReadBookingRequest(words.FirstBox, words.SecondBox, out var weekday, out var hour) ||
            !MarriagePolicy.TryResolveSlot(nowRealm, weekday, hour, out var slotRealm))
        {
            await turn.Reply(MarriagePolicy.BookBadFormat);
            return;
        }

        var gold = MarriagePolicy.BookingGold(ServerRates.Current);
        var result = await turn.Store.BookAsync(row.Id, me.Id, slotRealm.ToUniversalTime(), gold, turn.Token);
        Console.WriteLine(
            $"[marriage] book groom={me.Name} weekday={weekday} hour={hour} slot={slotRealm:O} gold={gold} outcome={result.Outcome}");
        switch (result.Outcome)
        {
            case MarriageOutcome.Done:
                await RefreshMarriageSelfAsync(turn.Token);
                await turn.Reply(MarriagePolicy.BookDone);
                await SendMarriageLogToAsync(
                    row.BrideId,
                    $"{SafeName(me.Name)} booked your wedding for " +
                    slotRealm.ToString("ddd d MMM 'at' HH':00'", System.Globalization.CultureInfo.InvariantCulture) + ".",
                    turn.Token);
                break;
            case MarriageOutcome.NotEnoughGold:
                await turn.Reply(MarriagePolicy.BookNotEnoughGold);
                break;
            case MarriageOutcome.SlotTaken:
                await turn.Reply(MarriagePolicy.BookSlotTaken);
                break;
            case MarriageOutcome.AlreadyBooked:
                await turn.Reply(MarriagePolicy.BookAlreadyBooked);
                break;
            default:
                await turn.Reply(MarriagePolicy.BothMustBeEngaged);
                break;
        }
    }

    /// <summary>The ceremony page's refusal for this couple right now, or 0 when the vows may be shown.</summary>
    private int CeremonyRefusal(MarriageRow? row)
    {
        if (row is null or { IsMarried: true })
        {
            return MarriagePolicy.NotEngaged;
        }

        if (PartyPartner() is not { } partner)
        {
            return MarriagePolicy.MustTeamUp;
        }

        if (partner.CharacterId != row.PartnerOf(_character!.Id))
        {
            return MarriagePolicy.NotEngagedToPartner;
        }

        return MarriagePolicy.SlotState(row.CeremonySlot, DateTimeOffset.UtcNow) switch
        {
            MarriageSlotState.None => MarriagePolicy.NoSlotBooked,
            MarriageSlotState.Expired => MarriagePolicy.SlotExpired,
            MarriageSlotState.Waiting => MarriagePolicy.NotYetTime,
            _ => 0
        };
    }

    private async Task OpenCeremonyAsync(MarriageTurn turn, MarriageRow? row)
    {
        var refusal = CeremonyRefusal(row);
        if (refusal != 0)
        {
            await turn.Reply(refusal);
            return;
        }

        var me = _character!;
        if (row!.GroomId == me.Id)
        {
            await turn.Reply(MarriagePolicy.TextVowsGroom, MarriagePolicy.ButtonAgree, MarriagePolicy.ButtonDisagree);
            return;
        }

        await turn.Reply(TryPeekPending(MarriagePendingKind.GroomVows, me.Id, out var vows) && vows.MarriageId == row.Id
            ? [MarriagePolicy.TextVowsBride, MarriagePolicy.ButtonAgree, MarriagePolicy.ButtonDisagree]
            : [MarriagePolicy.GroomNotConfirmed]);
    }

    private async Task SayIDoAsync(MarriageTurn turn, MarriageRow? row)
    {
        var me = _character!;
        var groom = row is not null && row.GroomId == me.Id;
        var refusal = CeremonyRefusal(row);
        if (refusal != 0)
        {
            await turn.Reply(refusal is MarriagePolicy.NotEngaged or MarriagePolicy.MustTeamUp or MarriagePolicy.NotEngagedToPartner
                ? MarriagePolicy.BothMustBeEngaged
                : groom ? MarriagePolicy.CeremonyExpired : MarriagePolicy.CeremonyExpiredBride);
            return;
        }

        var necklaces = MarriagePolicy.NecklaceRequired(ServerRates.Current);
        if (necklaces &&
            (!await turn.Store.HasBagItemAsync(row!.GroomId, MarriagePolicy.WhelkNecklaceItemId, turn.Token) ||
             !await turn.Store.HasBagItemAsync(row.BrideId, MarriagePolicy.WhelkNecklaceItemId, turn.Token)))
        {
            await turn.Reply(MarriagePolicy.BothNeedNecklace);
            return;
        }

        if (groom)
        {
            MarriagePendings[(MarriagePendingKind.GroomVows, row!.BrideId)] =
                new MarriagePending(me.Id, row.Id, row.CeremonySlot!.Value.AddHours(1));
            await turn.Reply(MarriagePolicy.GroomConfirmed);
            await SendMarriageLogToAsync(
                row.BrideId, $"{SafeName(me.Name)} said I do. Talk to Cupid and choose Begin the wedding ceremony.", turn.Token);
            return;
        }

        if (!TryTakePending(MarriagePendingKind.GroomVows, me.Id, out var vows) || vows.MarriageId != row!.Id)
        {
            await turn.Reply(MarriagePolicy.BrideBeforeGroom);
            return;
        }

        var result = await turn.Store.MarryAsync(row.Id, necklaces, turn.Token);
        Console.WriteLine($"[marriage] wedding couple={row.Id} groom={row.GroomId} bride={me.Name} outcome={result.Outcome}");
        if (result.Outcome != MarriageOutcome.Done)
        {
            await turn.Reply(result.Outcome == MarriageOutcome.NecklaceMissing
                ? MarriagePolicy.BothNeedNecklace
                : MarriagePolicy.CeremonyExpiredBride);
            return;
        }

        await RefreshMarriageSelfAsync(turn.Token);
        await RefreshMarriagePartnerAsync(row.GroomId, turn.Token);
        // Wiki-56: "the announcement will appear to players of the other faction as well". Wording generated.
        var announcement = $"Cupid proclaims {NameOf(row.GroomId)} and {SafeName(me.Name)} husband and wife!";
        if (announcement.Length > PacketBuilder.CenteredAnnouncementMaximumTextLength)
        {
            announcement = announcement[..PacketBuilder.CenteredAnnouncementMaximumTextLength];
        }

        var delivered = await _registry.BroadcastToAllSessionsAsync(
            PacketBuilder.CenteredAnnouncement(announcement), turn.Token, "MarriageWeddingNotice");
        await SendMarriageLogAsync("You are now married. Ask Cupid for your wedding gift.", turn.Token);
        await SendMarriageLogToAsync(row.GroomId, "You are now married. Ask Cupid for your wedding gift.", turn.Token);
        Console.WriteLine($"[marriage] wedding announced to {delivered} sessions");
    }

    private async Task ClaimGiftAsync(MarriageTurn turn, MarriageRow? row)
    {
        if (row is not { IsMarried: true })
        {
            await turn.Reply(MarriagePolicy.GiftNotMarried);
            return;
        }

        var result = await turn.Store.ClaimGiftAsync(row.Id, _character!.Id, turn.Token);
        Console.WriteLine($"[marriage] gift character={_character.Name} outcome={result.Outcome}");
        switch (result.Outcome)
        {
            case MarriageOutcome.Done:
                await ApplyMarriageRefreshAsync(turn.Token);
                await turn.Reply(MarriagePolicy.GiftClaimed);
                break;
            case MarriageOutcome.AlreadyClaimed:
                await turn.Reply(MarriagePolicy.GiftAlreadyClaimed);
                break;
            case MarriageOutcome.BagFull:
                await turn.Reply(MarriagePolicy.GiftBagFull);
                break;
            default:
                await turn.Reply(MarriagePolicy.GiftNotMarried);
                break;
        }
    }

    // ---- Cupid's Shadow ------------------------------------------------------------------------

    private async Task<bool> HandleCupidsShadowAsync(MarriageTurn turn)
    {
        var click = turn.Click;
        if (click.Path.Length == 0)
        {
            return false;
        }

        var me = _character!;
        var row = await turn.Store.GetActiveAsync(me.Id, turn.Token);
        var page = click.PageOneEntry(MarriagePolicy.ShadowEntryEngagement, MarriagePolicy.ShadowEntryMarriage);
        switch (click.Clicked)
        {
            case MarriagePolicy.ShadowEntryEngagement:
                await turn.Reply(row is { IsMarried: false }
                    ? [MarriagePolicy.ShadowMutualEngagement, MarriagePolicy.ShadowForcedEngagement]
                    : [MarriagePolicy.ShadowNotEngaged]);
                return true;
            case MarriagePolicy.ShadowEntryMarriage:
                await turn.Reply(row is not { IsMarried: true }
                    ? [MarriagePolicy.ShadowNotMarried]
                    : DateTimeOffset.UtcNow - row.MarriedAt!.Value < MarriagePolicy.DivorceWaitingPeriod
                        ? [MarriagePolicy.ShadowTooSoon]
                        : [MarriagePolicy.ShadowMutualDivorce, MarriagePolicy.ShadowForcedDivorce]);
                return true;
            case MarriagePolicy.ShadowMutualEngagement when page == MarriagePolicy.ShadowEntryEngagement:
                await EndByConsentAsync(turn, row, marriage: false);
                return true;
            case MarriagePolicy.ShadowMutualDivorce when page == MarriagePolicy.ShadowEntryMarriage:
                await EndByConsentAsync(turn, row, marriage: true);
                return true;
            case MarriagePolicy.ShadowForcedEngagement when page == MarriagePolicy.ShadowEntryEngagement:
                await EndByForceAsync(turn, row, marriage: false);
                return true;
            case MarriagePolicy.ShadowForcedDivorce when page == MarriagePolicy.ShadowEntryMarriage:
                await EndByForceAsync(turn, row, marriage: true);
                return true;
            default:
                return false;
        }
    }

    private async Task EndByConsentAsync(MarriageTurn turn, MarriageRow? row, bool marriage)
    {
        var me = _character!;
        if (row is null || row.IsMarried != marriage)
        {
            await turn.Reply(marriage ? MarriagePolicy.ShadowNotMarriedCouple : MarriagePolicy.ShadowNotPartners);
            return;
        }

        if (marriage && DateTimeOffset.UtcNow - row.MarriedAt!.Value < MarriagePolicy.DivorceWaitingPeriod)
        {
            await turn.Reply(MarriagePolicy.ShadowNotMarriedCouple);
            return;
        }

        if (PartyPartner() is not { } partner)
        {
            await turn.Reply(MarriagePolicy.ShadowMustTeamUp);
            return;
        }

        if (partner.CharacterId != row.PartnerOf(me.Id))
        {
            await turn.Reply(MarriagePolicy.ShadowNotPartners);
            return;
        }

        var kind = marriage ? MarriagePendingKind.EndMarriage : MarriagePendingKind.EndEngagement;
        if (!TryTakePending(kind, me.Id, out var request) || request.MarriageId != row.Id ||
            request.FromId != partner.CharacterId)
        {
            MarriagePendings[(kind, partner.CharacterId)] =
                new MarriagePending(me.Id, row.Id, DateTimeOffset.UtcNow + MarriagePolicy.PendingLifetime);
            await turn.Reply(MarriagePolicy.ShadowWaitForOther);
            await SendMarriageLogToAsync(
                partner.CharacterId,
                $"{SafeName(me.Name)} asks Cupid's Shadow to end your {(marriage ? "marriage" : "engagement")}. " +
                "Choose the same option there to agree.",
                turn.Token);
            return;
        }

        var result = await turn.Store.EndAsync(
            row.Id, request.FromId,
            marriage ? MarriageEndKind.MutualDivorce : MarriageEndKind.MutualEngagement,
            new MarriageFine(0, 0), turn.Token);
        Console.WriteLine($"[marriage] end by consent couple={row.Id} marriage={marriage} outcome={result.Outcome}");
        if (result.Outcome != MarriageOutcome.Done)
        {
            await turn.Reply(MarriagePolicy.ShadowNotPartners);
            return;
        }

        await RefreshMarriageSelfAsync(turn.Token);
        await RefreshMarriagePartnerAsync(partner.CharacterId, turn.Token);
        await turn.Reply(MarriagePolicy.ShadowDone);
        await SendMarriageLogToAsync(
            partner.CharacterId,
            marriage ? "Your marriage has been annulled. You each lost 100 Reputation." : "Your engagement has been cancelled.",
            turn.Token);
    }

    private async Task EndByForceAsync(MarriageTurn turn, MarriageRow? row, bool marriage)
    {
        var me = _character!;
        if (row is null || row.IsMarried != marriage)
        {
            await turn.Reply(marriage ? MarriagePolicy.ShadowNotMarriedCouple : MarriagePolicy.ShadowNotPartners);
            return;
        }

        if (marriage && DateTimeOffset.UtcNow - row.MarriedAt!.Value < MarriagePolicy.DivorceWaitingPeriod)
        {
            await turn.Reply(MarriagePolicy.ShadowNotMarriedCouple);
            return;
        }

        var partnerId = row.PartnerOf(me.Id);
        var online = _registry.TryGetCharacterContext(partnerId, out _);
        var lastOnline = await turn.Store.LastOnlineAsync(partnerId, turn.Token);
        var absent = MarriagePolicy.IsAbsent(lastOnline, online, DateTimeOffset.UtcNow);
        var wallet = await turn.Store.WalletAsync(me.Id, turn.Token);
        var fine = MarriagePolicy.ForcedFine(absent, wallet.Reputation, wallet.Silver);
        if (fine is null)
        {
            await turn.Reply(MarriagePolicy.ShadowCannotPayFine);
            return;
        }

        var result = await turn.Store.EndAsync(
            row.Id, me.Id, marriage ? MarriageEndKind.ForcedDivorce : MarriageEndKind.ForcedEngagement,
            fine.Value, turn.Token);
        Console.WriteLine(
            $"[marriage] forced end couple={row.Id} marriage={marriage} by={me.Name} absent={absent} " +
            $"last-online={lastOnline:O} fine-rep={fine.Value.Reputation} fine-silver={fine.Value.Silver} outcome={result.Outcome}");
        if (result.Outcome != MarriageOutcome.Done)
        {
            await turn.Reply(result.Outcome == MarriageOutcome.CannotPayFine
                ? MarriagePolicy.ShadowCannotPayFine
                : MarriagePolicy.ShadowNotPartners);
            return;
        }

        MarriagePendings.TryRemove((MarriagePendingKind.GroomVows, row.BrideId), out _);
        await RefreshMarriageSelfAsync(turn.Token);
        await RefreshMarriagePartnerAsync(partnerId, turn.Token);
        await turn.Reply(MarriagePolicy.ShadowDone);
        await SendMarriageLogToAsync(
            partnerId,
            $"{SafeName(me.Name)} ended your {(marriage ? "marriage" : "engagement")} at Cupid's Shadow.",
            turn.Token);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The other member of a party of exactly two, when that member is in the world.</summary>
    private GameSessionContext? PartyPartner()
    {
        if (_character is null ||
            _registry.GetPartyMembership(_session) is not { } party ||
            party.MemberCharacterIds.Count != 2)
        {
            return null;
        }

        var other = party.MemberCharacterIds[0] == _character.Id
            ? party.MemberCharacterIds[1]
            : party.MemberCharacterIds[0];
        return _registry.TryGetCharacterContext(other, out var context) ? context : null;
    }

    private static bool TryPeekPending(MarriagePendingKind kind, int toId, out MarriagePending pending)
    {
        if (MarriagePendings.TryGetValue((kind, toId), out pending!) && pending.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            return true;
        }

        if (pending is not null)
        {
            MarriagePendings.TryRemove((kind, toId), out _);
        }

        pending = null!;
        return false;
    }

    private static bool TryTakePending(MarriagePendingKind kind, int toId, out MarriagePending pending) =>
        MarriagePendings.TryRemove((kind, toId), out pending!) && pending.ExpiresUtc > DateTimeOffset.UtcNow;

    private string NameOf(int characterId) =>
        _registry.TryGetCharacterContext(characterId, out var context)
            ? SafeName(context.CharacterName)
            : "your partner";

    /// <summary>Game-log text must be printable ASCII; anything else in a name becomes '?'.</summary>
    private static string SafeName(string name) =>
        new([.. name.Select(static c => c is >= ' ' and <= '~' ? c : '?')]);

    private async Task SendMarriageLogAsync(string text, CancellationToken cancellationToken) =>
        await _session.SendAsync(PacketBuilder.PersonalGameLog(text), cancellationToken, "MarriageNotice");

    private async Task SendMarriageLogToAsync(int characterId, string text, CancellationToken cancellationToken)
    {
        if (_registry.TryGetCharacterContext(characterId, out var context))
        {
            await context.Session.SendAsync(PacketBuilder.PersonalGameLog(text), cancellationToken, "MarriageNotice");
        }
    }

    /// <summary>
    /// After a committed change: copy ONLY what a marriage change writes - wallet, Reputation, bag and titles -
    /// from a fresh database snapshot into the live character (the Wonderland chest / capital shop pattern; a
    /// full reload would also replace in-memory progress such as unsaved EXP or zodiac energy), then show it:
    /// status, details, the title list (10196) and the title over the head for everyone around.
    /// </summary>
    private async Task<string> ApplyMarriageRefreshAsync(CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return "not in the world";
        }

        var bagBefore = _character.KitBag;
        var snapshot = await _characterSnapshots.ReadAsync(_account.Id, _processRealmId, cancellationToken);
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
        if (_character is null || hydrated is null || hydrated.Character.Id != _character.Id)
        {
            return "snapshot mismatch";
        }

        var fresh = hydrated.Character;
        _character.Silver = fresh.Silver;
        _character.Gold = fresh.Gold;
        _character.BindingGold = fresh.BindingGold;
        _character.MedusaHonorPoints = fresh.MedusaHonorPoints;
        _character.MedusaRewardRevision = Math.Max(_character.MedusaRewardRevision, fresh.MedusaRewardRevision);
        _character.KitBag = fresh.KitBag;
        _character.OwnedTitleIds = fresh.OwnedTitleIds;
        _character.SelectedTitleId = fresh.SelectedTitleId;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        foreach (var acknowledgement in PacketBuilder.KitBagMutationDeletionAcknowledgements(bagBefore, _character.KitBag))
        {
            await _session.SendAsync(acknowledgement, cancellationToken, "MarriageKitBagDeleteAck");
        }

        await SendKitBagRefreshAsync(cancellationToken);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "MarriageStatus");
        if (_registry.TryGetCharacterContext(_character.Id, out var self))
        {
            await PublishMarriageTitlesAsync(self, cancellationToken);
        }

        return "marriage refreshed";
    }

    private async Task RefreshMarriageSelfAsync(CancellationToken cancellationToken) =>
        await ApplyMarriageRefreshAsync(cancellationToken);

    /// <summary>The partner's own handler applies the same projection (bounded to 10 s by the registry).</summary>
    private async Task RefreshMarriagePartnerAsync(int characterId, CancellationToken cancellationToken)
    {
        if (!_registry.TryGetCharacterContext(characterId, out var partner))
        {
            return;   // offline: the next login reads everything from the database
        }

        var result = await _registry.RunOperatorActionAsync(
            partner.Session, new OperatorMarriageRefreshAction(_character?.Name ?? "marriage"), cancellationToken);
        Console.WriteLine($"[marriage] partner refresh character={partner.CharacterName} result={result}");
    }

    private async Task PublishMarriageTitlesAsync(GameSessionContext context, CancellationToken cancellationToken)
    {
        var character = context.Character;
        var detail = PacketBuilder.PlayerDetail(character);
        if (detail.Length > 0)
        {
            await context.Session.SendAsync(detail, cancellationToken, "MarriageDetail", framed: false);
        }

        await context.Session.SendAsync(
            PacketBuilder.MedusaDesignationInfo(character.SelectedTitleId, character.OwnedTitleIds),
            cancellationToken,
            "MarriageTitleList");
        var title = PacketBuilder.PlayerTitleInfo(character, context.ObjectId);
        await context.Session.SendAsync(title, cancellationToken, "MarriageTitleSelf");
        await _registry.BroadcastToCurrentWorldInstanceAsync(
            context.Session, title, cancellationToken, includeRoutingSession: false, "MarriageTitleObservers");
    }
}
