using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Inspecting another player: <c>S2C 10199</c> (<c>_MSG_CD_INFO</c>, the message
/// <see cref="PacketBuilder.PlayerTitleInfo"/> builds) is what gives the target
/// window the guild name and the guild position, and its <c>body+0x44</c> byte is
/// the "this player is in a guild" flag the window checks before it prints either
/// row.
/// </summary>
/// <remarks>
/// The client's case for this opcode is <c>0x4ecc94</c>: it resolves the object id
/// at <c>body+0</c>, then writes <c>body+0x04</c> (64 bytes) to <c>object+0xB35</c>
/// (the name the target window's Union row reads), <c>body+0x44</c> to
/// <c>object+0xA34</c> (the flag), <c>body+0x46</c> to <c>object+0x2A0</c> (the
/// duty printed as "G%d") and <c>body+0x48</c> to <c>object+0x99C</c> (the title
/// id). The window's fill code (<c>0x62c432</c>) clears both rows when the flag is
/// zero, which is what happened while the name and duty travelled without it.
/// </remarks>
internal static class GuildProfileTitleChecks
{
    public const string CheckName =
        "Player title info carries the guild profile (10199 flag at +0x44)";

    private const int CharacterId = 4_244;
    private const uint ObjectId = 0x0000_4321;

    public static Task RunAsync()
    {
        var character = new GameCharacter
        {
            Id = CharacterId,
            AccountId = 4_245,
            Name = "ProfileHero",
            Camp = GameDefaults.AthensCamp,
            SelectedTitleId = 0x1234
        };

        GuildPanelStatusCache.Set(
            CharacterId,
            new GuildPanelStatus(Duty: 4, Contribution: 99, GuildName: "567"));
        var member = PacketBuilder.PlayerTitleInfo(character, ObjectId);
        Check.Equal(80, member.Length, "guild profile packet length");
        Check.Equal(
            (ushort)0x27D7,
            BinaryPrimitives.ReadUInt16LittleEndian(member.AsSpan(2)),
            "guild profile opcode");
        Check.Equal(
            ObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(4)),
            "guild profile object id");
        Check.Equal("567", ReadFixedAscii(member.AsSpan(8, 64)), "guild name field");
        Check.Equal(1, member[72], "in-guild flag at body+0x44");
        Check.Equal(4, member[74], "duty at body+0x46");
        Check.Equal(
            0x1234u,
            BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(76)),
            "selected title id at body+0x48");

        GuildPanelStatusCache.Clear(CharacterId);
        var guildless = PacketBuilder.PlayerTitleInfo(character, ObjectId);
        Check.Equal(0, guildless[72], "a character with no guild clears the flag");
        Check.Equal(0, guildless[74], "a character with no guild has no duty");
        Check.Equal(
            string.Empty,
            ReadFixedAscii(guildless.AsSpan(8, 64)),
            "a character with no guild has no guild name");

        return Task.CompletedTask;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(
            end < 0 ? field : field[..end]);
    }
}
