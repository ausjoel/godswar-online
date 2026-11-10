using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    /// <summary>
    /// Feeds one reviewed pet-care consumable to the character's summoned pet.
    /// The client states the requirement in LuaText.NF_L0_NTR11: only a pet
    /// that is actually out (唤出) can have its amity, satiety, or energy
    /// refilled, so carrying alone is refused.
    /// </summary>
    private async Task<PetTransition> ApplyPetCareItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        PetCareItemDefinition definition,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.Stack < 1 || item.PropId != definition.ItemId)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }

        var pet = await LockSummonedPetForCareItemAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        if (pet is null)
        {
            return new(
                PetDurableReceiptStatus.PetNotTaken,
                KitBagSlot: bagSlot);
        }

        if (!_petContent.TryGetSpecies(pet.SpeciesId, out var species))
        {
            throw new InvalidDataException(
                "The summoned pet has no published species definition.");
        }

        var satiety = Clamp(
            pet.Satiety +
                PetCareItemPolicy.ResolveSatiety(definition, species.FoodKind),
            PetCareDecayPolicy.MinimumSatiety,
            PetCareDecayPolicy.MaximumSatiety);
        var amity = Clamp(
            pet.Amity +
                PetCareItemPolicy.ResolveAmity(definition, species.FoodKind),
            PetCareDecayPolicy.MinimumAmity,
            PetCareDecayPolicy.MaximumAmity);
        var lifetime = Clamp(
            pet.RemainingLifetime + definition.Lifetime,
            PetCareDecayPolicy.MinimumLifetime,
            PetCareDecayPolicy.MaximumLifetime);
        var energy = Clamp(
            pet.CurrentEnergy +
                PetCareItemPolicy.ResolveEnergyGrant(
                    definition,
                    pet.MaximumEnergy),
            0,
            pet.MaximumEnergy);

        // A food or wine fed to a pet that is already full is a no-op rather
        // than a free item sink, exactly like the merge-energy waters: the
        // client refuses them at 100.
        if (satiety == pet.Satiety &&
            amity == pet.Amity &&
            lifetime == pet.RemainingLifetime &&
            energy == pet.CurrentEnergy)
        {
            return new(
                PetDurableReceiptStatus.PetCareExhausted,
                KitBagSlot: bagSlot,
                PetId: pet.PetId,
                PetRevision: pet.Revision,
                IsCarried: true,
                IsSummoned: true);
        }

        var nextRevision = await UpdateSummonedPetCareAsync(
            connection,
            transaction,
            characterId,
            pet,
            satiety,
            amity,
            lifetime,
            energy,
            cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            characterId,
            bagSlot,
            item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);

        return new(
            PetDurableReceiptStatus.PetCareRestored,
            KitBagSlot: bagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetRevision: nextRevision,
            IsCarried: true,
            IsSummoned: true,
            InventoryMutations:
            [
                new InventoryMutation(
                    item.ItemId,
                    consumed.MutationKind,
                    item.BeforeState,
                    consumed.AfterState,
                    "pet_care_item_consumed",
                    inventoryRevision)
            ],
            CareRestore: new PetCareRestoreEvidence(
                pet.PetId,
                satiety,
                amity,
                lifetime,
                energy,
                pet.MaximumEnergy,
                nextRevision,
                definition.ItemId));
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Clamp(value, minimum, maximum);

    private async Task<LockedCareItemPet?> LockSummonedPetForCareItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, species_id, level, revision,
                satiety, amity, remaining_lifetime,
                current_energy, maximum_energy
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_summoned
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var pet = new LockedCareItemPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "A character has multiple summoned pets.");
        }
        if (pet.Satiety < PetCareDecayPolicy.MinimumSatiety ||
            pet.Satiety > PetCareDecayPolicy.MaximumSatiety ||
            pet.Amity < PetCareDecayPolicy.MinimumAmity ||
            pet.Amity > PetCareDecayPolicy.MaximumAmity ||
            pet.RemainingLifetime < PetCareDecayPolicy.MinimumLifetime ||
            pet.RemainingLifetime > PetCareDecayPolicy.MaximumLifetime ||
            pet.MaximumEnergy <= 0 ||
            pet.CurrentEnergy < 0 ||
            pet.CurrentEnergy > pet.MaximumEnergy)
        {
            throw new InvalidDataException(
                "The summoned pet has invalid care state.");
        }
        return pet;
    }

    private async Task<long> UpdateSummonedPetCareAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedCareItemPet pet,
        int satiety,
        int amity,
        int lifetime,
        int energy,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET satiety = @satiety,
                amity = @amity,
                remaining_lifetime = @lifetime,
                current_energy = @energy,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @expectedRevision
              AND activity_state = 'owned'
              AND is_summoned
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("satiety", satiety);
        command.Parameters.AddWithValue("amity", amity);
        command.Parameters.AddWithValue("lifetime", lifetime);
        command.Parameters.AddWithValue("energy", energy);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("expectedRevision", pet.Revision);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The summoned pet care revision was not advanced exactly once.");
    }

    private sealed record LockedCareItemPet(
        long PetId,
        short SpeciesId,
        short Level,
        long Revision,
        int Satiety,
        int Amity,
        int RemainingLifetime,
        int CurrentEnergy,
        int MaximumEnergy);
}
