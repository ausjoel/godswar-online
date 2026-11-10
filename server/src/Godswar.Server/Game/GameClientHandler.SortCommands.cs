using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int SortKitBagSlotCount = 96;

    /// <summary>
    /// Player chat commands <c>/sort</c> (bag) and <c>/sortwh</c> (warehouse). Any recognised line is consumed here:
    /// it is never echoed to map chat and never needs operator authority, because a sort only reorders the
    /// player's OWN records through the same durable swap command a drag in the bag uses.
    /// </summary>
    private async Task<bool> HandlePlayerSortCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text) ||
            !InventorySortCommand.TryParseChatLine(text, out var target))
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
            message = target == InventorySortTarget.Bag
                ? await SortKitBagAsync(cancellationToken)
                : await SortWarehouseAsync(cancellationToken);
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
            Console.Error.WriteLine($"[sort] bag sort failed: {exception.Message}");
            message = "[sort] Sorting stopped because of a server problem. Your items are unchanged or partly sorted, never lost.";
            await TryRefreshAfterSortFailureAsync(cancellationToken);
        }

        await SendDeveloperItemFeedbackAsync(packet, message, cancellationToken);
        return true;
    }

    private async Task<string> SortKitBagAsync(CancellationToken cancellationToken)
    {
        var character = _character!;
        if (PlayerTradeBook.Shared.IsTrading(character.Id) ||
            PlayerTradeBook.Shared.FindStall(character.Id) is not null)
        {
            return "[sort] Finish your trade or close your stall before sorting.";
        }

        var durable = _session.IsSecure && _kitBagItemMoveCommands is not null;
        if (!durable &&
            !CanUseLegacyPlayerMutationFallback(
                _requiresDurablePlayerCommands,
                _session.IsSecure,
                _legacyAuthenticationAccess is not null))
        {
            return "[sort] Sorting is not available on this connection.";
        }

        var entries = new string[SortKitBagSlotCount];
        for (var slot = 0; slot < entries.Length; slot++)
        {
            entries[slot] = KitBagSlots.GetEntry(character.KitBag, slot);
        }

        var swaps = InventorySortPlanner.PlanSwaps(
            entries,
            InventorySortOrder.Default(RequireItemContent().Templates));
        if (swaps.Count == 0)
        {
            return "[sort] Your bag is already sorted.";
        }

        PlayerOwnershipFence ownership = default;
        if (durable && !TryCaptureCurrentPlayerOwnership(out ownership))
        {
            RejectLostPlayerOwnership();
            return string.Empty;
        }

        var subject = new CommandSubject(_account!.Id, character.Id);
        var moved = 0;
        foreach (var swap in swaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceEntry = KitBagSlots.GetEntry(character.KitBag, swap.Source);
            var destinationEntry = KitBagSlots.GetEntry(character.KitBag, swap.Destination);
            if (!await TrySortSwapAsync(subject, ownership, swap, durable, cancellationToken))
            {
                break;
            }

            // FINALISE2 (P5, found with the real exe under gwhost): the full refresh below repaints every filled
            // cell but never clears one that became empty, so the client kept a ghost of the last item. Each
            // committed swap now gets the same move acknowledgement a drag gets, so the client moves its icons too.
            await _session.SendAsync(
                PacketBuilder.StorageItemKitBagMove(swap.Source, swap.Destination),
                cancellationToken,
                "SortKitBagMoveAck");

            // Mirror the stored result so the next swap's preconditions describe what is now in the database.
            character.KitBag = KitBagSlots.SetSlot(
                KitBagSlots.SetSlot(character.KitBag, swap.Source, destinationEntry),
                swap.Destination,
                sourceEntry);
            moved++;
        }

        // The database is the truth: reload the projection once, then resend the whole bag (the same refresh
        // that follows any drag), so the open bag window shows exactly what was stored.
        if (durable)
        {
            await ReloadDurableKitBagMoveProjectionAsync(ownership, cancellationToken);
        }

        await SendKitBagRefreshAsync(cancellationToken);
        return moved == swaps.Count
            ? $"[sort] Bag sorted ({moved} move{(moved == 1 ? string.Empty : "s")})."
            : $"[sort] Sorting stopped after {moved} of {swaps.Count} moves because your bag changed. Nothing was lost; type /sort again.";
    }

    /// <summary>
    /// <c>/sortwh</c>: sorts the whole warehouse with the same revision-checked InternalMove command a drag between two
    /// warehouse cells uses, one swap at a time. It needs the warehouse NPC context a warehouse window already holds, so
    /// it works only while the warehouse is open.
    /// </summary>
    private async Task<string> SortWarehouseAsync(CancellationToken cancellationToken)
    {
        if (!TryAuthorizeWarehouseTransfer(out var warehouseNpc))
        {
            return "[sort] Open your warehouse first, then type /sortwh.";
        }

        if (!_session.IsSecure && !AllowLegacyPlayerMutationFallback("warehouse_transfer"))
        {
            return "[sort] Sorting is not available on this connection.";
        }

        if (!HasCurrentWarehouseRealmAuthority() || !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return string.Empty;
        }

        if (_warehouseSnapshots is null || _warehouseTransferCommands is null)
        {
            return "[sort] The warehouse is not available right now.";
        }

        var subject = new CommandSubject(_account!.Id, _character!.Id);
        var initial = await ReadWarehouseTransferStateAsync(subject, ownership, cancellationToken);
        if (initial is null)
        {
            return "[sort] The warehouse is not available right now.";
        }

        var entries = Enumerable.Range(0, initial.Warehouse.Capacity)
            .Select(slot => WarehouseItemState(initial.Warehouse, slot))
            .ToArray();
        var swaps = InventorySortPlanner.PlanSwaps(
            entries,
            InventorySortOrder.Default(RequireItemContent().Templates));
        if (swaps.Count == 0)
        {
            return "[sort] Your warehouse is already sorted.";
        }

        var moved = 0;
        foreach (var swap in swaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWarehouseNpc(warehouseNpc) || !RevalidateCurrentPlayerOwnership(ownership))
            {
                break;
            }

            var state = await ReadWarehouseTransferStateAsync(subject, ownership, cancellationToken);
            var identity = _session.IsSecure
                ? WarehouseOperationIdentity.SecureClient(Guid.NewGuid())
                : WarehouseOperationIdentity.RawLocalServer(Guid.NewGuid(), _commandConnectionId);
            var intent = new WarehouseTransferIntent(
                WarehouseTransferOperation.InternalMove,
                swap.Source,
                WarehouseCapacityPolicy.AutomaticKitBagSlot,
                swap.Destination,
                0,
                WarehouseStorageType.Normal);
            if (state is null ||
                !TryCreateWarehouseTransferCommand(identity, _processRealmId.Value, intent, state, out var command, out _))
            {
                break;
            }

            var envelope = WarehouseTransferCommandEnvelope.Create(
                subject,
                CreateWarehouseCommandCorrelation(identity),
                DateTimeOffset.UtcNow,
                command) with { Ownership = ownership };
            var execution = await _warehouseTransferCommands.ExecuteAsync(envelope, cancellationToken);
            if (execution.Disposition != WarehouseTransferExecutionDisposition.Committed ||
                execution.Receipt is not { Status: WarehouseTransferResultStatus.InternalMoved or WarehouseTransferResultStatus.Swapped })
            {
                break;
            }

            moved++;
        }

        // Project the authoritative result once: clear every cell that became empty (the same acknowledgement a drag
        // to an empty cell sends), then the whole warehouse snapshot.
        var final = await ReadWarehouseTransferStateAsync(subject, ownership, cancellationToken);
        if (final is not null && RevalidateCurrentPlayerOwnership(ownership))
        {
            ApplyWarehouseKitBagProjection(final.Character, final.Warehouse.InventoryRevision);
            for (var slot = 0; slot < entries.Length; slot++)
            {
                if (entries[slot] != "[]" && WarehouseItemState(final.Warehouse, slot) == "[]")
                {
                    await _session.SendAsync(
                        PacketBuilder.WarehouseTransferAcknowledgement(
                            new WarehouseTransferIntent(
                                WarehouseTransferOperation.Withdraw,
                                slot,
                                WarehouseCapacityPolicy.AutomaticKitBagSlot,
                                WarehouseCapacityPolicy.AutomaticWarehouseSlot,
                                0,
                                WarehouseStorageType.Normal)),
                        cancellationToken,
                        "WarehouseSortSourceClear");
                }
            }

            await SendWarehouseSnapshotAsync(final.Warehouse, cancellationToken, "WarehouseSortSnapshot");
        }

        return moved == swaps.Count
            ? $"[sort] Warehouse sorted ({moved} move{(moved == 1 ? string.Empty : "s")})."
            : $"[sort] Sorting stopped after {moved} of {swaps.Count} moves because your warehouse changed. Nothing was lost; type /sortwh again.";
    }

    private async Task<bool> TrySortSwapAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        InventorySortPlanner.Swap swap,
        bool durable,
        CancellationToken cancellationToken)
    {
        if (durable)
        {
            var execution = await ExecuteDurableKitBagItemMoveAsync(
                subject,
                swap.Source,
                swap.Destination,
                Guid.NewGuid(),
                ownership,
                cancellationToken);
            return execution.Disposition == KitBagItemMoveExecutionDisposition.Committed &&
                execution.Receipt is { Status: KitBagItemMoveResultStatus.Moved or KitBagItemMoveResultStatus.Swapped };
        }

        // Local-development raw sessions only (the same gate the legacy drag uses).
        var updated = await _store.MoveKitBagItemAsync(
            subject.AccountId,
            subject.CharacterId,
            swap.Source,
            swap.Destination,
            cancellationToken);
        if (updated is null)
        {
            return false;
        }

        InstallUpdatedCharacter(updated);
        _registry.UpdateCharacter(_session, _character!, advanceWorldRevision: false);
        return true;
    }

    private async Task TryRefreshAfterSortFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_session.IsSecure && _kitBagItemMoveCommands is not null &&
                TryCaptureCurrentPlayerOwnership(out var ownership))
            {
                await ReloadDurableKitBagMoveProjectionAsync(ownership, cancellationToken);
            }

            await SendKitBagRefreshAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[sort] refresh after failure also failed: {exception.Message}");
        }
    }
}
