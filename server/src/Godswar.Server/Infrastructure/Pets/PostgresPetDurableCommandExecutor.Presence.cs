using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Commands;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetPresenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetPresenceTransitionCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        // Merge owns the active-pet selection for its entire lifetime. Lock
        // that owner first so Take/Call Out/Recall cannot race expiry or clear
        // its contribution rows through the ordinary presence path.
        var activeOwnerMergePetId = await LockActiveOwnerMergePetIdAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        var pet = await LockPetAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.PetId,
            cancellationToken);
        if (pet is null)
        {
            return new(
                PetDurableReceiptStatus.PetNotFound,
                PetId: envelope.Command.PetId,
                PresenceOperation:
                    checked((byte)((byte)envelope.Command.Operation + 1)));
        }
        if (!string.Equals(
                pet.ActivityState,
                "owned",
                StringComparison.Ordinal))
        {
            return FromPet(
                PetDurableReceiptStatus.PetUnavailable,
                pet,
                envelope.Command.Operation);
        }
        if (activeOwnerMergePetId.HasValue)
        {
            return FromPet(
                PetDurableReceiptStatus.PetUnavailable,
                pet,
                envelope.Command.Operation);
        }
        if (envelope.Command.Operation is
                PetPresenceCommandOperation.CallOut or
                PetPresenceCommandOperation.Recall &&
            !pet.IsCarried)
        {
            return FromPet(
                PetDurableReceiptStatus.PetNotTaken,
                pet,
                envelope.Command.Operation);
        }
        if (envelope.Command.Operation ==
                PetPresenceCommandOperation.CallOut &&
            !pet.CanBeSummoned)
        {
            return FromPet(
                PetDurableReceiptStatus.PetCareExhausted,
                pet,
                envelope.Command.Operation);
        }

        // A discard destroys the row instead of changing it, so it never reaches
        // the carried/summoned update below. The reference server answers a
        // successful discard with result code 3 and the pet is gone from the
        // next owned-pet bootstrap, which is what a physical delete gives.
        if (envelope.Command.Operation ==
            PetPresenceCommandOperation.Delete)
        {
            return await DeletePetAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                pet,
                cancellationToken);
        }

        var carried = pet.IsCarried;
        var summoned = pet.IsSummoned;
        if (envelope.Command.Operation ==
            PetPresenceCommandOperation.Take)
        {
            var isSwitchingCarriedPet = !pet.IsCarried;
            _ = await ClearOtherCarriedPetsAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                pet.PetId,
                cancellationToken);
            carried = true;
            // Native Take selects a different companion immediately. Keep an
            // already-carried pet's current presentation unchanged so a
            // repeated Take cannot create a duplicate summoned model. A pet
            // with no satiety or lifetime is still carried, never summoned.
            summoned = (isSwitchingCarriedPet || pet.IsSummoned) &&
                pet.CanBeSummoned;
        }
        else
        {
            summoned =
                envelope.Command.Operation ==
                    PetPresenceCommandOperation.CallOut;
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = @carried,
                is_summoned = @summoned,
                contributes_to_character =
                    CASE WHEN @summoned
                         THEN contributes_to_character
                         ELSE false
                    END,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
            RETURNING revision;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("carried", carried);
        update.Parameters.AddWithValue("summoned", summoned);
        update.Parameters.AddWithValue("petId", pet.PetId);
        update.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        update.Parameters.AddWithValue("revision", pet.Revision);
        var revision =
            await update.ExecuteScalarAsync(cancellationToken) as long? ??
            throw new InvalidDataException(
                "The locked pet changed during presence transition.");
        return new(
            PetDurableReceiptStatus.PresenceChanged,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: revision,
            IsCarried: carried,
            IsSummoned: summoned,
            PresenceOperation:
                checked((byte)((byte)envelope.Command.Operation + 1)));
    }

    /// <summary>
    /// Permanently destroys one owned pet and everything hanging off it.
    /// </summary>
    /// <remarks>
    /// The pet's own level, experience and stats live on <c>character_pets</c>,
    /// so clearing the kill ledger below loses no progression: that table only
    /// records which monster deaths were already settled, and a destroyed pet
    /// can never be settled again. Every other child table cascades on delete.
    /// <para>
    /// FINALISE: the carried pet is discarded too (the projection retires its
    /// model and passives afterwards). One state is still refused: a pet sealed
    /// into an item is still referenced by <c>sealed_pet_items</c>, whose
    /// foreign key restricts; the insert would fail anyway, so it is reported as
    /// unavailable instead of surfacing a constraint error.
    /// </para>
    /// </remarks>
    private async Task<PetTransition> DeletePetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedPet pet,
        CancellationToken cancellationToken)
    {
        // FINALISE (the admin 2026-11-05, "abandon pet disconnects"): the Pet window's
        // Abandon button (PetGiveupQuery -> 10238) is normally pressed on the pet the
        // player has out, which is the carried one. Refusing it answered code 4,
        // which the client's PetOperator table reads as "Failure to recall", and the
        // window was left drawing a pet it had already asked to give up. A carried
        // (and summoned) pet is now discarded like any other: the row goes, and the
        // projection retires the companion model and the summoned passives after.
        // A pet merged into its owner (contributes_to_character) is still refused: the
        // session's owner-merge energy and bonuses point at it. Answered "Failure to abandon".
        if (pet.ContributesToCharacter)
        {
            return FromPet(
                PetDurableReceiptStatus.PetUnavailable,
                pet,
                PetPresenceCommandOperation.Delete);
        }
        await using (var sealedCheck = CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.sealed_pet_items
                WHERE pet_id = @petId
            );
            """,
            connection,
            transaction))
        {
            sealedCheck.Parameters.AddWithValue("petId", pet.PetId);
            if (Convert.ToBoolean(
                    await sealedCheck.ExecuteScalarAsync(cancellationToken)))
            {
                return FromPet(
                    PetDurableReceiptStatus.PetUnavailable,
                    pet,
                    PetPresenceCommandOperation.Delete);
            }
        }

        await using (var clearLedger = CreateCommand(
            """
            DELETE FROM public.monster_death_pet_experience
            WHERE pet_id = @petId;
            """,
            connection,
            transaction))
        {
            clearLedger.Parameters.AddWithValue("petId", pet.PetId);
            await clearLedger.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deletePet = CreateCommand(
            """
            DELETE FROM public.character_pets
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision;
            """,
            connection,
            transaction))
        {
            deletePet.Parameters.AddWithValue("petId", pet.PetId);
            deletePet.Parameters.AddWithValue(
                "characterId",
                characterId);
            deletePet.Parameters.AddWithValue("revision", pet.Revision);
            var affected =
                await deletePet.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                throw new InvalidDataException(
                    "The locked pet changed during the discard.");
            }
        }

        return new(
            PetDurableReceiptStatus.PresenceChanged,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: false,
            IsSummoned: false,
            PresenceOperation: checked((byte)(
                (byte)PetPresenceCommandOperation.Delete + 1)));
    }

    private async Task<bool> ClearOtherCarriedPetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var presence = CreateCommand(
            """
            SELECT COALESCE(bool_or(is_summoned), false)
            FROM public.character_pets
            WHERE user_id = @characterId
              AND id <> @petId;
            """,
            connection,
            transaction);
        presence.Parameters.AddWithValue("characterId", characterId);
        presence.Parameters.AddWithValue("petId", petId);
        var hadSummonedPet = Convert.ToBoolean(
            await presence.ExecuteScalarAsync(cancellationToken));

        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND id <> @petId
              AND NOT contributes_to_character
              AND (is_carried OR is_summoned OR contributes_to_character);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return hadSummonedPet;
    }

    private async Task<long?> LockActiveOwnerMergePetIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id
            FROM public.character_pets
            WHERE user_id = @characterId
              AND contributes_to_character
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var ids = new List<long>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids.Count switch
        {
            0 => null,
            1 => ids[0],
            _ => throw new InvalidDataException(
                "A character has multiple active pet owner-Merge rows.")
        };
    }

    private async Task<LockedPet?> LockPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, level, experience, activity_state, revision,
                is_carried, is_summoned, initial_savvy_source_version,
                contributes_to_character, satiety, remaining_lifetime
            FROM public.character_pets
            WHERE id = @petId
              AND user_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedPet(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetBoolean(8),
                reader.GetInt32(9),
                reader.GetInt32(10))
            : null;
    }

    private static PetTransition FromPet(
        PetDurableReceiptStatus status,
        LockedPet pet,
        PetPresenceCommandOperation? operation = null) =>
        new(
            status,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: pet.IsCarried,
            IsSummoned: pet.IsSummoned,
            PresenceOperation: operation.HasValue
                ? checked((byte)((byte)operation.Value + 1))
                : (byte)0);

    private sealed record LockedPet(
        long PetId,
        short Level,
        long Experience,
        string ActivityState,
        long Revision,
        bool IsCarried,
        bool IsSummoned,
        string? InitialSavvySourceVersion,
        bool ContributesToCharacter,
        int Satiety,
        int RemainingLifetime)
    {
        /// <summary>
        /// A pet with no satiety or no lifetime left cannot be sent out. Take
        /// still carries it; only the summon is refused.
        /// </summary>
        public bool CanBeSummoned =>
            PetCareDecayPolicy.CanBeSummoned(Satiety, RemainingLifetime);
    }
}
