using Godswar.Server.Application.Guilds;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild window's member management: changing a member's duty, and removing
/// one from the guild.
/// </summary>
/// <remarks>
/// The window only sends the request; everything else is the server's, including
/// telling the other members. Both actions repaint the roster of every member
/// still in the guild, and the removal additionally tells everyone - the removed
/// member included, who learns it from their own name in the message - that the
/// member is gone.
///
/// The rules enforced here are the ones the client's own refusal texts name, so a
/// request that reaches the server by other means cannot do more than the window
/// allows: <c>ERROR_03C2 理事以下职位没有相应权限</c> (duty 4 and above act) and
/// <c>ERROR_03C3 不能删除职位相同或更高的玩家</c> (only members below the
/// actor's own duty are touched).
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The duty the guild window cannot hand out: <c>Consortia_Job.ini</c> numbers
    /// 6 as 会长, and this server has no lordship-transfer flow.
    /// </summary>
    private const byte LordDuty = 6;

    private async Task HandleGuildDutyRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !GuildMemberActionProtocol.TryReadDutyRequest(
                packet,
                out var actorName,
                out var memberName,
                out var duty))
        {
            // The read is what tells the fields apart, so a refused read is
            // reported with the fields it did resolve plus the raw words: without
            // them a rejected duty request is indistinguishable from one that
            // never arrived.
            Console.WriteLine(
                $"[guild] duty request ignored len={packet.Length} " +
                $"{packet.ToHexPreview()}");
            DumpUnknownPacketWords(packet);
            return;
        }

        Console.WriteLine(
            $"[guild] duty request character={_character.Name} " +
            $"actor='{actorName}' member='{memberName}' duty={duty}");

        if (!string.Equals(actorName, _character.Name, StringComparison.OrdinalIgnoreCase))
        {
            await RefuseGuildActionAsync(
                "A guild member can only act as themselves.",
                "GuildDutyRefused",
                cancellationToken);
            return;
        }

        if (_guilds is null)
        {
            await RefuseGuildActionAsync(
                "Guilds are unavailable on this server.",
                "GuildDutyUnavailable",
                cancellationToken);
            return;
        }

        var guild = await _guilds.TryReadGuildAsync(
            _character.Id,
            cancellationToken);
        if (guild is null)
        {
            await RefuseGuildActionAsync(
                "You are not in a guild.",
                "GuildDutyNotAMember",
                cancellationToken);
            return;
        }

        if (duty >= LordDuty)
        {
            if (await TryTransferGuildLordshipAsync(
                    guild, actorName, memberName, duty, cancellationToken))
            {
                return; // GUILD: Change Lord (guild.lord_rules)
            }

            await RefuseGuildActionAsync(
                "Transferring guild leadership is not implemented.",
                "GuildDutyLordRefused",
                cancellationToken);
            return;
        }

        var refusal = CheckMemberAction(guild, memberName, out var target);
        if (refusal is not null)
        {
            await RefuseGuildActionAsync(
                refusal,
                "GuildDutyRefused",
                cancellationToken);
            return;
        }

        var actor = guild.Members.First(
            member => member.CharacterId == _character.Id);
        if (duty >= actor.Duty)
        {
            await RefuseGuildActionAsync(
                "You cannot set a duty at or above your own.",
                "GuildDutyPeerRefused",
                cancellationToken);
            return;
        }

        await _guilds.SetMemberDutyAsync(
            target!.CharacterId,
            guild.GuildId,
            duty,
            DateTimeOffset.UtcNow,
            cancellationToken);
        // The order matters and is not cosmetic. The line under a character's
        // name is not drawn live: the client materialises it as text when it
        // handles MSG_CONSORTIA_BASE_INFO - 0x4eda66 -> 0x4a8220 -> 0x4a7820
        // sprintf(obj+0xA35, "[%s-%s]", guild name, GetText("G"+obj+0x2A0)) - so
        // the member's own duty byte has to be on their client *before* the guild
        // info that triggers that sprintf. Measured 2026-10-02: with the notice
        // and the window push first, the promoted member's own client kept the
        // previous rank under its name (567-主管 while the roster and everybody
        // else showed 567-副会长), because the tag had been composed from the
        // old byte and nothing recomposed it - the 10199 map copy is dropped by
        // the member's own client, which addresses itself as 0x1448.
        await RefreshCharacterGuildPresenceAsync(
            target!.CharacterId,
            cancellationToken);
        await BroadcastGuildMemberActionAsync(
            guild,
            PacketBuilder.GuildDutyChanged(
                kind: 0,
                actorName,
                target.Name,
                duty),
            cancellationToken);
        Console.WriteLine(
            $"[guild] duty set character={_character.Name} " +
            $"guild='{guild.Name}' member='{target.Name}' duty={duty}");
    }

    private async Task HandleGuildMemberDelRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !GuildMemberActionProtocol.TryReadMemberDelRequest(
                packet,
                out var objectId,
                out var memberName))
        {
            Console.WriteLine(
                $"[guild] member del request ignored len={packet.Length}");
            return;
        }

        Console.WriteLine(
            $"[guild] member del request character={_character.Name} " +
            $"objectId={objectId} member='{memberName}'");

        if (_guilds is null)
        {
            await RefuseGuildActionAsync(
                "Guilds are unavailable on this server.",
                "GuildMemberDelUnavailable",
                cancellationToken);
            return;
        }

        var guild = await _guilds.TryReadGuildAsync(
            _character.Id,
            cancellationToken);
        if (guild is null)
        {
            await RefuseGuildActionAsync(
                "You are not in a guild.",
                "GuildMemberDelNotAMember",
                cancellationToken);
            return;
        }

        var refusal = CheckMemberAction(guild, memberName, out var target);
        if (refusal is not null)
        {
            await RefuseGuildActionAsync(
                refusal,
                "GuildMemberDelRefused",
                cancellationToken);
            return;
        }

        // The roster is snapshotted before the removal, because the removed member
        // has to be told too - their session is no longer in the guild once the
        // membership is gone, so it could not be found afterwards.
        var recipients = guild.Members.ToArray();
        if (!await _guilds.RemoveMemberAsync(
                target!.CharacterId, cancellationToken,
                refuseLord: GuildLordRulesOn))
        {
            await RefuseGuildActionAsync( // GUILD review fix: became Lord meanwhile
                "That member is now the Guild Lord and cannot be removed.",
                "GuildMemberDelLordRefused",
                cancellationToken);
            return;
        }
        var removed = PacketBuilder.GuildMemberRemoved(objectId, target.Name);
        foreach (var member in recipients)
        {
            if (_registry.TryGetCharacterSession(
                    member.CharacterId,
                    out var session))
            {
                await session.SendAsync(
                    removed,
                    cancellationToken,
                    "GuildMemberRemoved");
            }
        }

        foreach (var member in recipients)
        {
            if (member.CharacterId == target.CharacterId)
            {
                continue;
            }

            await PushGuildWindowToMemberAsync(
                member.CharacterId,
                cancellationToken);
        }

        // The removed member keeps no guild panel rows: their own duty and
        // contribution were cleared in the same transaction. The players around
        // them are told the same thing, or the nameplate keeps the old guild.
        await RefreshCharacterGuildPresenceAsync(
            target.CharacterId,
            cancellationToken);

        await _guilds.UpdateOnlineCountAsync(
            guild.GuildId,
            recipients.Count(member =>
                member.CharacterId != target.CharacterId &&
                _registry.IsCharacterOnline(member.CharacterId)),
            DateTimeOffset.UtcNow,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.ServerNote($"{target.Name} has left the guild."),
            cancellationToken,
            "GuildMemberDelOutcome");
        Console.WriteLine(
            $"[guild] member removed character={_character.Name} " +
            $"guild='{guild.Name}' member='{target.Name}'");
    }

    /// <summary>
    /// The checks both member actions share: the actor must be allowed to act,
    /// and the named member must be below the actor.
    /// </summary>
    /// <returns>The line to refuse with, or null when the action may proceed.</returns>
    private string? CheckMemberAction(
        GuildSnapshot guild,
        string memberName,
        out GuildMemberSnapshot? target)
    {
        target = guild.Members.FirstOrDefault(member =>
            string.Equals(
                member.Name,
                memberName,
                StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return "That character is not in your guild.";
        }

        var actor = guild.Members.First(member =>
            member.CharacterId == _character!.Id);
        if (actor.Duty < GuildMemberActionProtocol.ManagementDuty)
        {
            return "Only 理事 and above can manage members.";
        }

        if (target.Duty >= actor.Duty)
        {
            return "You cannot manage a member at your rank or above.";
        }

        return null;
    }

    /// <summary>
    /// Sends one member action to every online member of the guild.
    /// </summary>
    private async Task BroadcastGuildMemberActionAsync(
        GuildSnapshot guild,
        byte[] message,
        CancellationToken cancellationToken)
    {
        foreach (var member in guild.Members)
        {
            if (_registry.TryGetCharacterSession(
                    member.CharacterId,
                    out var session))
            {
                await session.SendAsync(
                    message,
                    cancellationToken,
                    "GuildMemberAction");
            }
        }

        foreach (var member in guild.Members)
        {
            await PushGuildWindowToMemberAsync(
                member.CharacterId,
                cancellationToken);
        }
    }

    private Task RefuseGuildActionAsync(
        string reason,
        string label,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.ServerNote(reason),
            cancellationToken,
            label);
}
