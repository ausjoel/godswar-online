using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FactionCrierHandlerChecks
{
    private static void CheckNameplateAcquisitionProjection()
    {
        var dailyAfter = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            4,
            NameplateI.ToCompactString());
        var daily = GameClientHandler.GetFactionCrierNameplateAcquisitions(
            GameDefaults.EmptyKitBag,
            dailyAfter);
        Check.True(
            daily.Count == 1 &&
            daily[0].Id == NameplateI.Id &&
            daily[0].Stack == 1,
            "daily/weekly claim projects its positive Nameplate delta");
        Check.Equal(
            0,
            GameClientHandler.GetFactionCrierAcquisitionScratchSlot(
                GameDefaults.EmptyKitBag,
                dailyAfter),
            "new-slot claim predicts the first client-empty scratch slot");

        var stackedBefore = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            5,
            (NameplateI with { Stack = 2 }).ToCompactString());
        var stackedAfter = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            5,
            (NameplateI with { Stack = 3 }).ToCompactString());
        var stacked = GameClientHandler.GetFactionCrierNameplateAcquisitions(
            stackedBefore,
            stackedAfter);
        Check.True(
            stacked.Count == 1 && stacked[0].Stack == 1,
            "stacked claim logs only the newly granted quantity");
        Check.Equal(
            0,
            GameClientHandler.GetFactionCrierAcquisitionScratchSlot(
                stackedBefore,
                stackedAfter),
            "stacked claim predicts an earlier client-empty scratch slot");

        var fullBefore = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            fullBefore = KitBagSlots.SetSlot(
                fullBefore,
                slot,
                NameplateVI.ToCompactString());
        }
        fullBefore = KitBagSlots.SetSlot(
            fullBefore,
            5,
            (NameplateI with { Stack = 2 }).ToCompactString());
        var fullAfter = KitBagSlots.SetSlot(
            fullBefore,
            5,
            (NameplateI with { Stack = 3 }).ToCompactString());
        Check.Equal(
            5,
            GameClientHandler.GetFactionCrierAcquisitionScratchSlot(
                fullBefore,
                fullAfter),
            "full-bag stack claim reuses its pre-cleared changed slot");

        var renewal = GameClientHandler.GetFactionCrierNameplateAcquisitions(
            BagWithRenewalSource(),
            BagWithRenewalSource(target: true));
        Check.True(
            renewal.Count == 1 &&
            renewal[0].Id == NameplateVI.Id &&
            renewal[0].Stack == 1,
            "renewal logs only the replacement Nameplate");
        Check.Equal(
            0,
            GameClientHandler.GetFactionCrierAcquisitionScratchSlot(
                BagWithRenewalSource(),
                BagWithRenewalSource(target: true)),
            "renewal predicts the first client-empty scratch slot");
        Check.Equal(
            0,
            GameClientHandler.GetFactionCrierNameplateAcquisitions(
                dailyAfter,
                dailyAfter).Count,
            "an unchanged/replayed bag has no acquisition delta");
        Check.Equal(
            0,
            GameClientHandler.GetFactionCrierNameplateAcquisitions(
                BagWithAllNameplates(),
                GameDefaults.EmptyKitBag).Count,
            "turn-in removals do not create acquisition notices");

        var packet = PacketBuilder.SystemAddItemWithAcquisitionLog(
            NameplateVI with { Bound = 1, Stack = 3 });
        Check.Equal(80, packet.Length, "system-add item packet length");
        Check.Equal(0x27C9, ReadOpcode(packet),
            "system-add item packet opcode");
        Check.Equal(0u,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4)),
            "system-add item reserved DWORD");
        Check.Equal(NameplateVI.Id,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4)),
            "system-add item record ID");
        Check.Equal((byte)1, packet[34],
            "system-add item record binding");
        Check.Equal((byte)3, packet[35],
            "system-add item record quantity");
        Check.Equal(0x1448u,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(76, 4)),
            "system-add item record local owner");
    }

    private static void AssertNameplateAcquisition(
        IReadOnlyList<byte[]> packets,
        uint expectedItemId,
        byte expectedQuantity,
        string description)
    {
        var acquisitions = packets
            .Select((packet, index) => (Packet: packet, Index: index))
            .Where(entry => ReadOpcode(entry.Packet) == 0x27C9)
            .ToArray();
        Check.Equal(1, acquisitions.Length,
            $"{description} emits one item acquisition");

        var acquisition = acquisitions[0];
        Check.Equal(80, acquisition.Packet.Length,
            $"{description} acquisition length");
        Check.Equal(expectedItemId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                acquisition.Packet.AsSpan(8, 4)),
            $"{description} acquisition item");
        Check.Equal(expectedQuantity, acquisition.Packet[35],
            $"{description} acquisition quantity");

        var deleteIndexes = packets
            .Select((packet, index) => (Packet: packet, Index: index))
            .Where(entry => ReadOpcode(entry.Packet) == 0x2744)
            .ToArray();
        var bagIndex = FindOpcode(packets, 0x2731);
        var resultIndex = FindOpcode(
            packets,
            Opcodes.NpcFunctionActionResponse);
        Check.True(
            deleteIndexes.Length == 2 &&
            deleteIndexes[0].Index < acquisition.Index &&
            acquisition.Index < deleteIndexes[1].Index &&
            deleteIndexes[1].Index < bagIndex &&
            bagIndex < resultIndex,
            $"{description} logs between mutation eviction and transient cleanup");
        var cleanup = deleteIndexes[1].Packet;
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(
                cleanup.AsSpan(8, 2)) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                cleanup.AsSpan(10, 2)) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                cleanup.AsSpan(12, 2)) == ushort.MaxValue &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                cleanup.AsSpan(14, 2)) == ushort.MaxValue,
            $"{description} clears predicted transient slot zero");
    }
}
