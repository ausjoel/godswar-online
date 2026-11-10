using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // Native 10269 applies the six exact increments and removes the deputy
    // pet in-place. Sending 10237 here would rebuild carry state and can
    // trigger an unintended Recall, so Merge has a narrow result.
    private async Task<bool> SendPetToPetMergeProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        if (!receipt.Succeeded)
        {
            return true;
        }

        if (disposition == PetDurableExecutionDisposition.Committed)
        {
            var primary = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            if (primary is null ||
                pets.Any(candidate =>
                    candidate.PetId == receipt.DeputyPetId) ||
                receipt.PetMergeDelta is not { } delta)
            {
                return false;
            }

            await _session.SendAsync(
                PacketBuilder.PetToPetMergeResult(
                    receipt.PetId,
                    receipt.DeputyPetId,
                    delta),
                cancellationToken,
                "DurablePetToPetMerge");
        }
        else if (!pets.Any(static pet =>
                     pet.ContributesToCharacter))
        {
            // 10269 is additive in the stock client and therefore cannot be
            // replayed safely. A duplicate durable receipt receives a
            // complete authoritative list only while no owner Merge is
            // active. Native 10237 can otherwise auto-Recall that newer
            // owner-Merge state.
            await _session.SendAsync(
                PacketBuilder.OwnedPetList(
                    RequirePetContent(),
                    pets,
                    _characterLoadSnapshot?.PetShed.OpenedCellCount ??
                        PetShedCapacityPolicy.DefaultOpenedCellCount),
                cancellationToken,
                "DurablePetToPetMergeReplayReconcile");
        }
        if (!await SendPetSkillOwnerStatRefreshAsync(
                "DurablePetToPetMergeRank",
                cancellationToken))
        {
            return false;
        }
        await SendKitBagRefreshAsync(cancellationToken);
        return true;
    }
}
