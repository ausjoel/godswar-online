using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task CompleteWarehouseTransferAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        WarehouseOperationIdentity identity,
        WarehouseTransferIntent intent,
        WarehouseTransferExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var receipt = execution.Receipt;
        if (receipt is not null)
        {
            receipt.Validate();
            ValidateWarehouseTransferReceipt(intent, receipt);
        }

        WarehouseTransferAuthoritativeState? state;
        try
        {
            state = await ReadWarehouseTransferStateAsync(
                subject,
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "[warehouse] authoritative projection remains pending: " +
                exception.Message);
            return;
        }

        if (state is null ||
            !RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        if (receipt is not null)
        {
            ValidateWarehouseTransferProjection(state.Warehouse, receipt);
        }
        ApplyWarehouseKitBagProjection(
            state.Character,
            receipt?.InventoryRevision ?? 0);

        if (receipt?.Succeeded == true)
        {
            foreach (var sourceSlot in receipt.Mutations
                         .Where(static mutation =>
                             mutation.BeforeLocation ==
                                 WarehouseInventoryLocation.KitBag)
                         .Select(static mutation => mutation.BeforeSlot)
                         .Distinct()
                         .Order())
            {
                // Detail/index snapshots hydrate replacements but do not
                // evict an item object already instantiated by Origin.
                await _session.SendAsync(
                    PacketBuilder.StorageItemKitBagDelete(sourceSlot),
                    cancellationToken,
                    "WarehouseTransferKitBagClear");
            }

            foreach (var vacatedWarehouseSlot in receipt.Mutations
                         .Where(static mutation =>
                             mutation.BeforeLocation ==
                                 WarehouseInventoryLocation.Warehouse &&
                             (mutation.AfterLocation !=
                                  WarehouseInventoryLocation.Warehouse ||
                              mutation.AfterSlot != mutation.BeforeSlot))
                         .Select(static mutation => mutation.BeforeSlot)
                         .Distinct()
                         .Order())
            {
                // The mirror of the kit-bag eviction above. An authoritative
                // MSG_STORAGE snapshot only hydrates the cells named by its
                // occupancy mask; it never tears down the warehouse slot view
                // the native client already instantiated. Without this
                // acknowledgement the vacated cell keeps rendering its stale
                // icon until the warehouse is closed and reopened, so a
                // withdrawal looks like it left a copy behind.
                //
                // The acknowledgement must name the resolved bag cell. An
                // automatic withdrawal (the native right-click path) carries
                // a -1 bag sentinel in its request, so echoing that sentinel
                // back leaves the client with no landing cell for the item it
                // is still holding: the drag never settles, the icon stays
                // put, and every later warehouse operation fails. The receipt
                // already resolved the real slot during execution; only fall
                // back to the sentinel when execution produced none.
                var resolvedKitBagSlot =
                    receipt.ActualKitBagSlot !=
                        WarehouseCapacityPolicy.AutomaticKitBagSlot
                        ? receipt.ActualKitBagSlot
                        : intent.KitBagSlot;
                await _session.SendAsync(
                    PacketBuilder.WarehouseTransferAcknowledgement(
                        new WarehouseTransferIntent(
                            WarehouseTransferOperation.Withdraw,
                            vacatedWarehouseSlot,
                            resolvedKitBagSlot,
                            WarehouseCapacityPolicy.AutomaticWarehouseSlot,
                            0,
                            WarehouseStorageType.Normal)),
                    cancellationToken,
                    "WarehouseTransferSourceClear");
            }
        }

        // The client presents one physical 40-cell view over the selected
        // logical box. An authoritative page projection works for same-page
        // and cross-page moves alike and clears absent cells exactly.
        await SendKitBagRefreshAsync(cancellationToken);
        await SendWarehouseSnapshotAsync(
            state.Warehouse,
            cancellationToken,
            "WarehouseTransferSnapshot");

        var status = receipt?.Status ??
            WarehouseTransferResultStatus.ConcurrentConflict;
        var secureDisposition = ResolveWarehouseTransferSecureDisposition(
            execution,
            receipt);
        await SendWarehouseTransferSecureResultAsync(
            identity.IsSecureClient ? identity.OperationId : null,
            status,
            secureDisposition,
            receipt?.InventoryRevision ??
                state.Warehouse.InventoryRevision,
            cancellationToken);
    }

    private async Task SendWarehouseTransferSecureResultAsync(
        Guid? operationId,
        WarehouseTransferResultStatus status,
        SecureLegacyCommandDisposition disposition,
        long revision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure || !operationId.HasValue)
        {
            return;
        }

        await SendSecureGearMentorResultAsync(
            operationId.Value,
            CommandFamily.WarehouseTransfer,
            (int)status,
            disposition,
            Math.Max(0, revision),
            cancellationToken);
    }

    private static SecureLegacyCommandDisposition
        ResolveWarehouseTransferSecureDisposition(
        WarehouseTransferExecutionResult execution,
        WarehouseTransferExecutionReceipt? receipt)
    {
        if (receipt?.Succeeded == true)
        {
            return execution.Disposition ==
                    WarehouseTransferExecutionDisposition.Committed
                ? SecureLegacyCommandDisposition.Applied
                : SecureLegacyCommandDisposition.Replayed;
        }

        return execution.Disposition ==
                WarehouseTransferExecutionDisposition.RequestHashConflict
            ? SecureLegacyCommandDisposition.Conflict
            : SecureLegacyCommandDisposition.Rejected;
    }

    private void ValidateWarehouseTransferReceipt(
        in WarehouseTransferIntent intent,
        WarehouseTransferExecutionReceipt receipt)
    {
        if (_character is null ||
            receipt.CharacterId != _character.Id ||
            receipt.Operation != intent.Operation ||
            receipt.WarehouseSlot != intent.WarehouseSlot ||
            receipt.KitBagSlot != intent.KitBagSlot ||
            receipt.DestinationWarehouseSlot !=
                intent.DestinationWarehouseSlot)
        {
            throw new InvalidDataException(
                "The warehouse transfer receipt identity is inconsistent.");
        }
    }

    private static void ValidateWarehouseTransferProjection(
        WarehouseSnapshot snapshot,
        WarehouseTransferExecutionReceipt receipt)
    {
        if (snapshot.CharacterId != receipt.CharacterId ||
            snapshot.InventoryRevision < receipt.InventoryRevision ||
            snapshot.WarehouseRevision < receipt.WarehouseRevision ||
            snapshot.WarehouseRevision == receipt.WarehouseRevision &&
                snapshot.Capacity != receipt.Capacity)
        {
            throw new InvalidDataException(
                "The authoritative warehouse projection predates its receipt.");
        }
    }
}
