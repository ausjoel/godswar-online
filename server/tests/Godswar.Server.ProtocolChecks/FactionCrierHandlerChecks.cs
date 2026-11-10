using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FactionCrierHandlerChecks
{
    public const string CheckName =
        "Faction Crier durable handler and stock projection";

    public static async Task RunAsync()
    {
        CheckNameplateAcquisitionProjection();
        await CheckNavigationDoesNotMutateAsync();
        await CheckSecureRenewalReplaysBeforeReadingConsumedSlotAsync();
        await CheckRawRenewalAppliesWithLocalCapabilityAsync();
        await CheckCommittedTurnInProjectsAndOrdersResultsAsync();
        await CheckTalentPointNoticeCommitAndReplayAsync();
        CheckSundayAvailabilityGate();
        await CheckBelowLevelFailsWithStockResultAsync();
        await CheckCurrencyFailureSettlesAsync();
        await CheckMissingPlateFailsBeforeExecuteAsync();
    }

    private static async Task
        CheckSecureRenewalReplaysBeforeReadingConsumedSlotAsync()
    {
        var before = CreateSnapshot(
            level: 80,
            GameDefaults.EmptyKitBag,
            after: false);
        var after = CreateSnapshot(
            level: 80,
            BagWithRenewalSource(target: true),
            after: true);
        var receipt = CreateReceipt(
            before,
            after,
            FactionCrierOperation.RenewNameplate,
            subId: 46,
            nativeResultSubId: 846);
        var executor = new FactionCrierExecutor
        {
            ReplayResult = FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.Duplicate,
                receipt)
        };
        await using var fixture = await CreateFixtureAsync(
            secure: true,
            before,
            after,
            executor);

        await InvokeAsync(
            fixture.Handler,
            CreateRenewalPacket(OperationId));

        Check.Equal(1, executor.ReplayCount,
            "secure renewal checks its durable inbox first");
        Check.Equal(0, executor.ExecuteCount,
            "replayed renewal does not reconstruct or execute consumed input");
        Check.True(
            executor.ReplayIntent == new FactionCrierReplayIntent(1, 46),
            "renewal replay identity uses realm and nested action sub-ID");
        Check.True(
            executor.ReplayIdentity is { } identity &&
            identity.IsSecureClient && identity.OperationId == OperationId,
            "renewal replay uses the supplied secure operation UUID");
        Check.Equal(1, fixture.Snapshots.ReadCount,
            "replayed renewal reloads authoritative state");
        Check.Equal(
            NameplateVI.Id,
            KitBagSlots.GetItemId(fixture.Character.KitBag, RenewalSlot),
            "replayed renewal projects the already-converted plate");

        var packets = fixture.ReadLegacyPackets();
        Check.True(!packets.Any(packet => ReadOpcode(packet) == 0x272F),
            "replayed renewal does not replay experience animation");
        Check.True(!packets.Any(packet => ReadOpcode(packet) == 0x27C9),
            "replayed renewal does not replay item acquisition");
        AssertBagBeforeNativeResult(packets, 846, "replayed renewal");
        var secure = fixture.SecureTransport!.CommandResults.Single();
        Check.True(
            secure.Disposition == SecureLegacyCommandDisposition.Replayed,
            "duplicate renewal settles as replayed");
        Check.Equal(
            checked((ulong)AfterFactionRevision),
            secure.InventoryRevision,
            "secure renewal carries Faction Crier revision");
        Check.Equal(OperationId, secure.OperationId,
            "secure renewal settles the supplied UUID");
        Check.Equal("secure", fixture.SecureTransport.Events[^1],
            "secure settlement follows every native projection packet");
    }

    private static async Task
        CheckRawRenewalAppliesWithLocalCapabilityAsync()
    {
        var before = CreateSnapshot(
            level: 80,
            BagWithRenewalSource(),
            after: false);
        var after = CreateSnapshot(
            level: 80,
            BagWithRenewalSource(target: true),
            after: true);
        var receipt = CreateReceipt(
            before,
            after,
            FactionCrierOperation.RenewNameplate,
            subId: 46,
            nativeResultSubId: 846);
        var executor = new FactionCrierExecutor
        {
            ExecuteResult = FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.Committed,
                receipt)
        };
        await using var fixture = await CreateFixtureAsync(
            secure: false,
            before,
            after,
            executor);

        await InvokeAsync(
            fixture.Handler,
            CreateRenewalPacket(operationId: null));

        Check.Equal(0, executor.ReplayCount,
            "raw local renewal does not query secure replay identity");
        Check.Equal(1, executor.ExecuteCount,
            "authorized raw local renewal executes once");
        var command = executor.Envelope?.Command ??
            throw new InvalidOperationException(
                "Raw renewal command was not captured.");
        Check.True(command.Identity.IsRawLocalServer,
            "raw renewal receives a server operation identity");
        Check.True(
            command.RenewalSource is { } source &&
            source.KitBagSlot == RenewalSlot &&
            source.ItemId == NameplateI.Id &&
            source.ExpectedCompactItemState == NameplateI.ToCompactString(),
            "raw renewal captures exact authoritative slot and item state");
        Check.Equal(
            NameplateVI.Id,
            KitBagSlots.GetItemId(fixture.Character.KitBag, RenewalSlot),
            "raw renewal projects the converted nameplate");
        Check.Equal(395, fixture.Character.Gold,
            "raw renewal projects the authoritative Gold wallet");
        var packets = fixture.ReadLegacyPackets();
        AssertNameplateAcquisition(
            packets,
            NameplateVI.Id,
            expectedQuantity: 1,
            "raw renewal");
        AssertBagBeforeNativeResult(packets, 846, "raw renewal");
    }

    private static async Task
        CheckCommittedTurnInProjectsAndOrdersResultsAsync()
    {
        var before = CreateSnapshot(
            level: 80,
            BagWithAllNameplates(),
            after: false);
        var after = CreateSnapshot(
            level: 80,
            GameDefaults.EmptyKitBag,
            after: true);
        var receipt = CreateReceipt(
            before,
            after,
            FactionCrierOperation.TurnIn,
            subId: 131,
            nativeResultSubId: 3009,
            awardedExperience: 1_000,
            awardedTalentPoints: 21);
        var executor = new FactionCrierExecutor
        {
            ExecuteResult = FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.Committed,
                receipt)
        };
        await using var fixture = await CreateFixtureAsync(
            secure: true,
            before,
            after,
            executor);
        fixture.Character.MaxHp = 42_000;
        fixture.Character.MaxMp = 9_000;
        fixture.Character.CurrentHp = 18_210;
        fixture.Character.CurrentMp = 843;

        await InvokeAsync(
            fixture.Handler,
            CreateAllBoundGoldPacket(OperationId));

        Check.Equal(1, executor.ReplayCount,
            "secure turn-in checks replay before apply");
        Check.Equal(1, executor.ExecuteCount,
            "replay miss applies one turn-in");
        var command = executor.Envelope?.Command ??
            throw new InvalidOperationException(
                "Faction Crier turn-in command was not captured.");
        Check.True(
            command.Operation == FactionCrierOperation.TurnIn &&
            command.SubId == 131 &&
            command.RenewalSource is null,
            "nested B-Gold offer becomes the exact durable action intent");
        Check.True(
            fixture.Character.Experience == 2_000 &&
            fixture.Character.TalentPoints == 30 &&
            fixture.Character.BindingGold == 688 &&
            fixture.Character.FactionCrierRevision == AfterFactionRevision,
            "turn-in projects EXP, talent, B-Gold, and feature revision");
        Check.True(
            Enumerable.Range(0, 6).All(slot =>
                KitBagSlots.GetItemId(fixture.Character.KitBag, slot) == 0),
            "turn-in projects all consumed nameplates");

        var packets = fixture.ReadLegacyPackets();
        var experienceIndex = FindOpcode(packets, 0x272F);
        var talentIndex = FindOpcode(packets, 0x2845);
        var statusIndex = FindOpcode(packets, 0x27B6);
        var bagIndex = FindOpcode(packets, 0x2731);
        var resultIndex = FindOpcode(
            packets,
            Opcodes.NpcFunctionActionResponse);
        Check.True(
            experienceIndex >= 0 && experienceIndex < talentIndex &&
            talentIndex < statusIndex &&
            statusIndex < bagIndex && bagIndex < resultIndex &&
            resultIndex == packets.Count - 1,
            "EXP/talent/status/bag projection precedes the native result");
        Check.True(
            packets[talentIndex].SequenceEqual(
                Convert.FromHexString("0C0045280500000015000000")),
            "combined turn-in shows the exact Talent Point gain");
        var status = packets[statusIndex];
        Check.Equal(9_000,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(120, 4)),
            "projected local status carries Silver");
        Check.Equal(395,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(124, 4)),
            "projected local status carries Gold");
        Check.Equal(688,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(136, 4)),
            "projected local status carries B-Gold");
        Check.Equal(30,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(228, 4)),
            "projected local status carries talent points");
        Check.Equal(18_210,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(104, 4)),
            "same-level EXP status preserves current HP");
        Check.Equal(843,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(108, 4)),
            "same-level EXP status preserves current MP");
        Check.Equal(42_000,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(144, 4)),
            "same-level EXP status preserves maximum HP");
        Check.Equal(9_000,
            BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(148, 4)),
            "same-level EXP status preserves maximum MP");
        AssertFunctionResponse(
            packets[resultIndex],
            [3009],
            "committed all-nameplate B-Gold turn-in");
        var secure = fixture.SecureTransport!.CommandResults.Single();
        Check.True(
            secure.Disposition == SecureLegacyCommandDisposition.Applied,
            "committed turn-in settles as applied");
        Check.Equal((ushort)CommandFamily.FactionCrier,
            secure.CommandFamily,
            "turn-in settles the Faction Crier family");
        Check.Equal("secure", fixture.SecureTransport.Events[^1],
            "secure result is emitted after the native result");
    }

    private static void CheckSundayAvailabilityGate()
    {
        var balance = FactionCrierRewardPolicy.CreateReviewedDefault();
        var sunday = new DateTimeOffset(
            2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var disposition = GameClientHandler.EvaluateFactionCrierAvailability(
            80,
            FactionCrierOperation.DailyClaim,
            sunday,
            balance,
            TestRealmCalendar,
            out var realmDay);
        Check.True(
            realmDay.DayOfWeek == DayOfWeek.Sunday &&
            disposition == FactionCrierExecutionDisposition.ClosedToday,
            "handler availability gate closes daily claims on Sunday");
        Check.True(
            GameClientHandler.EvaluateFactionCrierAvailability(
                80,
                FactionCrierOperation.WeeklyReclaim,
                sunday,
                balance,
                TestRealmCalendar,
                out _) is null,
            "Sunday does not close the separately paid weekly reclaim");
    }

    private static async Task CheckBelowLevelFailsWithStockResultAsync()
    {
        var before = CreateSnapshot(
            level: 19,
            GameDefaults.EmptyKitBag,
            after: false);
        var after = CreateSnapshot(
            level: 19,
            GameDefaults.EmptyKitBag,
            after: false);
        var executor = new FactionCrierExecutor();
        await using var fixture = await CreateFixtureAsync(
            secure: true,
            before,
            after,
            executor);

        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(1, operationId: OperationId));

        Check.Equal(1, executor.ReplayCount,
            "below-level secure claim remains replay-first");
        Check.Equal(0, executor.ExecuteCount,
            "below-level claim cannot reach durable execution");
        Check.Equal(0, fixture.Snapshots.ReadCount,
            "below-level claim does not reload a projection");
        AssertFunctionResponse(
            fixture.ReadLegacyPackets().Single(),
            [501],
            "below-level daily claim");
        Check.True(
            fixture.SecureTransport!.CommandResults.Single().Disposition ==
                SecureLegacyCommandDisposition.Rejected,
            "below-level daily claim settles as rejected");
    }

    private static async Task CheckCurrencyFailureSettlesAsync()
    {
        var before = CreateSnapshot(
            level: 80,
            BagWithAllNameplates(),
            after: false);
        var executor = new FactionCrierExecutor
        {
            ExecuteResult = FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.InsufficientCurrency)
        };
        await using var fixture = await CreateFixtureAsync(
            secure: true,
            before,
            before,
            executor);

        await InvokeAsync(
            fixture.Handler,
            CreateAllBoundGoldPacket(OperationId));

        Check.Equal(1, executor.ExecuteCount,
            "currency decision is delegated to durable execution");
        Check.Equal(131, executor.Envelope!.Command.SubId,
            "currency rejection preserves the B-Gold offer sub-ID");
        Check.Equal(0, fixture.Snapshots.ReadCount,
            "insufficient currency does not project uncommitted state");
        AssertFunctionResponse(
            fixture.ReadLegacyPackets().Single(),
            [2000],
            "insufficient B-Gold turn-in");
        var secure = fixture.SecureTransport!.CommandResults.Single();
        Check.True(
            secure.Disposition == SecureLegacyCommandDisposition.Rejected &&
            secure.ResultCode == 2000,
            "currency failure returns stock text and rejects secure intent");
    }

    private static async Task CheckMissingPlateFailsBeforeExecuteAsync()
    {
        var before = CreateSnapshot(
            level: 80,
            GameDefaults.EmptyKitBag,
            after: false);
        var executor = new FactionCrierExecutor();
        await using var fixture = await CreateFixtureAsync(
            secure: true,
            before,
            before,
            executor);

        await InvokeAsync(
            fixture.Handler,
            CreateRenewalPacket(OperationId));

        Check.Equal(1, executor.ReplayCount,
            "missing renewal source is checked only after replay miss");
        Check.Equal(0, executor.ExecuteCount,
            "missing renewal source cannot execute");
        Check.Equal(0, fixture.Snapshots.ReadCount,
            "missing renewal source cannot project state");
        AssertFunctionResponse(
            fixture.ReadLegacyPackets().Single(),
            [800],
            "missing renewal nameplate");
        Check.True(
            fixture.SecureTransport!.CommandResults.Single().Disposition ==
                SecureLegacyCommandDisposition.Rejected,
            "missing renewal plate settles as rejected");
    }

    private static void AssertBagBeforeNativeResult(
        IReadOnlyList<byte[]> packets,
        int nativeSubId,
        string description)
    {
        var bagIndex = FindOpcode(packets, 0x2731);
        var resultIndex = FindOpcode(
            packets,
            Opcodes.NpcFunctionActionResponse);
        Check.True(
            bagIndex >= 0 && bagIndex < resultIndex &&
            resultIndex == packets.Count - 1,
            $"{description} refreshes bag before final native result");
        AssertFunctionResponse(
            packets[resultIndex],
            [nativeSubId],
            $"{description} native result");
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

    private static void AssertFunctionResponse(
        byte[] packet,
        IReadOnlyList<int> expectedSubIds,
        string description)
    {
        Check.Equal(
            12 + (expectedSubIds.Count * sizeof(int)),
            packet.Length,
            $"{description} length");
        Check.Equal(
            Opcodes.NpcFunctionActionResponse,
            ReadOpcode(packet),
            $"{description} opcode");
        Check.Equal(
            FactionCrierProtocol.AthensNpcId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4)),
            $"{description} NPC");
        Check.Equal(
            FactionCrierProtocol.DialogIndex,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8, 4)),
            $"{description} dialog");
        for (var index = 0; index < expectedSubIds.Count; index++)
        {
            Check.Equal(
                expectedSubIds[index],
                BinaryPrimitives.ReadInt32LittleEndian(
                    packet.AsSpan(12 + (index * sizeof(int)), 4)),
                $"{description} sub-ID {index}");
        }
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
}
