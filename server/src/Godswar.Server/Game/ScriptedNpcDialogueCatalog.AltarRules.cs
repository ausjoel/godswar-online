using Godswar.Server.Application.Guilds;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Guilds;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// GUILD (2026-11-03): the altar's build / upgrade / demolish buttons under the
/// IGG guide's rules (<c>guild.building_rules</c> = 1).
/// </summary>
/// <remarks>
/// Every refusal is answered with the client's own result line from
/// <c>NpcFunAltar.lua</c> page five (<c>NF_L0_GH1000</c>-<c>GH1016</c>): 1002
/// "Your guild is short of Gold.", 1003 "... short of Silver.", 1004 "The
/// building has reached the top rank.", 1007 "Your guild doesn't have this
/// building.", 1011 "This function hasn't been released yet." and 1016 "You
/// can't do that." Where 1016 alone would not tell the player why, one line in
/// the chat log (the same <c>ServerNote</c> the altar already uses) says it.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private const int GuildShortOfGoldLine = 1002;

    private const int GuildShortOfSilverLine = 1003;

    private const int BuildingTopRankLine = 1004;

    private const int CannotDoThatLine = 1016;

    private async Task ApplyBuildingActionWithRulesAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int buildingType,
        int selection,
        GuildBuildingAction action,
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null)
        {
            return;
        }

        var (outcome, guild) = await _guilds.TryApplyBuildingWithRulesAsync(
            _character.Id,
            buildingType,
            action,
            GuildRateKeys.DutyOf(GuildRateKeys.BuildDuty),
            GuildRateKeys.DutyOf(GuildRateKeys.DeleteDuty),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var note = GuildBuildingNote(outcome, guild);
        if (note is not null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(note),
                cancellationToken,
                "GuildAltarRuleNote");
        }

        if (outcome == GuildBuildingOutcome.Done && guild is not null)
        {
            await ReportBuildingOutcomeAsync(guild, buildingType, cancellationToken);
            await PushAltarBonusAsync(cancellationToken);
            // The purse and (for the Footstone) the guild level changed, which
            // every member's window shows.
            foreach (var member in guild.Members)
            {
                if (member.CharacterId == _character.Id)
                {
                    await SendGuildWindowAsync(cancellationToken);
                }
                else
                {
                    await PushGuildWindowToMemberAsync(
                        member.CharacterId,
                        cancellationToken);
                }
            }
        }

        await SendScriptedNpcDialogueReplyAsync(
            npc,
            GuildAltarDialogue,
            dialogIndex,
            selection,
            [GuildBuildingLine(outcome, action)],
            cancellationToken);
    }

    internal static int GuildBuildingLine(
        GuildBuildingOutcome outcome,
        GuildBuildingAction action) => outcome switch
        {
            GuildBuildingOutcome.Done => action switch
            {
                GuildBuildingAction.Build => BuildSucceededLine,
                GuildBuildingAction.Upgrade => UpgradeSucceededLine,
                _ => DeleteSucceededLine
            },
            GuildBuildingOutcome.NotAMember => NotAMemberLine,
            GuildBuildingOutcome.ShortOfGold => GuildShortOfGoldLine,
            GuildBuildingOutcome.ShortOfSilver => GuildShortOfSilverLine,
            GuildBuildingOutcome.TopRank => BuildingTopRankLine,
            GuildBuildingOutcome.NoSuchBuilding => NoSuchBuildingLine,
            GuildBuildingOutcome.UnknownBuilding => NotAvailableLine,
            _ => CannotDoThatLine
        };

    private static string? GuildBuildingNote(
        GuildBuildingOutcome outcome,
        Application.Guilds.GuildSnapshot? guild) => outcome switch
        {
            GuildBuildingOutcome.NoAuthority =>
                "Your guild rank does not allow that.",
            GuildBuildingOutcome.FootstoneTooLow =>
                "Upgrade the Guild Footstone first: no building can be " +
                "above the Footstone's level " +
                $"({GuildBuildingPolicy.EffectiveFootstoneLevel(guild?.Buildings.FirstOrDefault(static b => b.BuildingType == GuildBuildingPolicy.FootstoneType)?.Level, guild?.Level ?? 1)}).",
            GuildBuildingOutcome.AlreadyBuilt =>
                "Your guild already has this building.",
            GuildBuildingOutcome.FootstoneProtected =>
                "The Guild Footstone cannot be demolished.",
            _ => null
        };
}
