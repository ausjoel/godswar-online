using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int DesignationHeaderLength = 12;
    private const int DesignationRecordLength = 12;
    private const byte MedusaDesignationType = 4;
    private const byte PermanentDesignationState = 1;

    /// <summary>
    /// Projects durable Medusa-title ownership into the stock client's Title
    /// dialog. Opcode 10196 is the designation list, not a learned-skill list.
    /// </summary>
    public static byte[] MedusaDesignationInfo(
        uint selectedTitleId,
        IReadOnlyCollection<uint> ownedTitleIds)
    {
        ArgumentNullException.ThrowIfNull(ownedTitleIds);
        if (ownedTitleIds.Count > CharacterSnapshotLimits.OwnedTitleCount ||
            ownedTitleIds.Any(static titleId => titleId == 0) ||
            ownedTitleIds.Distinct().Count() != ownedTitleIds.Count)
        {
            throw new ArgumentException(
                "Owned Medusa title IDs must be nonzero, unique, and bounded.",
                nameof(ownedTitleIds));
        }

        var titleIds = ownedTitleIds.Order().ToArray();
        var packetLength = checked(
            DesignationHeaderLength +
            (titleIds.Length * DesignationRecordLength));
        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packetLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.DesignationInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            selectedTitleId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            titleIds.Length);

        for (var index = 0; index < titleIds.Length; index++)
        {
            var offset = DesignationHeaderLength +
                (index * DesignationRecordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(offset),
                titleIds[index]);
            packet[offset + 4] = DesignationTypeFor(titleIds[index]); // B22: was MedusaDesignationType for all
            packet[offset + 5] = PermanentDesignationState;
            // +6..+7 are native padding; +8 is the remaining-duration field.
            // Zero is the captured permanent-title representation.
        }

        return packet;
    }

    /// <summary>
    /// B22: the Title window tab of a title (client Message.dat DesigType0-4: Leveling, Quest, Attribute, Ranking,
    /// Event), from the DesigName.dat id ranges. Before B22 every title was sent as Event (4), so the Leveling,
    /// Quest and Attribute tabs were always empty. Marriage (1401/1402) and everything else stay Event.
    /// </summary>
    internal static byte DesignationTypeFor(uint titleId) => titleId switch
    {
        >= 1000 and <= 1399 => 0,
        >= 2000 and <= 2999 => 1,
        >= 3000 and <= 3999 => 2,
        _ => MedusaDesignationType
    };
}
