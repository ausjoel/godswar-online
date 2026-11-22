using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleWarehouseOpenAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken,
        int page = 0,
        bool issueAccess = true)
    {
        if (_account is null ||
            _character is null ||
            _warehouseSnapshots is null ||
            !HasCurrentWarehouseRealmAuthority() ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            return;
        }
        if (page is < 0 or >=
                WarehouseCapacityPolicy.MaximumSupportedBoxCount)
        {
            return;
        }

        WarehouseSnapshot? snapshot;
        try
        {
            snapshot = await _warehouseSnapshots.ReadAsync(
                new CommandSubject(_account.Id, _character.Id),
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
                "[warehouse] open snapshot unavailable: " +
                exception.Message);
            return;
        }

        if (snapshot is null ||
            !RevalidateCurrentPlayerOwnership(ownership) ||
            snapshot.AccountId != _account.Id ||
            snapshot.CharacterId != _character.Id ||
            !TryResolveMapNpc(npc.InteractionId, out var currentNpc) ||
            !WarehouseNpcProtocol.IsWarehouseEndpoint(
                currentNpc.NpcKey,
                currentNpc.InteractionId) ||
            !CanUseDuelArenaWarehouseNpc(currentNpc))
        {
            return;
        }
        snapshot.Validate();
        if (page >= WarehouseCapacityPolicy.BoxNumber(snapshot.Capacity))
        {
            return;
        }

        _warehouseSelectedPage = page;
        await SendWarehouseSnapshotAsync(
            snapshot,
            cancellationToken,
            "WarehouseOpenSnapshot");
        if (issueAccess)
        {
            IssueWarehouseAccess(currentNpc);
        }
    }

    private async Task SendWarehouseSnapshotAsync(
        WarehouseSnapshot snapshot,
        CancellationToken cancellationToken,
        string reason)
    {
        // B21: the client's 40 cells keep whatever box they showed last (a snapshot only fills the cells it
        // names). When the box on screen changes, empty the cells only the old box used; a locked box is shown
        // empty. A 1.5.3 client always stays on box 1, so this never fires for it.
        var locked = _warehouseSelectedPage >= WarehouseCapacityPolicy.BoxNumber(snapshot.Capacity); // B21
        if (_warehouseSelectedPage != _warehouseShownPage) // B21
        {
            // Cells the old box filled that the new box leaves empty (a filled cell is simply replaced).
            var stale = WarehouseCellsOfBox(snapshot, _warehouseShownPage);
            if (!locked)
            {
                stale.ExceptWith(WarehouseCellsOfBox(snapshot, _warehouseSelectedPage));
            }
            await SendWarehouseCellClearsAsync(stale, cancellationToken, reason + "Clear");
        }
        _warehouseShownPage = _warehouseSelectedPage; // B21
        if (locked)
        {
            return;
        }

        foreach (var packet in PacketBuilder.WarehousePageSnapshotPackets(
                     snapshot,
                     _warehouseSelectedPage))
        {
            await _session.SendAsync(
                packet,
                cancellationToken,
                reason);
        }
    }
}
