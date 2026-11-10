using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

/// <summary>W0 F-93: right-click a pet stat spell on the summoned pet (see <see cref="PetStatSpellPolicy"/>).</summary>
internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ApplyPetStatSpellAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        PetStatSpellPolicy.Spell spell,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.Stack < 1 || item.PropId != (int)spell.ItemId)
        {
            return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        }

        long petId; decimal rank; long revision; short level; long experience;
        await using (var lockPet = CreateCommand(
            """
            SELECT id, rank, revision, level, experience
            FROM public.character_pets
            WHERE user_id = @characterId AND activity_state = 'owned' AND is_carried AND is_summoned
            FOR UPDATE;
            """, connection, transaction))
        {
            lockPet.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await lockPet.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new(PetDurableReceiptStatus.PetNotTaken, KitBagSlot: bagSlot);
            }
            petId = reader.GetInt64(0); rank = reader.GetDecimal(1); revision = reader.GetInt64(2);
            level = reader.GetInt16(3); experience = reader.GetInt64(4);
        }

        var gain = PetStatSpellPolicy.RollGain(spell);
        var rankAfter = rank;
        if (spell.StatCode == 0)
        {
            rankAfter = Math.Min(rank + gain, _petContent.Settings.MaximumRank);
            if (rankAfter <= rank)
            {
                return new(PetDurableReceiptStatus.PetCareExhausted, KitBagSlot: bagSlot, PetId: petId,
                    PetRevision: revision, IsCarried: true, IsSummoned: true);
            }
        }
        else
        {
            await using var updateStat = CreateCommand(
                """
                UPDATE public.character_pet_stat_values
                SET initial_savvy = initial_savvy + @gain, revision = revision + 1
                WHERE pet_id = @petId AND stat_code = @statCode;
                """, connection, transaction);
            updateStat.Parameters.AddWithValue("gain", gain);
            updateStat.Parameters.AddWithValue("petId", petId);
            updateStat.Parameters.AddWithValue("statCode", spell.StatCode);
            if (await updateStat.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException($"Pet stat spell: stat {spell.StatCode} of pet {petId} is missing.");
            }
        }

        long nextRevision;
        await using (var updatePet = CreateCommand(
            """
            UPDATE public.character_pets
            SET rank = @rankAfter, revision = revision + 1, updated_at = transaction_timestamp()
            WHERE id = @petId AND user_id = @characterId AND revision = @revision
            RETURNING revision;
            """, connection, transaction))
        {
            updatePet.Parameters.AddWithValue("rankAfter", rankAfter);
            updatePet.Parameters.AddWithValue("petId", petId);
            updatePet.Parameters.AddWithValue("characterId", characterId);
            updatePet.Parameters.AddWithValue("revision", revision);
            nextRevision = (long)(await updatePet.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidDataException("Pet stat spell: the pet revision did not advance."));
        }

        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction, characterId,
            character.InventoryRevision, cancellationToken);
        Console.WriteLine(
            $"[pet-stat-spell] character={characterId} pet={petId} item={spell.ItemId} stat={spell.StatCode} " +
            $"gain={gain} rank={rank}->{rankAfter}");

        return new(
            PetDurableReceiptStatus.PetStatSpellApplied,
            KitBagSlot: bagSlot,
            PetId: petId,
            PetLevel: level,
            PetExperience: experience,
            PetRevision: nextRevision,
            IsCarried: true,
            IsSummoned: true,
            InventoryMutations:
            [
                new InventoryMutation(item.ItemId, consumed.MutationKind, item.BeforeState, consumed.AfterState,
                    "pet_stat_spell", inventoryRevision)
            ]);
    }
}
