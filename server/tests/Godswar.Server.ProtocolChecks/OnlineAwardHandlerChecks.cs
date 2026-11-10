using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OnlineAwardHandlerChecks
{
    public const string CheckName =
        "Online Award durable handler and acquisition projection";

    public static async Task RunAsync()
    {
        CheckGroupedAcquisitionProjection();
        await CheckCommittedClaimAsync();
        await CheckDuplicateClaimAsync();
    }

    private static void CheckGroupedAcquisitionProjection()
    {
        var before = BagBefore();
        var after = BagAfter();
        var acquisitions = GameClientHandler.GetOnlineAwardAcquisitions(
            before,
            after,
            Balance);
        var actual = acquisitions.Select(static item =>
            (item.Id, item.Quality, item.Bound, item.Stack)).ToArray();
        Check.True(actual.SequenceEqual(new[]
        {
            (10150u, (short)14, (short)0, (short)1),
            (10150u, (short)10, (short)0, (short)1),
            (10150u, (short)10, (short)0, (short)1),
            (10150u, (short)10, (short)0, (short)1),
            (10150u, (short)10, (short)0, (short)1),
            (10134u, (short)1, (short)0, (short)5),
            (11005u, (short)1, (short)0, (short)5)
        }), "grouped receipt expands to five eggs and two material notices");
        Check.Equal(
            0,
            GameClientHandler.GetOnlineAwardAcquisitionScratchSlot(
                before,
                after),
            "changed occupied slot precedes a later original empty slot");

        var fullBefore = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            fullBefore = KitBagSlots.SetSlot(
                fullBefore,
                slot,
                Feather.ToCompactString());
        }
        fullBefore = KitBagSlots.SetSlot(
            fullBefore,
            5,
            Dew.ToCompactString());
        var fullAfter = KitBagSlots.SetSlot(
            fullBefore,
            5,
            (Dew with { Stack = 6 }).ToCompactString());
        Check.Equal(
            5,
            GameClientHandler.GetOnlineAwardAcquisitionScratchSlot(
                fullBefore,
                fullAfter),
            "full-bag stack grant reuses its deletion-ACK-cleared slot");
        Check.Equal(
            -1,
            GameClientHandler.GetOnlineAwardAcquisitionScratchSlot(
                fullBefore,
                fullBefore),
            "unchanged full bag has no unsafe transient scratch slot");
    }

    private static async Task CheckCommittedClaimAsync()
    {
        var before = CreateSnapshot(BagBefore(), after: false);
        var after = CreateSnapshot(BagAfter(), after: true);
        var receipt = CreateReceipt();
        await using var fixture = await CreateFixtureAsync(
            before,
            after,
            OnlineAwardExecutionResult.Terminal(
                OnlineAwardExecutionDisposition.Committed,
                receipt));
        fixture.Character.MaxHp = 77_700;
        fixture.Character.MaxMp = 7_770;
        fixture.Character.CurrentHp = 70_007;
        fixture.Character.CurrentMp = 7_007;

        await InvokeAsync(fixture.Handler);

        Check.True(
            fixture.Executor.ExecuteCount == 1 &&
            fixture.Executor.Envelope?.Command is { } command &&
            command.NpcId == OnlineAwardProtocol.AthensNpcId &&
            command.DialogIndex == OnlineAwardProtocol.DialogIndex &&
            command.Identity.IsSecureClient &&
            command.Identity.OperationId == OperationId,
            "top-level sub-minus-one dispatches directly to durable family 57");
        Check.True(
            fixture.Snapshots.ReadCount == 1 &&
            fixture.Character.KitBag == BagAfter() &&
            fixture.Character.OnlineAwardRevision == AfterAwardRevision,
            "committed claim reloads authoritative bag and Online Award revision");
        Check.True(
            fixture.Character.Level == 80 &&
            fixture.Character.Experience == 7_777 &&
            fixture.Character.TalentPoints == 77 &&
            fixture.Character.Silver == 777 &&
            fixture.Character.Gold == 77 &&
            fixture.Character.BindingGold == 7 &&
            fixture.Character.MaxHp == 77_700 &&
            fixture.Character.MaxMp == 7_770 &&
            fixture.Character.CurrentHp == 70_007 &&
            fixture.Character.CurrentMp == 7_007,
            "bag projection cannot overwrite progression, wallet, or vitals");

        var packets = fixture.ReadPackets();
        AssertCommittedPacketOrder(packets);
        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.CommandFamily == (ushort)CommandFamily.OnlineAward &&
            secure.ResultCode == OnlineAwardProtocol.SuccessSubId &&
            secure.Disposition == SecureLegacyCommandDisposition.Applied &&
            secure.AuthoritativeRevision == AfterAwardRevision &&
            secure.OperationId == OperationId &&
            fixture.Transport.Events[^1] == "secure",
            "secure family 57 result follows the complete native projection");
    }

    private static async Task CheckDuplicateClaimAsync()
    {
        var snapshot = CreateSnapshot(BagAfter(), after: true);
        var historical = CreateReceipt(
            balanceRevision: 99,
            balanceSha256: new string('A', 64),
            itemRevision: new string('B', 64));
        await using var fixture = await CreateFixtureAsync(
            snapshot,
            snapshot,
            OnlineAwardExecutionResult.Terminal(
                OnlineAwardExecutionDisposition.Duplicate,
                historical));

        await InvokeAsync(fixture.Handler);

        var packets = fixture.ReadPackets();
        Check.True(
            fixture.Executor.ExecuteCount == 1 &&
            fixture.Snapshots.ReadCount == 1 &&
            !packets.Any(packet => ReadOpcode(packet) == 0x27C9) &&
            !packets.Any(packet => ReadOpcode(packet) == 0x2744),
            "historical duplicate reloads but emits no acquisition or cleanup");
        var resultIndex = FindOpcode(
            packets,
            Opcodes.NpcFunctionActionResponse);
        Check.True(
            FindOpcode(packets, 0x2731) >= 0 &&
            resultIndex == packets.Count - 1,
            "duplicate refreshes bag before its native success result");
        AssertNativeResult(packets[resultIndex]);
        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.Disposition == SecureLegacyCommandDisposition.Replayed &&
            secure.CommandFamily == (ushort)CommandFamily.OnlineAward &&
            secure.AuthoritativeRevision == AfterAwardRevision,
            "historical duplicate settles as replayed with durable revision");
    }

    private static void AssertCommittedPacketOrder(
        IReadOnlyList<byte[]> packets)
    {
        var acquisitions = packets
            .Select((packet, index) => (Packet: packet, Index: index))
            .Where(static entry => ReadOpcode(entry.Packet) == 0x27C9)
            .ToArray();
        Check.Equal(7, acquisitions.Length,
            "committed claim emits seven acquisition records");
        var projected = acquisitions.Select(static entry => (
            Id: BinaryPrimitives.ReadUInt32LittleEndian(
                entry.Packet.AsSpan(8, 4)),
            Quality: entry.Packet[32],
            Quantity: entry.Packet[35])).ToArray();
        Check.True(projected.SequenceEqual(new[]
        {
            (10150u, (byte)14, (byte)1),
            (10150u, (byte)10, (byte)1),
            (10150u, (byte)10, (byte)1),
            (10150u, (byte)10, (byte)1),
            (10150u, (byte)10, (byte)1),
            (10134u, (byte)1, (byte)5),
            (11005u, (byte)1, (byte)5)
        }), "left log preserves Godly, Smart, Dew, and Feather quantities");

        Check.True(ReadOpcode(packets[0]) == 0x2744,
            "changed occupied slots are evicted before acquisition logs");
        foreach (var acquisition in acquisitions)
        {
            Check.True(
                acquisition.Index + 1 < packets.Count &&
                ReadOpcode(packets[acquisition.Index + 1]) == 0x2744 &&
                BinaryPrimitives.ReadUInt16LittleEndian(
                    packets[acquisition.Index + 1].AsSpan(8, 2)) == 0 &&
                BinaryPrimitives.ReadUInt16LittleEndian(
                    packets[acquisition.Index + 1].AsSpan(10, 2)) == 0,
                "each transient acquisition is immediately cleared from scratch slot zero");
        }
        var bagIndex = FindOpcode(packets, 0x2731);
        var resultIndex = FindOpcode(
            packets,
            Opcodes.NpcFunctionActionResponse);
        Check.True(
            acquisitions[^1].Index + 1 < bagIndex &&
            bagIndex < resultIndex && resultIndex == packets.Count - 1,
            "cleanup precedes authoritative bag refresh and native result");
        AssertNativeResult(packets[resultIndex]);
    }

    private static void AssertNativeResult(byte[] packet)
    {
        Check.True(
            packet.Length == 16 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4)) ==
                OnlineAwardProtocol.AthensNpcId &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8, 4)) ==
                OnlineAwardProtocol.DialogIndex &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12, 4)) ==
                OnlineAwardProtocol.SuccessSubId,
            "native Online Award result is exact success row 102");
    }

    private static int FindOpcode(
        IReadOnlyList<byte[]> packets,
        ushort opcode)
    {
        for (var index = 0; index < packets.Count; index++)
        {
            if (ReadOpcode(packets[index]) == opcode)
            {
                return index;
            }
        }
        return -1;
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
}
