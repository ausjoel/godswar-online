using System.Buffers.Binary;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int SkillBroadcastBlessingType = 0;
    private const int SkillBroadcastNameOffset = 9;
    private const int SkillBroadcastNoteOffset =
        SkillBroadcastNameOffset + PythonNoteFieldLength;

    // The client's skill_msg keys the parameter is built from: the first two
    // words pick the sentence, the book's item id fills its last word.
    private const string SkillBroadcastAthensRealmWord = "1003";
    private const string SkillBroadcastSpartaRealmWord = "1004";
    private const string SkillBroadcastAthensBlessingWord = "1000";
    private const string SkillBroadcastSpartaBlessingWord = "1001";

    /// <summary>
    /// The realm-wide line the reference server sends when a player of
    /// <paramref name="camp"/> is blessed with a skill book.
    /// </summary>
    /// <remarks>
    /// Captured 2026-09-24 as
    /// <c>890036270000000000536B72696C6C6578…3130303323313030302335323035…</c>
    /// for the Athens player <c>Skrillex</c> and book 5205 - the same 137-byte
    /// 10038 frame the centred proclamations use, but with type 0 and channel 0,
    /// and with a parameter string rather than text: the player name and
    /// <c>1003#1000#&lt;item id&gt;</c>, one 64-byte field each. All eighteen
    /// captured type-0 broadcasts carried that Athens pair.
    /// <para>
    /// The client's own <c>SrvMsg.lua</c> type-0 branch renders
    /// <c>skill_msg[realm] .. name .. skill_msg[blessing] .. skill_msg[item id]</c>,
    /// and its tables spell the two camps out separately: 1003/1000 read as
    /// "雅典玩家-" + "-受到神的祝福,获得: " and 1004/1001 as "斯巴达玩家-" +
    /// "-受到神的青睐,获得: ", so each camp gets its own sentence and the whole
    /// line is the client's, not this server's.
    /// </para>
    /// <para>
    /// Only the book's item id varies per draw: it is what picks the skill name
    /// out of <c>skill_msg</c>, where 5205 is <c>SkillBook283</c> in
    /// <c>ItemBaseAttribute.xml</c> and renders as "高级圣光突刺".
    /// </para>
    /// </remarks>
    public static byte[] SkillBookBroadcast(
        string playerName,
        byte camp,
        int skillBookItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(skillBookItemId);
        if (camp is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp))
        {
            throw new ArgumentOutOfRangeException(
                nameof(camp),
                "Native camp broadcasts require a recognized camp.");
        }

        if (playerName.Length > PythonNoteTextPartLength ||
            playerName.Any(static character => character is < ' ' or > '~'))
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerName),
                "The native broadcast carries a printable ASCII name of at most 63 bytes.");
        }

        var athens = camp == GameDefaults.AthensCamp;
        var note =
            $"{(athens ? SkillBroadcastAthensRealmWord : SkillBroadcastSpartaRealmWord)}" +
            $"#{(athens ? SkillBroadcastAthensBlessingWord : SkillBroadcastSpartaBlessingWord)}" +
            $"#{skillBookItemId}";
        if (note.Length > PythonNoteTextPartLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(skillBookItemId),
                "The native broadcast parameter must fit one 64-byte field.");
        }

        var packet = new byte[PythonNotePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)PythonNotePacketLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PythonNote);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            SkillBroadcastBlessingType);
        packet[8] = PythonNoteCenterChannel;
        PacketText.WriteFixedAscii(
            packet.AsSpan(SkillBroadcastNameOffset, PythonNoteFieldLength),
            playerName);
        PacketText.WriteFixedAscii(
            packet.AsSpan(SkillBroadcastNoteOffset, PythonNoteFieldLength),
            note);
        return packet;
    }
}
