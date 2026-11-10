using Godswar.Server.Application.Guilds;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Guilds;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// The guild altar's building page: a building's description plus the actions the
/// server can put there without two of them sharing a slot.
/// </summary>
/// <remarks>
/// The altar's script (<c>NpcFunAltar.lua</c>) reuses its number space on every
/// page, so a click only means something together with the page it was made on.
/// The client sends both: <c>+12</c> is the entry whose page the click answers,
/// <c>+16</c> is the entry clicked and is -1 for a click made on the page itself
/// (measured 2026-09-22: page one's <c>1</c> arrives as <c>+12 = 1, +16 = -1</c>,
/// while the building <c>6</c> on the page <c>1</c> opened arrives as
/// <c>+12 = 1, +16 = 6</c>). Reading only <c>+12</c> therefore takes a page-one
/// click for a building and skips the whole second level - which is what the first
/// version did.
///
/// <b>Only two actions travel per page, and never the two that share a
/// coordinate.</b> The script pins new (<c>1</c>) and upgrade (<c>2</c>) to the
/// same spot and leaves delete (<c>3</c>) without one, so it inherits a slot from
/// the window's own layout. Sending all three draws them on top of each other
/// (measured: "the delete, new and upgrade buttons overlap"), and spacing them from
/// the client side does not work either: the coordinates that win are the shared
/// window layout's, not the altar script's (measured: with the altar script's two
/// position lines changed, the overlap stayed). So the page carries exactly one of
/// {new, upgrade} - chosen from whether the guild already has the building -
/// together with delete.
///
/// The numbers whose menus change accounts the server does not run yet - 4/5/6
/// (the three donation kinds) and 10 (worship) - are not published, so no state is
/// invented for them.
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>The altar's page-three action buttons, from its own script.</summary>
    private const int BuildAction = 1;

    private const int UpgradeAction = 2;

    private const int DeleteAction = 3;

    private const int DonateGoldAction = 4;

    private const int DonateSilverAction = 5;

    private const int DonateBoundGoldAction = 6;

    private const int WorshipAction = 10;

    /// <summary>
    /// The number the client's own confirmation button puts in the click path,
    /// which is what a submission of the input box ends with.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-23: a typed amount arrives as depth four whose last entry is
    /// <c>0</c> (gold <c>1,5,4,0</c>, silver <c>1,5,5,0</c>, worship
    /// <c>2,13,10,0</c>), and the same <c>0</c> ends an action's own confirmation
    /// (<c>1,5,1,0</c>). No preset button uses it: those are 3-6 and 8-24.
    /// </remarks>
    private const int ConfirmEntry = 0;

    /// <summary>Page four's preset rows, one array per purse, from its own script.</summary>
    private static readonly int[] GoldAmounts = [8, 9, 10, 11, 12];

    private static readonly int[] SilverAmounts = [14, 15, 16, 17, 18];

    private static readonly int[] BoundGoldAmounts = [19, 20, 21, 22, 23, 24];

    /// <summary>
    /// Page four's per-purse pages: each is its own explanation plus the input box
    /// the player types the amount into.
    /// </summary>
    /// <remarks>
    /// The amounts are typed, not chosen: every explanation ends by asking for the
    /// number ("请在下方输入你要捐赠的金币数" in <c>NF_L0_GH88</c>, and the same in
    /// GH89 and GH103), and the client's page-four script shows that box for
    /// <c>SubID == 1</c>, <c>2</c>, <c>7</c> and <c>13</c> (NpcFunAltar.lua
    /// 510-595). Each of those rows stands immediately before the preset group of
    /// one purse, which is what pairs them: 7 before the gold presets (8-12), 13
    /// before the silver ones (14-18), 1 for the worship points (before 3-6) and 2
    /// for bound gold. The preset buttons are still registered so a click that
    /// arrives with one is honoured.
    /// </remarks>
    private const int GoldDonationTextRow = 304;

    private const int SilverDonationTextRow = 305;

    private const int BoundGoldDonationTextRow = 306;

    private const int GoldInputRow = 7;

    private const int SilverInputRow = 13;

    private const int BoundGoldInputRow = 2;

    private const int WorshipTextRow = 310;

    private const int WorshipInputRow = 1;

    /// <summary>
    /// The client's own "not open yet" line (<c>NF_L0_GH1011</c>), which is what a
    /// binding-gold donation is answered with: the guild holds no such account.
    /// </summary>
    private const int NotAvailableLine = 1011;

    /// <summary>Page three's status lines, from the client's text table.</summary>
    private const int BuildingAbsentLine = 100;

    private const int UpgradeSucceededLine = 1006;

    private const int DeleteSucceededLine = 1008;

    /// <summary>Result lines the client's own table draws (page five).</summary>
    private const int NotAMemberLine = 1000;

    private const int NoSuchBuildingLine = 1007;

    private const int BuildSucceededLine = 1005;

    private const int DonationSucceededLine = 1013;

    private const int GoldShortLine = 1014;

    private const int SilverShortLine = 1015;

    /// <summary>
    /// The offering's own result lines: 1009 "你对神明的供奉已经成功了。" and 1010
    /// "你的贡献點不够，所以供奉失败。"
    /// </summary>
    private const int WorshipSucceededLine = 1009;

    private const int ContributionShortLine = 1010;

    /// <summary>
    /// The preset amount rows page four draws, with the values their own text keys
    /// carry: gold 500/1000/5000/10000/50000 (<c>GH91</c>-<c>GH95</c>), silver
    /// 5000/10000/50000/100000/500000 (<c>GH97</c>-<c>GH101</c>) and bound gold
    /// 100/500/1000/5000/10000/50000 (<c>GH104</c>-<c>GH109</c>).
    /// </summary>
    private static readonly Dictionary<int, int> DonationAmountValues = new()
    {
        [8] = 500,
        [9] = 1_000,
        [10] = 5_000,
        [11] = 10_000,
        [12] = 50_000,
        [14] = 5_000,
        [15] = 10_000,
        [16] = 50_000,
        [17] = 100_000,
        [18] = 500_000,
        [19] = 100,
        [20] = 500,
        [21] = 1_000,
        [22] = 5_000,
        [23] = 10_000,
        [24] = 50_000
    };

    /// <summary>
    /// Answers the altar's clicks: the building that opens a page, and the actions
    /// that page carries.
    /// </summary>
    /// <returns>Whether the click belonged to this dialogue's building pages.</returns>
    private async Task<bool> TryHandleGuildAltarActionAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        IReadOnlyList<int> path,
        int typedAmount,
        int selection,
        CancellationToken cancellationToken)
    {
        if (dialogIndex != GuildAltarDialogue.FunctionNumber)
        {
            return false;
        }

        // Depth two: a building was picked on page two.
        if (path.Count == 2 && IsBuildingEntry(path[1]))
        {
            await SendScriptedNpcDialogueReplyAsync(
                npc,
                GuildAltarDialogue,
                dialogIndex,
                path[1],
                await BuildingPageAsync(path[1], cancellationToken),
                cancellationToken);
            return true;
        }

        // Depth three: something was pressed on that building's own page.
        if (path.Count == 3 && IsBuildingEntry(path[1]))
        {
            switch (path[2])
            {
                case BuildAction or UpgradeAction or DeleteAction:
                    await ApplyBuildingActionAsync(
                        npc,
                        dialogIndex,
                        path[1],
                        path[2],
                        cancellationToken);
                    return true;
                case DonateGoldAction or DonateSilverAction or DonateBoundGoldAction:
                    // The donation row opens page four, whose entries are the
                    // client's own preset buttons: gold 8-12, silver 14-18, bound
                    // gold 19-24 (NpcFunAltar.lua 562-650, each drawn with
                    // Button:SetText).
                    await SendDonationMenuAsync(
                        npc,
                        dialogIndex,
                        path[1],
                        path[2],
                        cancellationToken);
                    return true;
                default:
                    // Worship: page four carries its explanation (310) and the four
                    // preset offerings, 10000/20000/50000/100000 points
                    // (NpcFunAltar.lua 490 and 529-552; the amounts are the client's
                    // own keys GH71-GH74).
                    await SendWorshipMenuAsync(
                        npc,
                        dialogIndex,
                        path[1],
                        cancellationToken);
                    return true;
            }
        }

        // Depth four: an amount was submitted on the page a donation row opened, so
        // the row two levels up - not the amount number - names the purse. The two
        // overlap (14-18 are both silver presets and building types), which is why
        // the depth has to decide.
        //
        // The amount comes from one of two places. A preset button puts its own
        // number in the path (8-12 gold, 14-18 silver, 19-24 bound gold; the worship
        // row's own 3-6 are the same shape but its account is not run yet), while the
        // row's own input box carries what the player typed just past the path, and
        // the submission ends with the client's confirm entry (measured 2026-09-23:
        // gold 1,5,4,0 and 123, silver 1,5,5,0 and 123, worship 2,13,10,0 and 123123 -
        // the typed number being the request's only populated word past the path). A
        // player who types nothing and presses the confirmation instead reads -1
        // there, so it donates nothing.
        if (path.Count == 4 &&
            path[2] is DonateGoldAction or DonateSilverAction or DonateBoundGoldAction)
        {
            var amount = path[3] == ConfirmEntry
                ? typedAmount
                : DonationAmountValues.GetValueOrDefault(path[3]);
            if (amount <= 0)
            {
                return false;
            }

            if (path[2] == DonateBoundGoldAction)
            {
                // Bound gold has no guild column to credit.
                await SendScriptedNpcDialogueReplyAsync(
                    npc,
                    GuildAltarDialogue,
                    dialogIndex,
                    selection,
                    [NotAvailableLine],
                    cancellationToken);
                return true;
            }

            await ApplyDonationAsync(
                npc,
                dialogIndex,
                path[2] == DonateGoldAction
                    ? GuildDonationKind.Gold
                    : GuildDonationKind.Silver,
                amount,
                cancellationToken);
            return true;
        }

        // Depth four ending in the confirm entry on the worship row: the same shape
        // as a donation submission, but what is spent is the character's guild
        // contribution and what it buys is offering points on the altar the row was
        // opened from. Only the typed amount counts here - the row's own presets
        // ("供奉10000/20000/50000/100000點") are deliberately not published, because
        // the client's own explanation asks for a typed number.
        if (path.Count == 4 &&
            path[2] == WorshipAction &&
            path[3] == ConfirmEntry &&
            typedAmount > 0)
        {
            await ApplyWorshipAsync(
                npc,
                dialogIndex,
                path[1],
                typedAmount,
                cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>Answers a donation row with that purse's preset amounts.</summary>
    private async Task SendDonationMenuAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int building,
        int row,
        CancellationToken cancellationToken)
    {
        var (textRow, inputRow) = row switch
        {
            DonateGoldAction => (GoldDonationTextRow, GoldInputRow),
            DonateSilverAction => (SilverDonationTextRow, SilverInputRow),
            _ => (BoundGoldDonationTextRow, BoundGoldInputRow)
        };
        Console.WriteLine(
            $"[guild] altar donation menu npc={npc.InteractionId} " +
            $"character={_character?.Name ?? "<none>"} building={building} " +
            $"row={row} sent=[{textRow},{inputRow}]");
        await SendScriptedNpcDialogueReplyAsync(
            npc,
            GuildAltarDialogue,
            dialogIndex,
            row,
            [textRow, inputRow],
            cancellationToken);
    }

    /// <summary>
    /// Answers the worship row with the page the client's own script draws for it:
    /// the offering explanation (row 310) and the four preset offerings
    /// (rows 3-6, "供奉10000/20000/50000/100000點", keys GH71-GH74).
    /// </summary>
    private async Task SendWorshipMenuAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int building,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[guild] altar worship menu npc={npc.InteractionId} " +
            $"character={_character?.Name ?? "<none>"} building={building} " +
            $"sent=[{WorshipTextRow},{WorshipInputRow}]");
        await SendScriptedNpcDialogueReplyAsync(
            npc,
            GuildAltarDialogue,
            dialogIndex,
            WorshipAction,
            [WorshipTextRow, WorshipInputRow],
            cancellationToken);
    }

    /// <summary>
    /// Whether a page number is a building the server has a type for.
    /// </summary>
    /// <remarks>
    /// The page numbers are the building's own, and the table
    /// (<c>guild_building_types</c>, from the client's <c>Consortia.xml</c>) holds
    /// 1-6, 10-19 and 30. The ten advanced altars sit at 20-29 and have no type, so
    /// they keep the script's description page untouched.
    /// </remarks>
    private static bool IsBuildingEntry(int number) =>
        number is (>= 1 and <= 6) or (>= 10 and <= 19) or 30;

    /// <summary>
    /// One building's page: its description, then delete, then the one action of
    /// {new, upgrade} the guild's state allows.
    /// </summary>
    /// <remarks>
    /// <b>The order is what keeps them apart.</b> The window lays its button slots
    /// out at 135, 155, 175, 195 ... (<c>NpcFun.lua</c>), while the altar script pins
    /// new and upgrade to 175 itself. Delete has no coordinate of its own and keeps
    /// whatever slot it is given, so sent last it lands on slot three - which is
    /// 175, exactly where the other button is (measured: "still overlapping" with
    /// the reply <c>[page, 2, 3]</c>). Sent before it, delete takes slot two at 155
    /// and the pinned action keeps 175.
    /// </remarks>
    private async Task<int[]> BuildingPageAsync(
        int building,
        CancellationToken cancellationToken)
    {
        var page = 200 + building;
        if (!IsBuildingEntry(building))
        {
            return [page];
        }

        var built = false;
        var level = 0;
        if (_guilds is not null && _character is not null)
        {
            var guild = await _guilds.TryReadGuildAsync(
                _character.Id,
                cancellationToken);
            var existing = guild?.Buildings.FirstOrDefault(
                candidate => candidate.BuildingType == building);
            built = existing is not null;
            level = existing?.Level ?? 0;
        }

        // The page's own status line, from the client's text table
        // (NpcFunAltar.lua 290 and 298): 100 is "公会尚未修建该建筑。", and 400+level
        // is "公会该建筑当前等级为：N". The line travels with the buttons so the
        // text and the buttons describe the same state - the client never clears a
        // text area by itself.
        var status = built ? 400 + level : BuildingAbsentLine;

        // The client's own page table fixes every coordinate, so the reply is the
        // page's whole button set in the order those coordinates allow:
        //   4 or 10 -> (25,135)   5 -> (25,155)   1 or 2 -> (25,175)
        //   3 and 6 carry no coordinate of their own, so they go last and take the
        //   window's free slots.
        // The two pairs that share a coordinate are the client's rule, not a
        // choice: the building's own state picks new or upgrade, and the building's
        // kind picks a donation or the altar's worship.
        var donating = building is (>= 10 and <= 19) or 30;
        return
        [
            page,
            status,
            donating ? WorshipAction : DonateGoldAction,
            DonateSilverAction,
            built ? UpgradeAction : BuildAction,
            DeleteAction,
            DonateBoundGoldAction
        ];
    }

    private async Task ApplyBuildingActionAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int buildingType,
        int selection,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null || !IsBuildingEntry(buildingType))
        {
            return;
        }

        var action = selection switch
        {
            BuildAction => GuildBuildingAction.Build,
            UpgradeAction => GuildBuildingAction.Upgrade,
            _ => GuildBuildingAction.Delete
        };
        Console.WriteLine(
            $"[guild] altar action npc={npc.InteractionId} " +
            $"character={_character.Name} building={buildingType} " +
            $"action={action} selection={selection}");
        if (GuildRateKeys.On(GuildRateKeys.BuildingRules))
        {
            // GUILD: costs, Footstone and rank checks (guild.building_rules).
            await ApplyBuildingActionWithRulesAsync(
                npc, dialogIndex, buildingType, selection, action, cancellationToken);
            return;
        }

        var guild = await _guilds.TryApplyBuildingAsync(
            _character.Id,
            buildingType,
            action,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (guild is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote("You are not in a guild."),
                cancellationToken,
                "GuildAltarNotAMember");
        }
        else
        {
            await ReportBuildingOutcomeAsync(
                guild,
                buildingType,
                cancellationToken);
            // Building or upgrading an altar changes the guild's layer of the
            // altar bonus, so the acting member's projection is re-read before
            // the window that shows the new level is drawn. Every other member
            // picks it up on their own next window.
            await PushAltarBonusAsync(cancellationToken);
            // The guild window's building page is the base info's own array, so it
            // has to be republished before the script redraws its entry.
            await SendGuildWindowAsync(cancellationToken);
        }

        await SendScriptedNpcDialogueReplyAsync(
            npc,
            GuildAltarDialogue,
            dialogIndex,
            selection,
            [await BuildingResultLineAsync(action, buildingType, cancellationToken)],
            cancellationToken);
    }

    /// <summary>
    /// The result line the client's own table draws for what just happened
    /// (<c>NpcFunAltar.lua</c> 1004-1008): build, upgrade and delete each have
    /// their own success line, and "your guild does not have this building" is the
    /// failure the client names for the other two.
    /// </summary>
    private async Task<int> BuildingResultLineAsync(
        GuildBuildingAction action,
        int buildingType,
        CancellationToken cancellationToken)
    {
        var guild = _guilds is null || _character is null
            ? null
            : await _guilds.TryReadGuildAsync(_character.Id, cancellationToken);
        var level = guild?.Buildings
            .FirstOrDefault(candidate => candidate.BuildingType == buildingType)
            ?.Level;
        return action switch
        {
            GuildBuildingAction.Build => level is null
                ? NoSuchBuildingLine
                : BuildSucceededLine,
            GuildBuildingAction.Upgrade => level is null
                ? NoSuchBuildingLine
                : UpgradeSucceededLine,
            _ => level is null ? DeleteSucceededLine : NoSuchBuildingLine
        };
    }

    private async Task ApplyDonationAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        GuildDonationKind kind,
        int amount,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null)
        {
            return;
        }

        var result = await _guilds.TryDonateAsync(
            _character.Id,
            kind,
            amount,
            DateTimeOffset.UtcNow,
            cancellationToken);
        var currency = kind == GuildDonationKind.Gold ? "gold" : "silver";
        switch (result.Outcome)
        {
            case GuildDonationOutcome.NotAMember:
                await _session.SendAsync(
                    PacketBuilder.ServerNote("You are not in a guild."),
                    cancellationToken,
                    "GuildDonationNotAMember");
                break;
            case GuildDonationOutcome.InsufficientFunds:
                await _session.SendAsync(
                    PacketBuilder.ServerNote(
                        $"You do not have {amount} {currency} to donate."),
                    cancellationToken,
                    "GuildDonationRefused");
                break;
            default:
                // The purse moved, so the character the client draws has to move
                // with it; the guild's own account travels in the base info. The
                // legacy database calls the character's premium gold "Stone", which
                // is why only the columns differ.
                if (kind == GuildDonationKind.Gold)
                {
                    _character.Gold = result.CharacterBalance;
                }
                else
                {
                    _character.Silver = result.CharacterBalance;
                }

                // The donation also raised the character's own contribution
                // balance, which the same status frame carries into the panel's
                // Contributepro row.
                await RefreshGuildPanelStatusAsync(
                    _character.Id,
                    cancellationToken);

                // The purse moved, so the client's own wallet readout has to be
                // told. This is the packet the rest of the server uses after a
                // money change (the NPC shop sale sends the same one); a player
                // detail refresh does not repaint the wallet.
                await _session.SendAsync(
                    BuildLocalPlayerStatusUpdate(),
                    cancellationToken,
                    "GuildDonationWalletStatus");
                await SendGuildWindowAsync(cancellationToken);
                await _session.SendAsync(
                    PacketBuilder.ServerNote(
                        $"You donated {amount} {currency}. Guild " +
                        $"{currency} is now {result.GuildBalance}; your " +
                        $"contribution is {result.Contribution}."),
                    cancellationToken,
                    "GuildDonationOutcome");
                break;
        }

        // The client's own result table: 1013 "恭喜你，捐赠成功了。", 1014 and 1015
        // the two "your gold/silver is not enough" lines, 1000 for a character in
        // no guild.
        var line = result.Outcome switch
        {
            GuildDonationOutcome.NotAMember => NotAMemberLine,
            GuildDonationOutcome.InsufficientFunds => kind == GuildDonationKind.Gold
                ? GoldShortLine
                : SilverShortLine,
            _ => DonationSucceededLine
        };
        await SendScriptedNpcDialogueReplyAsync(
            npc,
            GuildAltarDialogue,
            dialogIndex,
            amount,
            [line],
            cancellationToken);
    }

    /// <summary>
    /// One offering: the typed amount is spent from the character's guild
    /// contribution at one point per contribution (<c>NF_L0_GH68</c>) and written to
    /// the altar it was made on, both in one transaction.
    /// </summary>
    /// <remarks>
    /// The answer is the client's own result line - 1009 "你对神明的供奉已经成功了。",
    /// or 1010 "你的贡献點不够，所以供奉失败。" when the contribution is short, which
    /// the store leaves every number untouched for. The points themselves are only
    /// stored: the hourly drain and the percentage bonus they buy need rates the
    /// client states for two bands alone (<c>NF_L0_GH86</c> sends the rest to its
    /// website), so nothing is projected for them yet.
    /// </remarks>
    private async Task ApplyWorshipAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int buildingType,
        int points,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null)
        {
            return;
        }

        var result = await _guilds.TryWorshipAsync(
            _character.Id,
            buildingType,
            points,
            DateTimeOffset.UtcNow,
            cancellationToken);
        switch (result.Outcome)
        {
            case GuildWorshipOutcome.NotAMember:
                await _session.SendAsync(
                    PacketBuilder.ServerNote("You are not in a guild."),
                    cancellationToken,
                    "GuildWorshipNotAMember");
                break;
            case GuildWorshipOutcome.InsufficientContribution:
                await _session.SendAsync(
                    PacketBuilder.ServerNote(
                        $"You do not have {points} guild contribution to offer."),
                    cancellationToken,
                    "GuildWorshipRefused");
                break;
            default:
                await _session.SendAsync(
                    PacketBuilder.ServerNote(
                        $"You offered {points} points. This altar now holds " +
                        $"{result.Points} for you; your guild contribution is " +
                        $"{result.Contribution}."),
                    cancellationToken,
                    "GuildWorshipOutcome");
                // The points just offered are the member's own layer of the altar
                // bonus, so the projection is re-read and the client's status
                // repainted now that the balance moved. The offering also spent
                // guild contribution, which the panel prints, so that is re-read
                // first and repainted if the altar's own repaint did not already
                // carry it.
                var panelChanged = await RefreshGuildPanelStatusAsync(
                    _character.Id,
                    cancellationToken);
                var altarStatusSent =
                    await PushAltarBonusAsync(cancellationToken);
                if (panelChanged && !altarStatusSent)
                {
                    await _session.SendAsync(
                        BuildLocalPlayerStatusUpdate(),
                        cancellationToken,
                        "GuildWorshipContributionStatus");
                }
                break;
        }

        await SendScriptedNpcDialogueReplyAsync(
            npc,
            GuildAltarDialogue,
            dialogIndex,
            points,
            [
                result.Outcome switch
                {
                    GuildWorshipOutcome.NotAMember => NotAMemberLine,
                    GuildWorshipOutcome.InsufficientContribution =>
                        ContributionShortLine,
                    _ => WorshipSucceededLine
                }
            ],
            cancellationToken);
    }

    private async Task ReportBuildingOutcomeAsync(
        GuildSnapshot guild,
        int buildingType,
        CancellationToken cancellationToken)
    {
        var building = guild.Buildings.FirstOrDefault(
            candidate => candidate.BuildingType == buildingType);
        var line = building is null
            ? $"Guild building {buildingType} is gone."
            : $"Guild building {buildingType} is now level {building.Level}.";
        await _session.SendAsync(
            PacketBuilder.ServerNote(line),
            cancellationToken,
            "GuildAltarOutcome");
    }
}
