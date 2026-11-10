using Godswar.Server.Application.Guilds;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// GUILD (2026-11-03): the Lord's hand-over, and the Lord's leave refusal.
/// </summary>
/// <remarks>
/// <para>
/// The guild window's member menu has a Change Lord button
/// (<c>UI/XML/Consortia.xml</c> <c>ChangeChairman</c>, id 390166). From the
/// client's code: <c>sub_4ba540</c> builds C2S 10151 (MSG_CONSORTIA_DUTY, size
/// 0x46) with the duty byte at <c>+0x41</c>, and it has two callers - the
/// window's promote at <c>0x540781</c> (pushes the member's duty + 1) and
/// <c>0x597fab</c>, which pushes the constant <c>6</c>. So a hand-over arrives
/// as an ordinary duty request whose new duty is 6 (Lord). The 2008 server
/// handled it the same way in <c>CPlayer::AppointDuty</c>.
/// </para>
/// <para>
/// Only the Lord may do it, only to another member of the guild, and the old
/// Lord becomes duty 2 (the 2008 server's CONSORTIA_MEMBER). Everyone is told
/// with two ordinary duty notices, after each character's own duty byte was
/// repainted (the order the duty handler above documents).
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <returns>True when the request was a hand-over and has been answered.</returns>
    private async Task<bool> TryTransferGuildLordshipAsync(
        GuildSnapshot guild,
        string actorName,
        string memberName,
        byte duty,
        CancellationToken cancellationToken)
    {
        if (duty != GuildBuildingPolicy.LordDuty ||
            _guilds is null ||
            _character is null ||
            !GuildRateKeys.On(GuildRateKeys.LordRules))
        {
            return false;
        }

        var actor = guild.Members.First(
            member => member.CharacterId == _character.Id);
        var heir = guild.Members.FirstOrDefault(member =>
            string.Equals(
                member.Name,
                memberName,
                StringComparison.OrdinalIgnoreCase));
        string? refusal = actor.Duty != GuildBuildingPolicy.LordDuty
            ? "Only the Guild Lord can hand over the lordship."
            : heir is null
                ? "That character is not in your guild."
                : heir.CharacterId == actor.CharacterId
                    ? "You are already the Guild Lord."
                    : null;
        if (refusal is not null || heir is null)
        {
            await RefuseGuildActionAsync(
                refusal ?? "That character is not in your guild.",
                "GuildLordTransferRefused",
                cancellationToken);
            return true;
        }

        if (!await _guilds.TransferLordshipAsync(
                guild.GuildId,
                actor.CharacterId,
                heir.CharacterId,
                DateTimeOffset.UtcNow,
                cancellationToken))
        {
            await RefuseGuildActionAsync(
                "The lordship could not be handed over.",
                "GuildLordTransferFailed",
                cancellationToken);
            return true;
        }

        await RefreshCharacterGuildPresenceAsync(heir.CharacterId, cancellationToken);
        await RefreshCharacterGuildPresenceAsync(actor.CharacterId, cancellationToken);
        foreach (var member in guild.Members)
        {
            if (!_registry.TryGetCharacterSession(member.CharacterId, out var session))
            {
                continue;
            }

            await session.SendAsync(
                PacketBuilder.GuildDutyChanged(
                    kind: 0, actorName, heir.Name, GuildBuildingPolicy.LordDuty),
                cancellationToken,
                "GuildLordTransfer");
            await session.SendAsync(
                PacketBuilder.GuildDutyChanged(
                    kind: 0,
                    actorName,
                    actor.Name,
                    (byte)Infrastructure.Guilds.PostgresGuildStore.FormerLordDuty),
                cancellationToken,
                "GuildLordTransfer");
        }

        foreach (var member in guild.Members)
        {
            await PushGuildWindowToMemberAsync(member.CharacterId, cancellationToken);
        }

        await _session.SendAsync(
            PacketBuilder.ServerNote($"{heir.Name} is now the Guild Lord."),
            cancellationToken,
            "GuildLordTransferOutcome");
        Console.WriteLine(
            $"[guild] lordship handed over guild='{guild.Name}' " +
            $"from='{actor.Name}' to='{heir.Name}'");
        return true;
    }

    /// <summary>
    /// The guide: a Lord "should pass his position to another first" before
    /// quitting. With the switch on, a Lord's leave request is refused while
    /// anyone else is in the guild; a Lord alone in it may still leave
    /// (disbanding, 10147, is not built).
    /// </summary>
    /// <returns>True when the leave was refused.</returns>
    private async Task<bool> RefuseLordGuildExitAsync(
        GuildSnapshot guild,
        CancellationToken cancellationToken)
    {
        if (_character is null || !GuildRateKeys.On(GuildRateKeys.LordRules))
        {
            return false;
        }

        var self = guild.Members.FirstOrDefault(
            member => member.CharacterId == _character.Id);
        // A Lord who is the guild's only member has nobody to hand over to, and
        // disbanding is not built, so he leaves as before (the empty guild row
        // stays, as it did before this patch).
        if (self is null ||
            self.Duty != GuildBuildingPolicy.LordDuty ||
            guild.Members.Count <= 1)
        {
            return false;
        }

        Console.WriteLine(
            $"[guild] exit refused (lord) character={_character.Name} " +
            $"guild='{guild.Name}'");
        await _session.SendAsync(
            PacketBuilder.ServerNote(
                "The Guild Lord must hand the lordship to another member " +
                "(guild window, Change Lord) before leaving the guild."),
            cancellationToken,
            "GuildExitLordRefused");
        return true;
    }

    /// <summary>GUILD review fix: whether a leave or kick may not remove a Lord.</summary>
    private static bool GuildLordRulesOn => GuildRateKeys.On(GuildRateKeys.LordRules);

    /// <summary>
    /// GUILD review fix: the store refused the leave under the guild lock, because
    /// a hand-over made this character Lord after the leave's own check.
    /// </summary>
    private async Task RefuseLordGuildExitLateAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[guild] exit refused (became lord) character={_character?.Name}");
        await _session.SendAsync(
            PacketBuilder.ServerNote(
                "The Guild Lord must hand the lordship to another member " +
                "(guild window, Change Lord) before leaving the guild."),
            cancellationToken,
            "GuildExitLordRefused");
    }
}
