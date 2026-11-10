using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild window's leave action.
/// </summary>
/// <remarks>
/// Leaving is the one guild action whose whole effect is the server's: the
/// membership row and the character's duty are removed together (see
/// <c>PostgresGuildStore.RemoveMemberAsync</c>), and the roster every other
/// member is looking at is only correct once the server repaints it. The leaver
/// is answered with the notification its own handler reads
/// (<see cref="GuildExitProtocol"/>) plus the outcome line.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task HandleGuildExitRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !GuildExitProtocol.TryReadExitRequest(packet, out var name))
        {
            Console.WriteLine($"[guild] exit request ignored len={packet.Length}");
            return;
        }

        Console.WriteLine(
            $"[guild] exit request character={_character.Name} name='{name}'");

        if (!string.Equals(name, _character.Name, StringComparison.OrdinalIgnoreCase))
        {
            // The captured request names the character that sent it. A leave that
            // names somebody else is the window's action against another member,
            // which is a different message (10152) with no established layout, so
            // it is refused rather than guessed at.
            Console.WriteLine(
                $"[guild] exit refused character={_character.Name} name='{name}'");
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    "A guild member can only leave the guild themselves."),
                cancellationToken,
                "GuildExitRefused");
            return;
        }

        if (_guilds is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    "Guilds are unavailable on this server."),
                cancellationToken,
                "GuildExitUnavailable");
            return;
        }

        var guild = await _guilds.TryReadGuildAsync(
            _character.Id,
            cancellationToken);
        if (guild is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote("You are not in a guild."),
                cancellationToken,
                "GuildExitNotAMember");
            return;
        }

        if (await RefuseLordGuildExitAsync(guild, cancellationToken))
        {
            return; // GUILD: guild.lord_rules
        }

        var leaverId = _character.Id;
        var leaverName = _character.Name;
        var remaining = guild.Members
            .Where(member => member.CharacterId != leaverId)
            .Select(member => member.CharacterId)
            .ToArray();

        if (!await _guilds.RemoveMemberAsync(
                leaverId, cancellationToken,
                refuseLord: GuildLordRulesOn))
        {
            await RefuseLordGuildExitLateAsync(cancellationToken); // GUILD review fix
            return;
        }
        await _guilds.UpdateOnlineCountAsync(
            guild.GuildId,
            remaining.Count(_registry.IsCharacterOnline),
            DateTimeOffset.UtcNow,
            cancellationToken);
        foreach (var memberId in remaining)
        {
            await PushGuildWindowToMemberAsync(memberId, cancellationToken);
        }

        // Leaving clears the leaver's own duty and contribution, which their
        // character panel prints, and it clears the guild on their object, which
        // the players around them draw; the repaint goes out with the rest of the
        // leave answer.
        await RefreshCharacterGuildPresenceAsync(leaverId, cancellationToken);

        await _session.SendAsync(
            PacketBuilder.ServerNote($"You have left {guild.Name}."),
            cancellationToken,
            "GuildExitOutcome");
        // The echo is required, not optional: withheld (measured 2026-09-22
        // 20:11) the client kept drawing the guild it had just left and a second
        // click answered "you are not in a guild"; with it the window clears at
        // once. It is not what faults the create answer either - a session in
        // which no echo was ever sent crashed in that same handler.
        await _session.SendAsync(
            PacketBuilder.GuildExitNotification(leaverName),
            cancellationToken,
            "GuildExitNotification");
        Console.WriteLine(
            $"[guild] exit character={leaverName} guild='{guild.Name}' " +
            $"remaining={remaining.Length}");
    }

    /// <summary>
    /// Repaints one member's guild window from the guild as it stands now.
    /// </summary>
    /// <remarks>
    /// The roster is read again per member rather than reused from the caller's
    /// copy, because the caller's copy is the one taken before the change. The
    /// member's own character id is what identifies them to the client, so their
    /// own row is the one it draws as itself.
    /// </remarks>
    private async Task PushGuildWindowToMemberAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        if (_guilds is null ||
            !_registry.TryGetCharacterSession(characterId, out var session))
        {
            return;
        }

        var guild = await _guilds.TryReadGuildAsync(
            characterId,
            cancellationToken);
        if (guild is null)
        {
            return;
        }

        var members = guild.Members
            .Select(member => member with
            {
                Online = _registry.IsCharacterOnline(member.CharacterId)
            })
            .ToArray();
        await session.SendAsync(
            PacketBuilder.GuildBaseInfo(guild),
            cancellationToken,
            "GuildBaseInfoBroadcast");
        await session.SendAsync(
            PacketBuilder.GuildMemberList(members, characterId),
            cancellationToken,
            "GuildMemberListBroadcast");
        Console.WriteLine(
            $"[guild] window broadcast character={characterId} " +
            $"guild='{guild.Name}' members={guild.Members.Count} " +
            $"online={members.Count(static member => member.Online)} " +
            $"roster={FormatRoster(members)}");
    }
}
