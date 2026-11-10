using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// Publishes the guild window's List tab: every guild of the character's own
/// camp.
/// </summary>
/// <remarks>
/// The tab is filled by <c>MSG_CONSORTIA_ELEMENT_LIST</c> (<c>10157</c>), whose
/// shape is recovered in <see cref="PacketBuilder.GuildElementList"/>. Unlike the
/// rest of the window this answer does not depend on the character having a
/// guild - the list is how a guildless player finds one - so it is sent on every
/// guild-window request and on the registrar's dialog, beside the guild's own
/// records.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task SendGuildListAsync(
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null)
        {
            return;
        }

        var guilds = await _guilds.TryReadCampGuildsAsync(
            _character.Camp,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.GuildElementList(guilds),
            cancellationToken,
            "GuildElementList");
        Console.WriteLine(
            $"[guild] list sent character={_character.Name} " +
            $"camp={_character.Camp} guilds={guilds.Count} " +
            $"entries={string.Join(',', guilds.Select(static guild =>
                $"{guild.Name}(id={guild.GuildId},lv={guild.Level}," +
                $"members={guild.MemberCount}/{guild.MemberLimit}," +
                $"online={guild.OnlineCount}," +
                $"buildings={guild.FootstoneLevel}/{guild.BuildingCount}," +
                $"silver={guild.Silver},gold={guild.Gold})"))}");
    }
}
