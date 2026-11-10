using Godswar.Server.Application.Finalise2;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// ITEMS-HERMES (F-55): pet rename (C2S 10242) with the Renaming Stone. Protocol and rules: State/PetRenamePolicy.cs.
/// One guarded transaction (the character row is locked by the events store): spend the stone when this pet has
/// had its free rename, rename the pet only if it belongs to this character, and count the rename.
/// </summary>
internal sealed partial class GameClientHandler
{
    private enum PetRenameOutcome { Renamed, NeedsStone, NotOwned, Unavailable }

    private async Task HandlePetRenameRequestAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }

        if (!PetRenamePolicy.TryParseRequest(packet.Buffer, out var petId, out var rawName))
        {
            Console.WriteLine($"[pet-rename] malformed request character={_character.Name} length={packet.Buffer.Length}");
            return;
        }

        if (!Finalise2Rates.On(Finalise2RateKeys.PetRename))
        {
            Console.WriteLine($"[pet-rename] switched off (item.pet_rename=0) character={_character.Name} pet={petId}");
            return;
        }

        var pet = _characterLoadSnapshot?.Pets.SingleOrDefault(candidate => candidate.PetId == petId);
        if (pet is null)
        {
            Console.WriteLine($"[pet-rename] pet not owned character={_character.Name} pet={petId}");
            return;
        }

        if (!PetRenamePolicy.TryValidateName(rawName, out var name))
        {
            Console.WriteLine($"[pet-rename] name refused character={_character.Name} pet={petId} " +
                $"bytes={Convert.ToHexString(rawName)}");
            await SendPetRenameResultAsync(petId, PetOperationResultCode.RenameFailedInvalidName, cancellationToken);
            return;
        }

        var stoneSpent = false;
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            var renames = await tx.ScalarAsync(
                "SELECT renames FROM public.pet_rename_state WHERE pet_id = @p FOR UPDATE;", ("p", (long)petId));
            if (renames is not null)
            {
                if (!await tx.ConsumeAsync(PetRenamePolicy.RenamingStone, 1))
                {
                    tx.Rollback();
                    return PetRenameOutcome.NeedsStone;
                }
                stoneSpent = true;
            }

            var updated = await tx.ScalarAsync("""
                UPDATE public.character_pets SET name = @n, revision = revision + 1, updated_at = now()
                WHERE id = @p AND user_id = @c RETURNING id;
                """, ("n", name), ("p", (long)petId));
            if (updated is null)
            {
                tx.Rollback();
                return PetRenameOutcome.NotOwned;
            }

            await tx.ScalarAsync("""
                INSERT INTO public.pet_rename_state (pet_id, character_id) VALUES (@p, @c)
                ON CONFLICT (pet_id) DO UPDATE SET renames = public.pet_rename_state.renames + 1,
                    character_id = EXCLUDED.character_id, renamed_at = now();
                """, ("p", (long)petId));
            return PetRenameOutcome.Renamed;
        }, PetRenameOutcome.Unavailable, cancellationToken);
        Console.WriteLine($"[pet-rename] character={_character.Name} pet={petId} from={pet.Name} to={name} " +
            $"outcome={outcome} stone={stoneSpent}");

        switch (outcome)
        {
            case PetRenameOutcome.NeedsStone:
                // "Failure to rename! The pet has already been renamed!": the free rename is used, no stone in the bag.
                await SendPetRenameResultAsync(petId, PetOperationResultCode.RenameFailedAlreadyRenamed, cancellationToken);
                return;
            case PetRenameOutcome.NotOwned:
            case PetRenameOutcome.Unavailable:
                return;
        }

        if (stoneSpent)
        {
            await RefreshAfterEventsNpcAsync([], cancellationToken);
        }

        if (!await RefreshCharacterSnapshotAsync("pet_rename", cancellationToken) || _character is null)
        {
            return;
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        var pets = _characterLoadSnapshot?.Pets ?? [];
        var summoned = pets.SingleOrDefault(static candidate => candidate.IsCarried &&
            candidate.IsSummoned && !candidate.ContributesToCharacter);
        _registry.UpdateSummonedPet(_session, summoned);

        // F-156 (the admin): no code 9, which opens the client's "You are called [name]" box. With no pet summoned and no
        // owner Merge, the owned-pet list (10237) renames the bean silently; otherwise 10237 would make the client
        // Recall the summoned pet (0x69c950 -> 0x69c3c0), so the owner sees the new name after a relog.
        if (pets.Any(static candidate => candidate.IsSummoned || candidate.ContributesToCharacter))
        {
            Console.WriteLine($"[pet-rename] owner list not resent (a pet is summoned or merged) pet={petId}");
        }
        else
        {
            await PublishOwnedPetUtilityListAsync(pets, cancellationToken, "PetRenameOwnedList");
        }

        if (summoned is not null && summoned.PetId == petId && !_registry.IsPetOwnerMergePresentationActive(_session))
        {
            // Other players: rebuild the owner's presentation (it carries no companion), then re-attach the pet
            // with its new name (0x2808 carries the name at +32), as the pet swap path does.
            await BroadcastEquipmentRefreshAsync("pet-rename", cancellationToken);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PetWorldPresence(summoned, CurrentPlayerObjectId),
                cancellationToken,
                _session,
                "PetRenameWorldPresence");
        }
    }

    private Task SendPetRenameResultAsync(uint petId, PetOperationResultCode code, CancellationToken cancellationToken) =>
        _session.SendAsync(PacketBuilder.PetOperationResult(petId, code), cancellationToken, "PetRenameResult");
}
