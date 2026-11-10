using Godswar.Server.Application.Guilds;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild window's proclamation edit.
/// </summary>
/// <remarks>
/// The window sends the text and nothing else happens on its own: the guild row
/// is the server's, and every other member's window has to be told. The text is
/// published twice per member - as the placard message, which is what makes an
/// open window repaint, and inside the base info, which is what a window opened
/// afterwards reads it from.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task HandleGuildTextRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !GuildTextProtocol.TryReadTextRequest(packet, out var text))
        {
            Console.WriteLine(
                $"[guild] text request ignored len={packet.Length}");
            return;
        }

        var proclamation = text.Length > GuildTextProtocol.MaximumStoredLength
            ? text[..GuildTextProtocol.MaximumStoredLength]
            : text;
        Console.WriteLine(
            $"[guild] text request character={_character.Name} " +
            $"text='{proclamation}'");

        if (_guilds is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote("Guilds are unavailable on this server."),
                cancellationToken,
                "GuildTextUnavailable");
            return;
        }

        var guild = await _guilds.TrySetProclamationAsync(
            _character.Id,
            proclamation,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (guild is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote("You are not in a guild."),
                cancellationToken,
                "GuildTextNotAMember");
            return;
        }

        await BroadcastGuildPlacardAsync(guild, proclamation, cancellationToken);
        Console.WriteLine(
            $"[guild] proclamation set character={_character.Name} " +
            $"guild='{guild.Name}' members={guild.Members.Count}");
    }

    /// <summary>
    /// Tells every online member of the guild what the proclamation now is.
    /// </summary>
    /// <remarks>
    /// One encoding is shared by every recipient: the base info carries no
    /// per-recipient field (unlike the roster, whose online flags are counted per
    /// reader), so re-encoding it per member would only produce identical bytes.
    /// </remarks>
    private async Task BroadcastGuildPlacardAsync(
        GuildSnapshot guild,
        string proclamation,
        CancellationToken cancellationToken)
    {
        var placard = PacketBuilder.GuildPlacard(proclamation);
        var baseInfo = PacketBuilder.GuildBaseInfo(guild);
        foreach (var member in guild.Members)
        {
            if (!_registry.TryGetCharacterSession(
                    member.CharacterId,
                    out var session))
            {
                continue;
            }

            await session.SendAsync(
                placard,
                cancellationToken,
                "GuildPlacard");
            await session.SendAsync(
                baseInfo,
                cancellationToken,
                "GuildBaseInfoProclamation");
        }
    }
}
