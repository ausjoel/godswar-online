using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendPetCaptureAcquisitionAsync(
        string bagBefore,
        string bagAfter,
        CancellationToken cancellationToken,
        uint expectedEggItemId = RockElfEggItemId)
    {
        if (!TryResolvePetCaptureAcquisition(
                bagBefore,
                bagAfter,
                out var eggSlot,
                out var egg,
                expectedEggItemId))
        {
            Console.WriteLine(
                "[pet-capture] acquisition log skipped because the " +
                "committed egg bag delta was not unique");
            return;
        }

        var scratchSlot = GetPetCaptureAcquisitionScratchSlot(
            bagAfter,
            eggSlot,
            out var deleteBefore,
            expectedEggItemId);
        if (scratchSlot < 0)
        {
            Console.WriteLine(
                "[pet-capture] acquisition log skipped because no " +
                "temporary bag slot was available");
            return;
        }

        if (deleteBefore)
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemKitBagDelete(scratchSlot),
                cancellationToken,
                "PetCaptureAcquisitionScratchClear");
        }
        await _session.SendAsync(
            PacketBuilder.SystemAddItemWithAcquisitionLog(egg),
            cancellationToken,
            "PetCaptureAcquisition");
        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagDelete(scratchSlot),
            cancellationToken,
            "PetCaptureAcquisitionCleanup");
        await SendKitBagRefreshAsync(cancellationToken);
    }

    internal static bool TryResolvePetCaptureAcquisition(
        string bagBefore,
        string bagAfter,
        out int eggSlot,
        out CompactItemEntry egg,
        uint expectedEggItemId = RockElfEggItemId)
    {
        eggSlot = -1;
        egg = default;
        for (var slot = 0; slot < 96; slot++)
        {
            var current = KitBagSlots.GetItem(bagAfter, slot);
            if (current.Id != expectedEggItemId ||
                current == KitBagSlots.GetItem(bagBefore, slot))
            {
                continue;
            }
            if (eggSlot >= 0)
            {
                eggSlot = -1;
                egg = default;
                return false;
            }

            eggSlot = slot;
            egg = current;
        }

        return eggSlot >= 0 && egg.Stack == 1;
    }

    internal static int GetPetCaptureAcquisitionScratchSlot(
        string bagAfter,
        int eggSlot,
        out bool deleteBefore,
        uint expectedEggItemId = RockElfEggItemId)
    {
        for (var slot = 0; slot < 96; slot++)
        {
            if (KitBagSlots.GetItem(bagAfter, slot).IsEmpty)
            {
                deleteBefore = false;
                return slot;
            }
        }

        deleteBefore = eggSlot is >= 0 and < 96 &&
            KitBagSlots.GetItem(bagAfter, eggSlot).Id ==
                expectedEggItemId;
        return deleteBefore ? eggSlot : -1;
    }
}
