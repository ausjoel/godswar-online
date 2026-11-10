using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

/// <summary>
/// W0 F-55 (the admin, 2026-11-05/08): Weak Purge Potion 10102 (client Use=1). Right-clicking it removes ONE random active
/// skill from the summoned pet, then compacts the remaining skills; Strong Purge 10101 (Pet Manager) stays the way to
/// remove a chosen skill. Reuses the Strong Purge delete/compact/revision steps.
/// </summary>
internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ApplyWeakPurgeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.Stack < 1 || item.PropId != (int)PetItemCatalog.WeakPurgePotion)
        {
            return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        }

        var pet = await LockSummonedPetForSkillUnlearnAsync(connection, transaction, characterId, cancellationToken);
        if (pet is null)
        {
            return new(PetDurableReceiptStatus.PetNotTaken, KitBagSlot: bagSlot);
        }

        var skills = await LockPetSkillsAsync(connection, transaction, pet.PetId, cancellationToken);
        var active = skills.Where(static skill => skill.IsActive).OrderBy(static skill => skill.SlotIndex).ToArray();
        if (active.Length == 0)
        {
            // Nothing to purge: the potion stays in the bag.
            return new(PetDurableReceiptStatus.PetSkillNotFound, KitBagSlot: bagSlot, PetId: pet.PetId,
                PetRevision: pet.Revision, IsCarried: true, IsSummoned: true);
        }

        var selected = active[Random.Shared.Next(active.Length)];
        await DeletePetSkillAsync(connection, transaction, pet.PetId, selected, cancellationToken);
        await CompactPetSkillsAsync(connection, transaction, pet.PetId,
            skills.Where(skill => skill.SkillId != selected.SkillId).OrderBy(static skill => skill.SlotIndex).ToArray(),
            cancellationToken);
        var nextPetRevision = await AdvanceSkillUnlearnPetRevisionAsync(connection, transaction, characterId, pet,
            cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction, characterId,
            character.InventoryRevision, cancellationToken);
        Console.WriteLine(
            $"[pet-weak-purge] character={characterId} pet={pet.PetId} removed skill={selected.SkillId} " +
            $"slot={selected.SlotIndex} of {active.Length}");

        return new(
            PetDurableReceiptStatus.PetSkillUnlearned,
            KitBagSlot: bagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextPetRevision,
            IsCarried: true,
            IsSummoned: true,
            InventoryMutations:
            [
                new InventoryMutation(item.ItemId, consumed.MutationKind, item.BeforeState, consumed.AfterState,
                    "pet_weak_purge", inventoryRevision)
            ]);
    }
}
