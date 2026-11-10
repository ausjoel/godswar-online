using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Talents;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static bool TryReadNpcFunctionAction(
        ReadOnlySpan<byte> payload,
        out uint npcId,
        out int dialogIndex,
        out int subId,
        out int[] args)
    {
        npcId = 0;
        dialogIndex = 0;
        subId = 0;
        args = [];

        if (payload.Length < 16)
        {
            return false;
        }

        npcId = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        dialogIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        subId = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4));

        var count = Math.Max(0, (payload.Length - 16) / 4);
        args = new int[count];
        for (var i = 0; i < count; i++)
        {
            args[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16 + (i * 4), 4));
        }

        return true;
    }

    private static void LogReceived(GamePacket packet)
    {
        Console.WriteLine(
            $"[game] recv {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} hex={packet.ToHexPreview(32)}");
    }

    private static void LogInventoryPacket(GamePacket packet)
    {
        var payload = packet.Payload;
        Console.WriteLine(
            $"[equip-re] {Opcodes.Name(packet.Opcode)} payloadLen={payload.Length} bytes={FormatBytes(payload)} u16={FormatUInt16(payload)} u32={FormatUInt32(payload)}");
    }

    internal static int ResolveEquippedSlotForAck(
        GameCharacter character,
        string previousEquipment,
        int requestedEquipmentSlot,
        uint itemIdHint)
    {
        if (EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot)
            && EquipmentSlots.GetItemId(character.Equipment, character.Profession, requestedEquipmentSlot) == itemIdHint)
        {
            return requestedEquipmentSlot;
        }

        if (itemIdHint == 0)
        {
            return -1;
        }

        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Mount; slot++)
        {
            if (EquipmentSlots.GetItemId(character.Equipment, character.Profession, slot) == itemIdHint
                && !string.Equals(
                    EquipmentSlots.GetEntry(previousEquipment, character.Profession, slot),
                    EquipmentSlots.GetEntry(character.Equipment, character.Profession, slot),
                    StringComparison.Ordinal))
            {
                return slot;
            }
        }

        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Mount; slot++)
        {
            if (EquipmentSlots.GetItemId(character.Equipment, character.Profession, slot) == itemIdHint)
            {
                return slot;
            }
        }

        return -1;
    }

    private static string FormatBytes(ReadOnlySpan<byte> payload)
    {
        return payload.Length == 0 ? "[]" : "[" + string.Join(",", payload.ToArray().Select(b => b.ToString())) + "]";
    }

    private static string FormatUInt16(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return "[]";
        }

        var values = new List<ushort>();
        for (var i = 0; i + 1 < payload.Length; i += 2)
        {
            values.Add(BinaryPrimitives.ReadUInt16LittleEndian(payload[i..(i + 2)]));
        }

        return "[" + string.Join(",", values) + "]";
    }

    private static string FormatUInt32(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return "[]";
        }

        var values = new List<uint>();
        for (var i = 0; i + 3 < payload.Length; i += 4)
        {
            values.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload[i..(i + 4)]));
        }

        return "[" + string.Join(",", values) + "]";
    }

    internal static bool TryReadStorageItemEquipmentBagTransfer(
        ReadOnlySpan<byte> payload,
        out int equipmentSlot,
        out int bagSlot)
    {
        equipmentSlot = 0;
        bagSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        equipmentSlot = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var emptyMarker = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var destinationPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var destinationIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot)
            || emptyMarker != ushort.MaxValue
            || destinationPage >= 4
            || destinationIndex >= 24)
        {
            return false;
        }

        bagSlot = (destinationPage * 24) + destinationIndex;
        return true;
    }

    internal static bool TryReadStorageItemKitBagMove(
        ReadOnlySpan<byte> payload,
        out int sourceSlot,
        out int destinationSlot)
    {
        sourceSlot = 0;
        destinationSlot = 0;

        if (payload.Length < 16)
        {
            return false;
        }

        var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var destinationPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var destinationIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        var marker1 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(12, 2));
        var marker2 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14, 2));

        const int fullStorageItemRequestPayloadLength = 76;
        var hasStrictEmptyMarkers = marker1 == ushort.MaxValue && marker2 == ushort.MaxValue;
        var isFullStorageItemRequest = payload.Length == fullStorageItemRequestPayloadLength;
        if (!hasStrictEmptyMarkers && !isFullStorageItemRequest)
        {
            return false;
        }

        if (sourcePage >= 4 || destinationPage >= 4 || sourceIndex >= 24 || destinationIndex >= 24)
        {
            return false;
        }

        sourceSlot = (sourcePage * 24) + sourceIndex;
        destinationSlot = (destinationPage * 24) + destinationIndex;
        return true;
    }

    internal static bool TryReadStorageItemDelete(ReadOnlySpan<byte> payload, out int sourceSlot)
    {
        sourceSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var destinationPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var destinationIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));

        if (sourcePage >= 4
            || sourceIndex >= 24
            || destinationPage != ushort.MaxValue
            || destinationIndex != ushort.MaxValue)
        {
            return false;
        }

        sourceSlot = (sourcePage * 24) + sourceIndex;
        return true;
    }

    internal static bool TryReadBreakItemEquip(
        ReadOnlySpan<byte> payload,
        out int sourceSlot)
    {
        sourceSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        // The dword at offset 4 is the currently selected world object. It may
        // be the player, a monster, or an NPC, so it cannot be used to decide
        // whether this is an equip request. Captured clients consistently put
        // the authoritative bag page/index at offsets 8 and 10.
        var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        if (sourcePage >= 4 || sourceIndex >= 24)
        {
            return false;
        }

        sourceSlot = (sourcePage * 24) + sourceIndex;
        return true;
    }

    /// <summary>
    /// Reads the client's sell request (opcode 10060). The four-byte payload is
    /// the bag page followed by the index inside that page, observed live:
    /// selling the medium healing potion in page 1 index 19 arrived as
    /// <c>01 00 13 00</c> and the authoritative slot was (1 * 24) + 19 = 43.
    /// The packet carries no quantity, so a sale always covers the whole stack.
    /// </summary>
    internal static bool TryReadSellItemRequest(
        ReadOnlySpan<byte> payload,
        out int sourceSlot)
    {
        sourceSlot = 0;
        if (payload.Length != SellRequestPayloadBytes)
        {
            return false;
        }

        var page = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        var index = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        if (page >= KitBagPageCount || index >= KitBagSlotsPerPage)
        {
            return false;
        }

        sourceSlot = (page * KitBagSlotsPerPage) + index;
        return true;
    }

    internal const int SellRequestPayloadBytes = 4;
    internal const int KitBagSlotsPerPage = 24;
    internal const int KitBagPageCount = 4;

    /// <summary>
    /// Payload bytes of the 40-byte 10056 descriptor: the ground pickup, the
    /// quest reward item the client pays itself, and a bag-to-equipment move all
    /// arrive in this one shape.
    /// </summary>
    internal const int GroundLootPickupPayloadBytes = 36;

    private static bool TryReadBagItemAction(
        ReadOnlySpan<byte> payload,
        out int sourceSlot,
        out uint itemId)
    {
        sourceSlot = 0;
        itemId = 0;

        if (payload.Length < 20)
        {
            return false;
        }

        sourceSlot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4));
        itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
        return sourceSlot is >= 0 and < 96 && itemId != 0;
    }

    /// <summary>
    /// Parses the ground-loot pickup descriptor the installed client sends on
    /// 10056. Captured 2026-09-15 (captures/monster-drop-20260915.txt): a
    /// 40-byte frame whose payload names the destination bag page at +8 and
    /// slot index at +12, the item at +16, and the ground identity at +28/+32.
    /// The reference server answered with the same descriptor after replacing
    /// both ground-identity words with the item it matched.
    /// </summary>
    internal static bool TryReadGroundLootPickup(
        ReadOnlySpan<byte> payload,
        out int destinationSlot,
        out uint itemId,
        out uint groundKeyHigh,
        out uint groundKeyLow)
    {
        destinationSlot = 0;
        itemId = 0;
        groundKeyHigh = 0;
        groundKeyLow = 0;

        if (payload.Length != GroundLootPickupPayloadBytes)
        {
            return false;
        }

        var page = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        var index = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
        itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
        groundKeyHigh = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(28, 4));
        groundKeyLow = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(32, 4));
        if (itemId == 0 ||
            index >= KitBagSlotsPerPage ||
            page >= KitBagPageCount)
        {
            return false;
        }

        destinationSlot = checked((int)(page * KitBagSlotsPerPage + index));
        return true;
    }

    internal static bool TryReadTalentUpgrade(
        ReadOnlySpan<byte> payload,
        out int talentId,
        out int clientRank,
        out int clientTalentPoints)
    {
        talentId = 0;
        clientRank = 0;
        clientTalentPoints = 0;

        if (payload.Length !=
            LegacyTalentUpgradeCommandAdapter.PayloadLength)
        {
            return false;
        }

        talentId = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        clientRank = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8, 4));
        clientTalentPoints = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16, 4));
        return talentId is >= TalentUpgradeCommandEnvelope.MinimumTalentId
                and <= TalentUpgradeCommandEnvelope.MaximumTalentId &&
            clientRank is >= TalentUpgradeCommandEnvelope.MinimumExpectedRank
                and <= TalentUpgradeCommandEnvelope.MaximumExpectedRank &&
            clientTalentPoints >= 0;
    }

    private static bool TryReadItemInfoRequest(
        ReadOnlySpan<byte> payload,
        out int sourceSlot,
        out uint itemId)
    {
        sourceSlot = 0;
        itemId = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        sourceSlot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        return sourceSlot is >= 0 and < 96 && itemId != 0;
    }

    internal static bool MatchesCurrentKitBagItem(GameCharacter? character, int sourceSlot, uint itemId)
    {
        return character is not null
            && sourceSlot is >= 0 and < 96
            && itemId != 0
            && KitBagSlots.GetItemId(character.KitBag, sourceSlot) == itemId;
    }

    internal static byte ReadZodiacTypeFromCreationPayload(ReadOnlySpan<byte> payload)
    {
        var zodiacType = ReadByte(payload, 35, 0);
        return zodiacType <= 11 ? zodiacType : (byte)0;
    }

    private static byte ReadByte(ReadOnlySpan<byte> buffer, int offset, byte fallback)
    {
        return offset >= 0 && offset < buffer.Length ? buffer[offset] : fallback;
    }

    private sealed record PendingUnequipFollowup(int DestinationSlot, uint ItemId, DateTime CreatedUtc);
}
