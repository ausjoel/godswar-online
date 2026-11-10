using System.Buffers.Binary;
using Godswar.Server.Application.Mentor;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// ECONOMY mentor: off by default (message 10234 keeps the unknown path), the guide's level rules, and the frame layouts
// read from the client (10234 proven; 10235/10236 sizes unproven, so their field offsets are pinned here).
internal static class EconomyMentorChecks
{
    public const string CheckName = "Economy mentor Master and Apprentice rules and frames";

    public static Task RunAsync()
    {
        Check.True(Environment.GetEnvironmentVariable("GODSWAR_MENTOR") == "1" || !MentorPolicy.Enabled,
            "the mentor system is off unless GODSWAR_MENTOR=1");
        Check.True(MentorPolicy.LevelsAllow(80, 48), "80 may teach 48 (gap 32, apprentice below 49)");
        Check.True(MentorPolicy.LevelsAllow(50, 20), "50 may teach 20 (gap 30)");
        Check.True(!MentorPolicy.LevelsAllow(80, 49), "49 is too high to be an apprentice");
        Check.True(!MentorPolicy.LevelsAllow(49, 10), "a master must be level 50+");
        Check.True(!MentorPolicy.LevelsAllow(60, 31), "the gap must be at least 30");
        Check.Equal(6, MentorPolicy.MaximumApprentices, "at most 6 apprentices");
        Check.Equal(50, MentorPolicy.GraduationLevel, "apprentices graduate at level 50");
        Check.Equal(new MentorRewardValues(200_000, 50, 20_000, 2_000), MentorPolicy.ApprenticeReward, "apprentice reward per guide");
        Check.Equal(new MentorRewardValues(100_000, 20, 10_000, 2_000), MentorPolicy.MasterReward, "master reward per guide + 2,000 B-Gold (STAGE3, the admin)");
        Check.Equal(20, MentorPolicy.MasterLevelBindingGold, "master gets 20 B-Gold per apprentice level (STAGE3, the admin)");

        var invite = PacketBuilder.MentorInvitation("Master", "Pupil");
        Check.Equal(72, invite.Length, "10234 is 72 bytes");
        Check.Equal((ushort)72, BinaryPrimitives.ReadUInt16LittleEndian(invite), "10234 length word");
        Check.Equal((ushort)10234, BinaryPrimitives.ReadUInt16LittleEndian(invite.AsSpan(2)), "10234 type word");
        Check.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(invite.AsSpan(4)), "invitation is operation 0");
        Check.Equal("Master", PacketBuilder.ReadMentorName(invite.AsSpan(8, 32)), "master name at +8");
        Check.Equal("Pupil", PacketBuilder.ReadMentorName(invite.AsSpan(40, 32)), "apprentice name at +40");

        var list = PacketBuilder.MentorList(new("Master", 1, 80, true), [new("Pupil", 2, 20, false), new("Other", 3, 25, true)], 7);
        Check.Equal(0x164, list.Length, "10235 covers every byte the client reads (+0x163)");
        Check.Equal((ushort)10235, BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(2)), "10235 type word");
        Check.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(4)), "row count at +4");
        Check.Equal("Pupil", PacketBuilder.ReadMentorName(list.AsSpan(8, 32)), "row 0 name at +8");
        Check.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(8 + 0x24)), "row map at +0x24");
        Check.Equal((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(8 + 0x26)), "row level at +0x26");
        Check.Equal((byte)0, list[8 + 0x28], "row online flag at +0x28");
        Check.Equal("Other", PacketBuilder.ReadMentorName(list.AsSpan(8 + 44, 32)), "rows are 44 bytes");
        Check.Equal("Master", PacketBuilder.ReadMentorName(list.AsSpan(0x110, 32)), "master row at +0x110");
        Check.Equal((byte)1, list[0x110 + 0x28], "master online");
        Check.Equal("Master", PacketBuilder.ReadMentorName(list.AsSpan(0x13c, 32)), "master name text at +0x13c");
        Check.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(0x15c)), "Masters Ethics at +0x15c");
        var many = PacketBuilder.MentorList(new("M", 1, 80, true), Enumerable.Range(0, 9).Select(i => new PacketBuilder.MentorListRow("A" + i, 1, 1, false)).ToArray());
        Check.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(many.AsSpan(4)), "never more than 6 rows");

        var tip = PacketBuilder.MentorTip(15, 2000, "Pupil");
        Check.Equal((ushort)10236, BinaryPrimitives.ReadUInt16LittleEndian(tip.AsSpan(2)), "10236 type word");
        Check.Equal(15, BinaryPrimitives.ReadInt32LittleEndian(tip.AsSpan(4)), "tip code at +4");
        Check.Equal(2000, BinaryPrimitives.ReadInt32LittleEndian(tip.AsSpan(8)), "tip value at +8");
        Check.Equal("Pupil", PacketBuilder.ReadMentorName(tip.AsSpan(12, 32)), "tip name at +12");
        var longName = PacketBuilder.MentorInvitation(new string('x', 40), "y");
        Check.Equal((byte)0, longName[8 + 31], "an over-long name keeps its NUL terminator");
        return Task.CompletedTask;
    }
}
