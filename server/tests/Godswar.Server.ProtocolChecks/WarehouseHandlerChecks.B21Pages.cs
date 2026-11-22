using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// B21 (F1): client 1.5.4's box buttons. /whpage N selects a logical box: the 40 physical cells are emptied and
/// refilled from that box, a deposit into physical cell c lands in slot 40 x (N - 1) + c, and a locked box
/// shows empty and names the Warehouse Manager.
/// </summary>
internal static partial class WarehouseHandlerChecks
{
    private static readonly MethodInfo SelectWarehousePageMethod = FindHandlerMethod("SelectWarehousePageAsync");

    private static async Task<string> SelectPageAsync(GameClientHandler handler, int page) =>
        await (Task<string>)SelectWarehousePageMethod.Invoke(handler, [page, CancellationToken.None])!;

    private static async Task CheckB21BoxPagesAsync()
    {
        const int capacity = 120;          // three boxes open
        const int boxThreeCell = 5;
        const int logical = 80 + boxThreeCell;
        var beforeBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, KitBagSlot, StorageKey.ToCompactString());
        var beforeCharacter = CharacterSnapshot(beforeBag, BeforeInventoryRevision, "warehouse-b21-before");
        var afterCharacter = CharacterSnapshot(GameDefaults.EmptyKitBag, AfterInventoryRevision, "warehouse-b21-after");
        var emptyWarehouse = WarehouseSnapshot(BeforeInventoryRevision, containsKey: false, capacity: capacity);
        var afterWarehouse = WarehouseSnapshot(AfterInventoryRevision, containsKey: true, capacity: capacity,
            itemSlot: logical);
        var executor = new WarehouseTransferExecutor
        {
            ExecuteResult = WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.Committed,
                DepositReceipt(logical, capacity)),
            ExpectedWarehouseSlot = logical // the replay lookup already sees the logical slot
        };
        await using var fixture = await CreateFixtureAsync(
            beforeCharacter,
            [beforeCharacter, afterCharacter],
            [emptyWarehouse, emptyWarehouse, emptyWarehouse, afterWarehouse, afterWarehouse, emptyWarehouse],
            executor);
        await OpenWarehouseAsync(fixture);
        var opened = fixture.ReadPackets().Count;

        var reply = await SelectPageAsync(fixture.Handler, 2);
        var switched = fixture.ReadPackets().Skip(opened).ToArray();
        Check.True(reply.Length == 0 &&
                !switched.Any(packet => ReadOpcode(packet) == Opcodes.WarehouseTransfer) &&
                switched.Count(packet => ReadOpcode(packet) == Opcodes.WarehouseSnapshot) == 4 &&
                GetHandlerField<int>(fixture.Handler, "_warehouseSelectedPage") == 2,
            "B21: /whpage 3 from an empty box 1 projects box 3, sends no clears and says nothing");
        opened += switched.Length;

        await InvokeAsync(fixture.Handler, CreateDepositRequest(boxThreeCell));
        var deposit = fixture.ReadPackets().Skip(opened).ToArray();
        Check.True(executor.Envelope?.Command is { } command &&
                command.WarehouseSlot == logical && command.KitBagSlot == KitBagSlot &&
                deposit.Count(packet => ReadOpcode(packet) == Opcodes.WarehouseSnapshot) == 4 &&
                !deposit.Any(packet => ReadOpcode(packet) == Opcodes.WarehouseTransfer),
            "B21: a deposit into physical cell 5 on box 3 lands in slot 85 and re-projects box 3 only");
        opened += deposit.Length;

        var locked = await SelectPageAsync(fixture.Handler, 6);
        var lockedPackets = fixture.ReadPackets().Skip(opened).ToArray();
        Check.True(locked.Contains("Storage Box 7 is locked", StringComparison.Ordinal) &&
                locked.Contains("Warehouse Manager", StringComparison.Ordinal) &&
                lockedPackets.Length == 2 &&
                ReadOpcode(lockedPackets[0]) == Opcodes.WarehouseTransfer &&
                BinaryPrimitives.ReadInt16LittleEndian(lockedPackets[0].AsSpan(4)) == boxThreeCell &&
                ReadOpcode(lockedPackets[1]) == 0x2744 && IsKitBagClear(lockedPackets[1], 0),
            "B21: a locked box shows empty: box 3's one item leaves cell 5 through empty bag cell 0, which is then " +
            "deleted; no snapshot; the reply names the Warehouse Manager");

        SetHandlerField<WarehouseAccessContext?>(fixture.Handler, "_warehouseAccessContext", null);
        Check.True((await SelectPageAsync(fixture.Handler, 0)).Contains("Open your warehouse first", StringComparison.Ordinal),
            "B21: /whpage needs the warehouse window open");
    }
}
