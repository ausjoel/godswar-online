using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class SkillBookBroadcastChecks
{
    public const string CheckName = "Skill book realm broadcast";

    // Byte-for-byte golden frames from the reference capture of 2026-09-24:
    // eighteen type-0 10038 broadcasts, every one of them Athens
    // ("1003#1000#<book>"). Two draws by Skrillex are pinned here so the field
    // layout is fixed by the capture instead of by our own reading of it.
    private const string GoldenAthensBook5205 =
        "890036270000000000536B72696C6C65780000000000000000000000" +   // bytes 0-27
        "00000000000000000000000000000000000000000000000000000000" +   // bytes 28-55
        "00000000000000000000000000000000003130303323313030302335" +   // bytes 56-83
        "32303500000000000000000000000000000000000000000000000000" +   // bytes 84-111
        "00000000000000000000000000000000000000000000000000";        // bytes 112-136

    private const string GoldenAthensBook5242 =
        "890036270000000000536B72696C6C65780000000000000000000000" +   // bytes 0-27
        "00000000000000000000000000000000000000000000000000000000" +   // bytes 28-55
        "00000000000000000000000000000000003130303323313030302335" +   // bytes 56-83
        "32343200000000000000000000000000000000000000000000000000" +   // bytes 84-111
        "00000000000000000000000000000000000000000000000000";        // bytes 112-136

    // The Sparta pair is the client's own other half of the same sentence:
    // skill_msg[1004] is "斯巴达玩家-" and skill_msg[1001] is
    // "-受到神的青睐,获得: ", so only those two words differ from the capture.
    private const byte SpartaCamp = GameDefaults.SpartaCamp;
    private const byte AthensCamp = GameDefaults.AthensCamp;

    public static Task RunAsync()
    {
        Check.Equal(
            10038,
            (int)Opcodes.PythonNote,
            "realm broadcast opcode");

        var athens = PacketBuilder.SkillBookBroadcast(
            "Skrillex",
            AthensCamp,
            5205);
        Check.Equal(137, athens.Length, "broadcast length");
        Check.Equal(
            GoldenAthensBook5205,
            Convert.ToHexString(athens),
            "captured Athens 5205 broadcast");

        Check.Equal(
            GoldenAthensBook5242,
            Convert.ToHexString(
                PacketBuilder.SkillBookBroadcast("Skrillex", AthensCamp, 5242)),
            "captured Athens 5242 broadcast");

        Check.Equal(
            (ushort)137,
            BinaryPrimitives.ReadUInt16LittleEndian(athens),
            "broadcast declared length");
        Check.Equal(
            Opcodes.PythonNote,
            BinaryPrimitives.ReadUInt16LittleEndian(athens.AsSpan(2)),
            "broadcast opcode");
        Check.Equal(
            0,
            BinaryPrimitives.ReadInt32LittleEndian(athens.AsSpan(4)),
            "skill blessing broadcast type");
        Check.Equal((byte)0, athens[8], "skill blessing channel");
        Check.Equal(
            "Skrillex",
            ReadFixedAscii(athens.AsSpan(9, 64)),
            "broadcast name field");
        Check.Equal(
            "1003#1000#5205",
            ReadFixedAscii(athens.AsSpan(73, 64)),
            "captured Athens parameter field");

        var sparta = PacketBuilder.SkillBookBroadcast(
            "Skrillex",
            SpartaCamp,
            5205);
        Check.Equal(
            "1004#1001#5205",
            ReadFixedAscii(sparta.AsSpan(73, 64)),
            "Sparta parameter field");
        Check.Equal((byte)0, SpartaCamp, "Sparta camp value");
        Check.Equal((byte)1, AthensCamp, "Athens camp value");
        Check.Equal(
            137,
            sparta.Length,
            "Sparta broadcast length");
        Check.Equal(
            ReadFixedAscii(athens.AsSpan(9, 64)),
            ReadFixedAscii(sparta.AsSpan(9, 64)),
            "both camps carry the same name field");
        Check.True(
            athens.AsSpan(0, 73).SequenceEqual(sparta.AsSpan(0, 73)),
            "the two camps differ only inside the parameter field");
        Check.Equal(
            "1003",
            ReadFixedAscii(athens.AsSpan(73, 4)),
            "Athens realm word");
        Check.Equal(
            "1004",
            ReadFixedAscii(sparta.AsSpan(73, 4)),
            "Sparta realm word");
        Check.Equal(
            "1000",
            ReadFixedAscii(athens.AsSpan(78, 4)),
            "Athens blessing word");
        Check.Equal(
            "1001",
            ReadFixedAscii(sparta.AsSpan(78, 4)),
            "Sparta blessing word");

        // The client renders skill_msg[realm] .. name .. skill_msg[blessing] ..
        // skill_msg[item id], so between two draws of one camp only the
        // parameter's last word may differ.
        var second = PacketBuilder.SkillBookBroadcast("Skrillex", AthensCamp, 5242);
        Check.True(
            athens.AsSpan(2, 7).SequenceEqual(second.AsSpan(2, 7)),
            "opcode, type and channel are identical across draws");
        Check.True(
            ReadFixedAscii(second.AsSpan(73, 64))
                .StartsWith("1003#1000#", StringComparison.Ordinal),
            "parameter keeps the captured fixed words");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.SkillBookBroadcast("Skrillex", 2, 5205),
            "an unrecognized camp is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.SkillBookBroadcast("Skrillex", AthensCamp, 0),
            "a zero skill book id is rejected");
        Check.Throws<ArgumentException>(
            () => PacketBuilder.SkillBookBroadcast(string.Empty, AthensCamp, 5205),
            "an empty name is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.SkillBookBroadcast(new string('a', 64), AthensCamp, 5205),
            "a name longer than the field is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.SkillBookBroadcast("Skrillex\u4E2D", AthensCamp, 5205),
            "a non-ASCII name is rejected");
        return Task.CompletedTask;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }
}
