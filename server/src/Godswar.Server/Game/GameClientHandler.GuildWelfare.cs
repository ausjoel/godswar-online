using Godswar.Server.Application.Guilds;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Guilds;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// GUILD (2026-11-03): the [Guild] Member Advisor's double EXP / TP claims and
/// the [Guild] Quest Supervisor's quest release.
/// </summary>
/// <remarks>
/// <para>
/// Rules (IGG guide igg-acid=67, "Guild Welfare" and "Guild Quests"): members
/// claim 1, 2, 3 or 4 hours (the client offers up to 24) of double EXP and
/// double TP, "effective immediately", out of a weekly allowance the advisor can
/// report; the Lord, Deputies and Directors release the guild quests. The
/// client's own text fixes the allowance: "The double EXP time for this week will
/// be determined by the guild's current double EXP level plus two"
/// (<c>NF_L0_UNION400</c>), and the level goes to 22 (<c>NF_L0_UNION0570</c>).
/// The guide resets the advisor's counts on Saturday, so the week starts on
/// Saturday (realm calendar).
/// </para>
/// <para>
/// Every number sent is a line of the client's own scripts:
/// <c>NpcFunUionTime.lua</c> (function 8) page two 11-20 / 21-30 (the hour
/// buttons), 240 / 241 ("your current period hasn't expired yet"), and
/// <c>500*n + 31</c> / <c>500*n + 3</c> / <c>500*n + 4</c> (level n-1, EXP hours
/// left n, TP hours left n); page three 350 / 351 (claimed) and 352
/// ("insufficient time left to claim"). <c>NpcFunGuildQuest.lua</c> (function 6)
/// page two 1000 ("The guild quest has been released.") and 1001 ("You are not
/// the director, you cannot issue a guild quest.").
/// </para>
/// <para>
/// The boost is the client's own status: <c>Status.ini</c> 1000-1009 are
/// "[Guild] Double EXP" for 1, 2, 3, 4, 6, 8, 12, 16, 20 and 24 hours (kind 100,
/// +100% EXP) and 1010-1019 "[Guild] Double TP" for the same hours (kind 101,
/// 2x TP), which <c>ExperienceBoostKinds</c> already applies.
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>The hours of the advisor's ten claim buttons (UNION11-20 / 21-30).</summary>
    internal static readonly int[] GuildDoubleClaimHours = [1, 2, 3, 4, 6, 8, 12, 16, 20, 24];

    private const int GuildDoubleExperienceMenu = 3;

    private const int GuildDoubleTalentMenu = 4;

    private const int GuildDoubleInquiry = 5;

    /// <summary>
    /// The advisor's claim buttons: entry, hours, client status id, and whether it
    /// is the TP kind. Null for any other entry.
    /// </summary>
    internal static (int Hours, int StatusId, bool Talent)? GuildDoubleClaimOf(int entry) =>
        entry switch
        {
            >= 11 and <= 20 => (GuildDoubleClaimHours[entry - 11], 1000 + entry - 11, false),
            >= 21 and <= 30 => (GuildDoubleClaimHours[entry - 21], 1010 + entry - 21, true),
            _ => null
        };

    /// <summary>The advisor's inquiry page, encoded the way its script reads it.</summary>
    internal static int[] GuildDoubleInquiryLines(int level, int experienceLeft, int talentLeft) =>
    [
        500 * (level + 1) + 31,
        500 * Math.Max(0, experienceLeft) + 3,
        500 * Math.Max(0, talentLeft) + 4
    ];

    internal static int GuildDoubleWeeklyHours(int level) => Math.Clamp(level, 0, 22) + 2;

    /// <returns>True when the click belonged to a guild service and was answered.</returns>
    private async Task<bool> TryHandleGuildWelfareAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null)
        {
            return false;
        }

        if (IsGuildNpc(npc, dialogue, GuildMemberAdvisorDialogue, "Athens_051", "Sparta_051") &&
            GuildRateKeys.On(GuildRateKeys.DoubleClaims))
        {
            return await HandleGuildMemberAdvisorAsync(
                npc, dialogue, dialogIndex, selection, cancellationToken);
        }

        if (IsGuildNpc(npc, dialogue, GuildQuestSupervisorDialogue, "Athens_038", "Sparta_039") &&
            GuildRateKeys.On(GuildRateKeys.QuestRules) &&
            selection is GuildQuestCatalog.DeliveryKind or GuildQuestCatalog.KillKind)
        {
            await HandleGuildQuestReleaseAsync(
                npc, dialogue, dialogIndex, selection, cancellationToken);
            return true;
        }

        if (IsGuildNpc(npc, dialogue, GuildAltarDialogue, "Athens_050", "Sparta_050") &&
            selection == 1 &&
            GuildRateKeys.On(GuildRateKeys.BuildingRules))
        {
            // The altar's "Basic Buildings" page with the Guild Footstone (1) and
            // the Common Tent (4) added. The stock table left them out because a
            // selection-keyed table could not tell page one's 1/4 from page two's;
            // the altar handler above now resolves the page-two click by its path
            // ([1, 1] = the Footstone), so they can be offered. Without the
            // Footstone the guide's rule would cap every building at level 1.
            // Positions are the script's own (NpcFunAltar.lua page 2: 1 at 25,95,
            // 4 at 25,115, 5 at 25,135, 6 at 25,155; 3 has none and stays last).
            await SendScriptedNpcDialogueReplyAsync(
                npc, dialogue, dialogIndex, selection, BasicBuildingsPage, cancellationToken);
            return true;
        }

        return false;
    }

    internal static readonly int[] BasicBuildingsPage = [1, 4, 5, 6, 3];

    /// <summary>
    /// The routing table hands out its own copies of the dialogues, so the NPC
    /// is matched by its key (the routing table's own names) and the function.
    /// </summary>
    private static bool IsGuildNpc(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        ScriptedNpcDialogue expected,
        string athensKey,
        string spartaKey) =>
        dialogue.FunctionNumber == expected.FunctionNumber &&
        (npc.NpcKey == athensKey || npc.NpcKey == spartaKey);

    private async Task<bool> HandleGuildMemberAdvisorAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        CancellationToken cancellationToken)
    {
        var level = (int)Application.Rates.ServerRates.Current.Absolute(
            GuildRateKeys.DoubleLevel);
        var allowance = GuildDoubleWeeklyHours(level);
        var weekStart = RealmCalendar.GetWeekStart(
            _realmCalendar.GetDay(DateTimeOffset.UtcNow),
            DayOfWeek.Saturday);
        int[] reply;
        switch (selection)
        {
            case GuildDoubleExperienceMenu or GuildDoubleTalentMenu or GuildDoubleInquiry:
            {
                var week = await _guilds!.ReadDoubleWeekAsync(
                    _character!.Id, weekStart, cancellationToken);
                if (!week.InGuild)
                {
                    await _session.SendAsync(
                        PacketBuilder.ServerNote("You are not in a guild."),
                        cancellationToken,
                        "GuildAdvisorNotAMember");
                }

                var talent = selection == GuildDoubleTalentMenu;
                reply = selection switch
                {
                    GuildDoubleInquiry => GuildDoubleInquiryLines(
                        level,
                        week.InGuild ? allowance - week.ExperienceHoursUsed : 0,
                        week.InGuild ? allowance - week.TalentHoursUsed : 0),
                    _ when (talent ? week.TalentActive : week.ExperienceActive) =>
                        [talent ? 241 : 240],
                    _ => dialogue.Steps[selection]
                };
                break;
            }

            default:
            {
                if (GuildDoubleClaimOf(selection) is not { } claim)
                {
                    return false; // 1/2 (raise the level) keep the stock answer.
                }

                var outcome = await _guilds!.ClaimDoubleAsync(
                    _character!.Id,
                    claim.Talent,
                    claim.Hours,
                    claim.StatusId,
                    allowance,
                    weekStart,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                var note = outcome switch
                {
                    GuildDoubleClaimOutcome.NotAMember => "You are not in a guild.",
                    GuildDoubleClaimOutcome.StillActive =>
                        "Your current guild double period has not run out yet.",
                    _ => null
                };
                if (note is not null)
                {
                    await _session.SendAsync(
                        PacketBuilder.ServerNote(note),
                        cancellationToken,
                        "GuildAdvisorClaimNote");
                }

                if (outcome == GuildDoubleClaimOutcome.Granted)
                {
                    await SendExperienceBoostStatusAsync(
                        "guild-double-claim",
                        cancellationToken);
                }

                reply = outcome == GuildDoubleClaimOutcome.Granted
                    ? [claim.Talent ? 351 : 350]
                    : [352];
                Console.WriteLine(
                    $"[guild] advisor claim character={_character.Name} " +
                    $"entry={selection} hours={claim.Hours} outcome={outcome}");
                break;
            }
        }

        await SendScriptedNpcDialogueReplyAsync(
            npc, dialogue, dialogIndex, selection, reply, cancellationToken);
        return true;
    }

    private async Task HandleGuildQuestReleaseAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        CancellationToken cancellationToken)
    {
        var guild = await _guilds!.TryReadGuildAsync(_character!.Id, cancellationToken);
        var self = guild?.Members.FirstOrDefault(
            member => member.CharacterId == _character.Id);
        var allowed = self is not null &&
            self.Duty >= GuildRateKeys.DutyOf(GuildRateKeys.QuestReleaseDuty);
        if (allowed)
        {
            await _guilds.ReleaseGuildQuestAsync(
                _character.Id,
                selection,
                QuestDailyState.Today(),
                DateTimeOffset.UtcNow,
                cancellationToken);
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    (selection == GuildQuestCatalog.DeliveryKind
                        ? "Delivery"
                        : "Monster") +
                    " guild quests are released for your guild today. " +
                    "Members can take them from the Quest Supervisor."),
                cancellationToken,
                "GuildQuestReleased");
        }
        else if (guild is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote("You are not in a guild."),
                cancellationToken,
                "GuildQuestNotAMember");
        }

        Console.WriteLine(
            $"[guild] quest release character={_character.Name} kind={selection} " +
            $"allowed={allowed}");
        await SendScriptedNpcDialogueReplyAsync(
            npc, dialogue, dialogIndex, selection, [allowed ? 1000 : 1001],
            cancellationToken);
    }

    /// <summary>
    /// Accept gate for the guild quests (<c>guild.quest_rules</c>): the character
    /// needs a guild whose officers released that quest kind today. Any other
    /// quest is unaffected.
    /// </summary>
    private async Task<bool> GuildQuestAcceptRefusedAsync(
        uint questId,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null ||
            !GuildRateKeys.On(GuildRateKeys.QuestRules) ||
            !GuildQuestCatalog.TryFind(questId, out var reward))
        {
            return false;
        }

        if (await _guilds.IsGuildQuestReleasedAsync(
                _character.Id,
                reward.Kind,
                QuestDailyState.Today(),
                cancellationToken))
        {
            return false;
        }

        Console.Error.WriteLine(
            $"[quest-accept] guild quest not released character={_character.Name} " +
            $"quest={questId} kind={reward.Kind}");
        await _session.SendAsync(
            PacketBuilder.ServerNote(
                "This is a guild quest: your guild's Lord, a Deputy or a Director " +
                "must release it at the Quest Supervisor today."),
            cancellationToken,
            "GuildQuestNotReleased");
        return true;
    }

    /// <summary>
    /// Hand-in hook: a guild quest's "Guild Gold" is the guild's, not the
    /// player's, so it is taken out of the personal payout - but only while the
    /// character is in a guild, the same condition the guild credit below needs,
    /// so a hand-in never loses the gold on both sides.
    /// </summary>
    private async Task<QuestRewardPayout> GuildQuestPersonalPayoutAsync(
        uint questId,
        QuestRewardPayout payout,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null ||
            !GuildRateKeys.On(GuildRateKeys.QuestRules) ||
            !GuildQuestCatalog.TryFind(questId, out _))
        {
            return payout;
        }

        var inGuild = await _guilds.IsGuildMemberAsync(_character.Id, cancellationToken);
        return GuildQuestPersonalPayout(questId, payout, inGuild);
    }

    internal static QuestRewardPayout GuildQuestPersonalPayout(
        uint questId,
        QuestRewardPayout payout,
        bool inGuild) =>
        inGuild &&
        GuildRateKeys.On(GuildRateKeys.QuestRules) &&
        GuildQuestCatalog.TryFind(questId, out var reward) &&
        reward.GuildGold > 0
            ? payout with { Gold = 0 }
            : payout;

    /// <summary>Hand-in hook: pays the guild half of a guild quest.</summary>
    private async Task PayGuildQuestRewardAsync(
        uint questId,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null ||
            !GuildRateKeys.On(GuildRateKeys.QuestRules) ||
            !GuildQuestCatalog.TryFind(questId, out var reward))
        {
            return;
        }

        try
        {
            var paid = await _guilds.CreditGuildQuestAsync(
                _character.Id,
                reward.GuildGold,
                reward.GuildFund,
                reward.Contribution,
                DateTimeOffset.UtcNow,
                cancellationToken);
            Console.WriteLine(
                $"[guild] quest reward character={_character.Name} quest={questId} " +
                $"guildGold={reward.GuildGold} guildFund={reward.GuildFund} " +
                $"contribution={reward.Contribution} paid={paid}");
            if (paid)
            {
                // Cache only, no frame: the hand-in's own status frame, sent
                // next, carries the new contribution, and no guild frame is put
                // inside the captured hand-in order. The guild window shows the
                // new purse the next time it is drawn.
                await RefreshGuildPanelStatusAsync(_character.Id, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[guild] quest reward failed character={_character.Name} " +
                $"quest={questId} {ex.GetType().Name}: {ex.Message}");
        }
    }
}
