using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> ReloadPetProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (IsPotionProjection(receipt))
            return await ReloadPotionProjectionAsync(receipt, disposition, cancellationToken);
        var previousKitBag = _character?.KitBag;
        var previousCarriedPet = _characterLoadSnapshot?.Pets
            .SingleOrDefault(static pet => pet.IsCarried);
        var previousVitals = CapturePetManagerUtilityVitals(_character);
        if (!await RefreshCharacterSnapshotAsync(
                "durable_pet_command",
                cancellationToken) ||
            _account is null ||
            _character is null)
        {
            return false;
        }

        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        var pets = _characterLoadSnapshot?.Pets ?? [];
        _registry.UpdateSummonedPet(
            _session,
            pets.SingleOrDefault(
                static pet => pet.IsCarried &&
                    pet.IsSummoned &&
                    !pet.ContributesToCharacter));

        // Opcode 10237 is not an inert detail refresh: the native client
        // rebuilds its active-pet selection from it and immediately emits a
        // Recall while the pet-unite transition is being processed. Owner
        // Merge therefore has a dedicated lifecycle projection and must
        // never pass through the complete owned-pet-list branch, on either a
        // successful transition or a rejected repeated request.
        if ((receipt.Family is CommandFamily.BagItemActivation or
                CommandFamily.PetOwnerMergeToggle) &&
            IsOwnerMergeReceipt(receipt.Status))
        {
            if (!receipt.Succeeded)
            {
                await SendPetOwnerMergeRejectionProjectionAsync(
                    receipt,
                    pets,
                    cancellationToken);
                return true;
            }

            var receiptPet = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            var activeMerges = pets
                .Where(static pet => pet.ContributesToCharacter)
                .Take(2)
                .ToArray();
            if (activeMerges.Length > 1)
            {
                return false;
            }

            if (disposition == PetDurableExecutionDisposition.Committed)
            {
                if (receiptPet is null)
                {
                    return false;
                }
                var committedMerge = receipt.Status ==
                    PetDurableReceiptStatus.OwnerMerged;
                if (committedMerge != (activeMerges.Length == 1) ||
                    committedMerge &&
                        activeMerges[0].PetId != receipt.PetId)
                {
                    return false;
                }
            }

            // A duplicate receipt describes a historical commit. Project the
            // snapshot's one current contributor, not the historical receipt
            // pet. A delayed receipt for pet A must not tear down a newer
            // Merge owned by pet B.
            if (activeMerges.Length == 1)
            {
                var activePet = activeMerges[0];
                if (!activePet.IsCarried || !activePet.IsSummoned)
                {
                    return false;
                }
                await PublishPetOwnerMergeStartedAsync(
                    activePet,
                    cancellationToken);
                // The committed Merge spends amity, and the unite
                // presentation never carries that field: publish the narrow
                // care frame so the pet panel matches durable state.
                await SendPetCareStateAsync(
                    activePet.PetId,
                    activePet.Satiety,
                    activePet.Amity,
                    activePet.RemainingLifetime,
                    "PetOwnerMergeCareState",
                    cancellationToken);
            }
            else
            {
                if (receiptPet is null)
                {
                    return false;
                }
                CancelPetOwnerMergeEnergyDrain();
                await PublishPetOwnerMergeEndedAsync(
                    receiptPet,
                    restoreCompanion: true,
                    cancellationToken);
            }
            return true;
        }

        if (receipt.Family == CommandFamily.PetToPetMerge)
        {
            return await SendPetToPetMergeProjectionAsync(
                receipt,
                disposition,
                pets,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetRebirth)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetRebirthProjectionAsync(
                receipt,
                disposition,
                pets,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetSoulContract)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetSoulContractProjectionAsync(
                receipt,
                disposition,
                pets,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetAppearanceChange)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetAppearanceChangeProjectionAsync(
                receipt,
                disposition,
                pets,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetBind)
        {
            return await SendPetBindProjectionAsync(
                receipt,
                disposition,
                pets,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetManagerUtility)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetManagerUtilityProjectionAsync(
                receipt,
                disposition,
                pets,
                previousCarriedPet,
                previousVitals,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.BagItemActivation)
        {
            if (IsPotionProjection(receipt))
                return previousKitBag is not null && await SendPotionProjectionAsync(
                    receipt, disposition, previousKitBag, cancellationToken);
            if (IsProgressItemProjection(receipt))
                return previousKitBag is not null && await SendProgressItemProjectionAsync(
                    receipt, disposition, previousKitBag, cancellationToken);
            if (IsGiftPackProjection(receipt))
                return previousKitBag is not null && await SendGiftPackProjectionAsync(
                    receipt, disposition, previousKitBag, cancellationToken);
            if (IsPlayerExperienceItemProjection(receipt))
                return await SendPlayerExperienceItemProjectionAsync(receipt, disposition, cancellationToken);
            if (IsMoneyBagProjection(receipt))
                return previousKitBag is not null && await SendMoneyBagProjectionAsync(
                    receipt, disposition, previousKitBag, cancellationToken);
            // A Wonderland sack owns its whole projection: the native
            // acquisition notice for the drawn reward plus the exact bag
            // cleanup. The generic slot clear and owned-pet rebuild below must
            // not also answer it.
            if (IsWonderlandSackProjection(receipt))
            {
                return previousKitBag is not null &&
                    await SendWonderlandSackProjectionAsync(
                        receipt,
                        disposition,
                        previousKitBag,
                        cancellationToken);
            }
            if (IsPlayerSkillBookReceipt(receipt.Status))
            {
                return await SendPlayerSkillBookProjectionAsync(
                    receipt,
                    disposition,
                    cancellationToken);
            }
            if (receipt.Status is
                    PetDurableReceiptStatus.PetShedExpanded or
                    PetDurableReceiptStatus.PetShedMaximumReached)
            {
                await _session.SendAsync(
                    PacketBuilder.PetShedExpansionResult(
                        receipt.Status ==
                            PetDurableReceiptStatus.PetShedExpanded
                            ? PetShedExpansionResultCode.Succeeded
                            : PetShedExpansionResultCode.AlreadyMaximum),
                    cancellationToken,
                    "DurablePetShedExpansionResult");
            }

            // A detail refresh cannot evict a now-empty item object. Clear
            // only an authoritatively empty committed slot, preserving the
            // stock cooling overlay for remaining stacks.
            if (receipt.Succeeded &&
                receipt.KitBagSlot >= 0 &&
                KitBagSlots.GetItem(
                    _character.KitBag,
                    receipt.KitBagSlot).IsEmpty)
            {
                await _session.SendAsync(
                    PacketBuilder.StorageItemKitBagDelete(
                        receipt.KitBagSlot),
                    cancellationToken,
                    "DurablePetBagActivationSlotClear");
            }
            await SendKitBagRefreshAsync(cancellationToken);
            if (receipt.Status ==
                PetDurableReceiptStatus.PetExperienceBoostActivated)
            {
                // A pet-experience potion is a bag item, not a skill: it changes
                // neither the pet nor any derived stat, and it casts nothing.
                // The slot clear and kit-bag refresh above already answer the
                // use; the only remaining work is to publish the recomposed
                // status snapshot so the granted status and its countdown reach
                // the status bar, instead of the pet-list fallback.
                await SendExperienceBoostStatusAsync(
                    "pet-experience-boost-potion",
                    cancellationToken);
            }
            if (receipt.EquipmentSlot >= 0)
            {
                var equipment = PacketBuilder.EquipmentItemSnapshot(
                    _character,
                    receipt.EquipmentSlot);
                if (equipment.Length == 0)
                {
                    equipment = PacketBuilder
                        .EquipmentItemClearSnapshot(
                            receipt.EquipmentSlot);
                }
                await _session.SendAsync(
                    equipment,
                    cancellationToken,
                    "DurablePetEquipmentRefresh");
                await _session.SendAsync(
                    PacketBuilder.EquipmentVisualRefresh(
                        _character,
                        _itemContent?.FashionAppearances),
                    cancellationToken,
                    "DurablePetEquipmentVisualRefresh");
                await _session.SendAsync(
                    PacketBuilder.EquipmentEffectVisibility(
                        LocalPlayerObjectId,
                        ResolveEquipmentEffectProjection(_character)),
                    cancellationToken,
                    "DurablePetEquipmentEffectVisibility");
                await BroadcastEquipmentRefreshAsync(
                    "durable_bag_activation",
                    cancellationToken);
            }
            PetBootstrapSnapshot? hatched = null;
            PetBootstrapSnapshot? skillCellPet = null;
            PetBootstrapSnapshot? experiencePet = null;
            if (receipt.Status == PetDurableReceiptStatus.EggHatched)
            {
                hatched = pets.SingleOrDefault(
                    candidate => candidate.PetId == receipt.PetId);
                if (hatched is null ||
                    !hatched.IsCarried ||
                    hatched.IsSummoned != receipt.IsSummoned)
                {
                    return false;
                }

                if (previousCarriedPet is
                        { IsSummoned: true } previous &&
                    previous.PetId != hatched.PetId)
                {
                    await _session.SendAsync(
                        PacketBuilder.PetOperationResult(
                            checked((uint)previous.PetId),
                            PetOperationResultCode.RecallSucceeded),
                        cancellationToken,
                        "DurablePetHatchPreviousRecall");
                }
            }
            else if (receipt.Status is
                         PetDurableReceiptStatus
                             .PetSkillCellMadeAvailable or
                         PetDurableReceiptStatus.PetSkillCellOpened or
                         PetDurableReceiptStatus.PetSkillLearned or
                         PetDurableReceiptStatus.PetSkillUnlearned) // W0 F-55 Weak Purge
            {
                skillCellPet = pets.SingleOrDefault(
                    candidate => candidate.PetId == receipt.PetId);
                if (skillCellPet is null ||
                    skillCellPet.Revision != receipt.PetRevision)
                {
                    return false;
                }
            }
            else if (receipt.Status ==
                     PetDurableReceiptStatus.PetExperienceAdded)
            {
                experiencePet = pets.SingleOrDefault(
                    candidate => candidate.PetId == receipt.PetId);
                if (experiencePet is null ||
                    !experiencePet.IsCarried ||
                    experiencePet.Experience != receipt.PetExperience ||
                    experiencePet.Revision != receipt.PetRevision)
                {
                    return false;
                }
            }

            // Native 10237 rebuilds active-pet selection. Skill-cell items
            // instead use 10247 and must preserve carry/summon presentation.
            if (skillCellPet is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.PetSkillState(skillCellPet),
                    cancellationToken,
                    "DurablePetSkillStateRefresh");
                if (receipt.Status is
                        PetDurableReceiptStatus.PetSkillLearned or
                        PetDurableReceiptStatus.PetSkillUnlearned &&
                    !await SendPetSkillOwnerStatRefreshAsync(
                        "DurablePetSkillLearned",
                        cancellationToken))
                {
                    return false;
                }
            }
            else if (experiencePet is not null)
            {
                // Opcode 10237 rebuilds native carry/summon state and can
                // auto-recall an actively merged pet. Morning Dew has its
                // own narrow original-server EXP projection.
                await _session.SendAsync(
                    PacketBuilder.PetExperience(
                        experiencePet.PetId,
                        experiencePet.Experience),
                    cancellationToken,
                    "DurablePetExperienceRefresh");
            }
            else if (receipt is
                     {
                         Status: PetDurableReceiptStatus.PetCareRestored,
                         CareRestore: { IsValid: true } care
                     })
            {
                // Care consumables have their own narrow originals: opcode
                // 10245 carries satiety/amity/lifetime and 10278 carries the
                // merge energy. Rebuilding the owned-pet list here would
                // reset the client's active-pet selection for no reason.
                await _session.SendAsync(
                    PacketBuilder.PetCareState(
                        care.PetId,
                        care.Satiety,
                        care.Amity,
                        care.RemainingLifetime),
                    cancellationToken,
                    "DurablePetCareItemState");
                await _session.SendAsync(
                    PacketBuilder.PetEnergy(
                        care.CurrentEnergy,
                        care.MaximumEnergy),
                    cancellationToken,
                    "DurablePetCareItemEnergy");
            }
            else if (receipt.Status == PetDurableReceiptStatus.PetStatSpellApplied)
            {
                // W0 F-93: the narrow level-up frame carries the new Basic Savvy. Rank only travels in the
                // owned-pet list (10237), which can auto-Recall an owner-merged pet, so it is sent only while no
                // owner Merge is active (the PetToPetMerge rule); otherwise the client shows the new rank after
                // the next relog. The database is authoritative either way.
                var spellPet = pets.SingleOrDefault(candidate => candidate.PetId == receipt.PetId);
                if (spellPet is null || spellPet.Revision != receipt.PetRevision ||
                    !await SendPetProgressionRefreshAsync(spellPet, "DurablePetStatSpell", cancellationToken))
                {
                    return false;
                }
                if (!pets.Any(static pet => pet.ContributesToCharacter))
                {
                    await _session.SendAsync(
                        PacketBuilder.OwnedPetList(
                            RequirePetContent(),
                            pets,
                            _characterLoadSnapshot?.PetShed.OpenedCellCount ??
                                PetShedCapacityPolicy.DefaultOpenedCellCount),
                        cancellationToken,
                        "DurablePetStatSpellList");
                }
                if (!await SendPetSkillOwnerStatRefreshAsync("DurablePetStatSpell", cancellationToken))
                {
                    return false;
                }
            }
            else if (IsRejectedPetSkillBook(receipt.Status))
            {
                // A refused skill book changes nothing: the item is still in
                // the bag and the pet keeps its exact skills and carry/summon
                // presentation. Rebuilding the full pet list would disturb
                // active-pet selection for no reason, so the kit-bag refresh
                // above is the complete answer.
            }
            else
            {
                await _session.SendAsync(
                    PacketBuilder.OwnedPetList(
                        RequirePetContent(),
                        pets,
                        _characterLoadSnapshot?.PetShed.OpenedCellCount ??
                            PetShedCapacityPolicy.DefaultOpenedCellCount),
                    cancellationToken,
                    "DurablePetListRefresh");
            }
            if (hatched is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        checked((uint)hatched.PetId),
                        PetOperationResultCode.TakeSucceeded),
                    cancellationToken,
                    "DurablePetHatchTake");
                if (hatched.IsSummoned)
                {
                    await _session.SendAsync(
                        PacketBuilder.PetOperationResult(
                            checked((uint)hatched.PetId),
                            PetOperationResultCode.CallOutSucceeded),
                        cancellationToken,
                        "DurablePetHatchCallOut");
                }
                if (previousCarriedPet?.PetId != hatched.PetId &&
                    !await SendPetSkillOwnerStatRefreshAsync(
                        "DurablePetHatchCarriedSkillSource",
                        cancellationToken))
                {
                    return false;
                }
            }
        }
        else if (receipt.Family == CommandFamily.PetSkillUnlearn &&
                 receipt.Succeeded)
        {
            return await SendPetSkillUnlearnProjectionAsync(
                receipt,
                pets,
                cancellationToken);
        }
        else if (receipt.Family == CommandFamily.PetGrowthReset &&
                 receipt.Succeeded)
        {
            return await SendPetGrowthResetProjectionAsync(
                receipt,
                pets,
                cancellationToken);
        }
        else if (receipt.Family == CommandFamily.PetBasicSavvyReset &&
                 receipt.Succeeded)
        {
            return await SendPetBasicSavvyResetProjectionAsync(
                receipt,
                pets,
                cancellationToken);
        }
        else if (receipt.Family == CommandFamily.PetLevelUpgrade &&
                 receipt.Succeeded)
        {
            var pet = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            if (pet is null)
            {
                return false;
            }
            return await SendPetProgressionRefreshAsync(
                pet,
                "DurablePetLevelUpgrade",
                cancellationToken);
        }
        else if (receipt.Family ==
                 CommandFamily.PetPresenceTransition)
        {
            var target = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            var result = ResolveAuthoritativePresenceResult(
                receipt,
                target is not null,
                target?.IsCarried == true,
                target?.IsSummoned == true);
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    checked((uint)receipt.PetId),
                    result),
                cancellationToken,
                "DurablePetPresenceResult");

            // A discard has no post-state to reconcile: the pet is gone from the
            // snapshot this projection already reloaded, so the ladder below
            // would read "not carried, not summoned" and answer a Recall. Answer
            // the discard, then republish the owned-pet list so the shed drops
            // the slot the client is still drawing.
            if (receipt.PresenceOperation ==
                checked((byte)(
                    (byte)PetPresenceCommandOperation.Delete + 1)))
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        checked((uint)receipt.PetId),
                        result),
                    cancellationToken,
                    "DurablePetDeleteResult");
                if (receipt.Succeeded)
                {
                    await PublishOwnedPetUtilityListAsync(
                        pets,
                        cancellationToken,
                        "DurablePetDeleteOwnedList");
                    // FINALISE: abandoning the carried pet. Retire its companion
                    // model for everyone and republish the owner's stats so the
                    // summoned passives it supplied are gone too.
                    if (previousCarriedPet?.PetId == receipt.PetId)
                    {
                        await BroadcastPetWorldPresenceTransitionAsync(
                            previousCarriedPet,
                            pets.SingleOrDefault(static pet => pet.IsCarried),
                            cancellationToken);
                        if (!await SendPetSkillOwnerStatRefreshAsync(
                                "DurablePetDeleteCarriedSkillSource",
                                cancellationToken))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            // Take atomically selects and summons. Preserve the native Take,
            // then CallOut order so the old model is disposed before spawn.
            if (receipt.Succeeded &&
                receipt.PresenceOperation ==
                    checked((byte)(
                        (byte)PetPresenceCommandOperation.Take + 1)) &&
                result == PetOperationResultCode.TakeSucceeded &&
                target is { IsCarried: true, IsSummoned: true } &&
                previousCarriedPet?.PetId != target.PetId)
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        checked((uint)target.PetId),
                        PetOperationResultCode.CallOutSucceeded),
                    cancellationToken,
                    "DurablePetTakeAutoCallOut");
            }
            if (receipt.Succeeded &&
                receipt.PresenceOperation ==
                    checked((byte)(
                        (byte)PetPresenceCommandOperation.Take + 1)) &&
                pets.SingleOrDefault(static pet => pet.IsCarried) is
                    { } carried &&
                previousCarriedPet?.PetId != carried.PetId &&
                !await SendPetSkillOwnerStatRefreshAsync(
                    "DurablePetTakeCarriedSkillSource",
                    cancellationToken))
            {
                return false;
            }
            if (receipt.Succeeded)
            {
                await BroadcastPetWorldPresenceTransitionAsync(
                    previousCarriedPet,
                    pets.SingleOrDefault(static pet => pet.IsCarried),
                    cancellationToken);
            }
            if (receipt.Succeeded &&
                target is { ContributesToCharacter: false })
            {
                StartPetOwnerMergeEnergyRecharge();
            }
        }

        return true;
    }

    /// <summary>
    /// Terminal skill-book rejections leave the pet untouched. They are kept
    /// away from the owned-pet list rebuild, which would otherwise reset the
    /// client's active-pet selection.
    /// </summary>
    private static bool IsRejectedPetSkillBook(
        PetDurableReceiptStatus status) =>
        status is
            PetDurableReceiptStatus.PetSkillBookWrongSpecies or
            PetDurableReceiptStatus.PetSkillBookAlreadyLearned or
            PetDurableReceiptStatus.PetSkillBookPriorTierRequired or
            PetDurableReceiptStatus.PetSkillBookTraitRequirementNotMet or
            PetDurableReceiptStatus.PetSkillBookNoOpenSlot or
            PetDurableReceiptStatus.PetSkillBookInvalidState;

    private static bool IsOwnerMergeReceipt(
        PetDurableReceiptStatus status) =>
        status is >= PetDurableReceiptStatus.OwnerMerged and
            <= PetDurableReceiptStatus.OwnerMergeCharmInvalid;
}
