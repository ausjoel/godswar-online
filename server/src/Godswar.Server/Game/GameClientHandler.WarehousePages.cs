using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// B21 (F1, the admin 2026-11-21): <c>/whpage N</c> from client 1.5.4's warehouse box buttons. Any player; it needs
/// the warehouse window open (the same lease a transfer needs). Success says nothing in chat: the cells of
/// the previous box are cleared and the chosen box is projected into the 40 cells. A locked box answers with
/// one chat line. See <see cref="WarehousePageCommand"/>.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task<bool> HandlePlayerWarehousePageCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text) ||
            !WarehousePageCommand.TryParseChatLine(text, out var box))
        {
            return false;
        }

        if (_account is null || _character is null)
        {
            return true;
        }

        string message;
        try
        {
            message = await SelectWarehousePageAsync(box - 1, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[warehouse] page switch failed: {exception.Message}");
            message = "[warehouse] The box could not be shown. Please try again.";
        }

        if (message.Length != 0)
        {
            await SendDeveloperItemFeedbackAsync(packet, message, cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// B21: removes stale item objects from physical warehouse cells. The stock client has no "empty this cell"
    /// message (a snapshot only fills cells; upstream's shim cleared them in memory). Its transfer handler
    /// (GodsWar.exe 0x4e0798..0x4e07d2) moves a cell's item to a NAMED bag cell when it gets a withdraw
    /// acknowledgement, and a cell that is already empty is left alone (0x4c1e5f). So each stale cell is moved to
    /// one bag cell that is empty on the server, and that bag cell is then deleted (0x2744, the bag-clear
    /// message). The real bag never changes. Without an empty bag cell nothing is sent (icons stay until reopen).
    /// </summary>
    private async Task SendWarehouseCellClearsAsync(
        IReadOnlyCollection<int> cells,
        CancellationToken cancellationToken,
        string reason)
    {
        if (cells.Count == 0 || _character is null)
        {
            return;
        }

        var kitBag = _character.KitBag;
        var parking = Enumerable.Range(0, SortKitBagSlotCount)
            .Where(slot => KitBagSlots.GetEntry(kitBag, slot) == "[]")
            .Select(static slot => (int?)slot)
            .FirstOrDefault();
        if (parking is not { } bagSlot)
        {
            Console.WriteLine($"[warehouse] {reason}: bag full, {cells.Count} stale cell(s) left until the window reopens");
            return;
        }

        foreach (var cell in cells.Order())
        {
            await _session.SendAsync(
                PacketBuilder.WarehouseTransferAcknowledgement(
                    new WarehouseTransferIntent(
                        WarehouseTransferOperation.Withdraw,
                        cell,
                        bagSlot,
                        WarehouseCapacityPolicy.AutomaticWarehouseSlot,
                        0,
                        WarehouseStorageType.Normal)),
                cancellationToken,
                reason);
            await _session.SendAsync(
                PacketBuilder.StorageItemKitBagDelete(bagSlot),
                cancellationToken,
                reason + "Bag");
        }
    }

    /// <summary>B21: the physical cells box <paramref name="page"/> fills, from the snapshot.</summary>
    private static HashSet<int> WarehouseCellsOfBox(WarehouseSnapshot snapshot, int page) =>
        snapshot.Items
            .Where(item => item.Slot / WarehouseCapacityPolicy.SlotsPerBox == page)
            .Select(static item => item.Slot % WarehouseCapacityPolicy.SlotsPerBox)
            .ToHashSet();

    private async Task<string> SelectWarehousePageAsync(int page, CancellationToken cancellationToken)
    {
        if (!TryAuthorizeWarehouseTransfer(out var npc) ||
            _warehouseSnapshots is null ||
            !HasCurrentWarehouseRealmAuthority() ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            return "[warehouse] Open your warehouse first.";
        }

        var snapshot = await _warehouseSnapshots.ReadAsync(
            new CommandSubject(_account!.Id, _character!.Id),
            ownership,
            cancellationToken);
        if (snapshot is null ||
            !RevalidateCurrentPlayerOwnership(ownership) ||
            !IsCurrentWarehouseNpc(npc) ||
            snapshot.AccountId != _account.Id ||
            snapshot.CharacterId != _character.Id)
        {
            return "[warehouse] Open your warehouse first.";
        }
        snapshot.Validate();

        var unlocked = WarehouseCapacityPolicy.BoxNumber(snapshot.Capacity);
        if (page >= unlocked)
        {
            // Show the locked box empty (the button stays highlighted, as a locked stock tab would) and say why.
            _warehouseSelectedPage = page;
            await SendWarehouseSnapshotAsync(snapshot, cancellationToken, "WarehousePageLocked");
            return $"[warehouse] Storage Box {page + 1} is locked. The Warehouse Manager opens the next box " +
                "with Storage Box Keys.";
        }

        // SendWarehouseSnapshotAsync empties the 40 cells when the box on screen changes, then fills them.
        _warehouseSelectedPage = page;
        await SendWarehouseSnapshotAsync(snapshot, cancellationToken, "WarehousePageSnapshot");
        Console.WriteLine(
            $"[warehouse] page character={_character.Id} box={page + 1} of {unlocked}");
        return string.Empty;
    }
}
