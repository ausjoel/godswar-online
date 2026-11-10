using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task ReloadFactionCrierProjectionAsync(
        PlayerOwnershipFence ownership,
        FactionCrierExecutionReceipt receipt,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The Faction Crier owner changed during projection reload.");
        }

        var persistedSnapshot = accountSnapshot.Character;
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            accountSnapshot);
        if (persistedSnapshot is null ||
            hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable Faction Crier character could not be reloaded.");
        }

        ValidateFactionCrierProjection(persistedSnapshot, receipt);
        ApplyFactionCrierProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
    }

    internal static void ApplyFactionCrierProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId ||
            liveCharacter.RealmId != persistedCharacter.RealmId ||
            persistedCharacter.CalculatedStats is null)
        {
            throw new InvalidDataException(
                "A Faction Crier projection cannot change character " +
                "identity or omit calculated stats.");
        }

        var levelChanged = liveCharacter.Level != persistedCharacter.Level;
        liveCharacter.Level = persistedCharacter.Level;
        liveCharacter.Experience = persistedCharacter.Experience;
        liveCharacter.FighterLevelSealed =
            persistedCharacter.FighterLevelSealed;
        liveCharacter.TalentPoints = persistedCharacter.TalentPoints;
        liveCharacter.TalentExperience =
            persistedCharacter.TalentExperience;
        liveCharacter.Silver = persistedCharacter.Silver;
        liveCharacter.Gold = persistedCharacter.Gold;
        liveCharacter.BindingGold = persistedCharacter.BindingGold;
        liveCharacter.KitBag = persistedCharacter.KitBag;
        liveCharacter.FactionCrierRevision =
            persistedCharacter.FactionCrierRevision;

        if (levelChanged)
        {
            // A level-up changes derived maxima. Import the recalculated stats
            // while preserving newer live HP/MP instead of restoring checkpoint
            // vitals from the database snapshot. Pure EXP, talent, wallet, and
            // item exchanges must not discard newer runtime-only stat effects.
            ApplyDurableEquipmentStatsProjection(
                liveCharacter,
                persistedCharacter.CalculatedStats);
        }
    }

    internal static void ValidateFactionCrierProjection(
        Godswar.Server.Application.Characters.CharacterLoadSnapshot snapshot,
        FactionCrierExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(receipt);
        if (snapshot.Identity.CharacterId != receipt.CharacterId ||
            snapshot.Identity.RealmId.Value != receipt.RealmId ||
            snapshot.Identity.FactionCrierRevision <
                receipt.FactionCrierRevision ||
            snapshot.Loadout.InventoryRevision <
                receipt.InventoryRevision ||
            snapshot.Progression.Revision <
                receipt.ProgressionRevision)
        {
            throw new InvalidDataException(
                "The authoritative Faction Crier projection predates its " +
                "durable receipt.");
        }

        if (snapshot.Progression.Revision ==
                receipt.ProgressionRevision &&
            (snapshot.Progression.Level != receipt.CurrentLevel ||
             snapshot.Progression.Experience !=
                receipt.CurrentExperience ||
             snapshot.Progression.TalentPoints !=
                receipt.CurrentTalentPoints))
        {
            throw new InvalidDataException(
                "The committed Faction Crier progression is stale.");
        }
    }

    private void ValidateFactionCrierReceipt(
        FactionCrierOperation expectedOperation,
        int expectedSubId,
        FactionCrierExecutionReceipt receipt)
    {
        if (_character is null ||
            receipt.CharacterId != _character.Id ||
            receipt.RealmId != _processRealmId.Value ||
            receipt.Operation != expectedOperation ||
            receipt.SubId != expectedSubId ||
            receipt.NativeResultSubId <= 0 ||
            !receipt.Succeeded ||
            receipt.PreviousLevel is < 1 or >
                CharacterProgressionSnapshotRules.MaximumCharacterLevel ||
            receipt.CurrentLevel is < 1 or >
                CharacterProgressionSnapshotRules.MaximumCharacterLevel ||
            receipt.PreviousExperience is < 0 or > uint.MaxValue ||
            receipt.CurrentExperience is < 0 or > uint.MaxValue ||
            receipt.AwardedExperience < 0 ||
            receipt.PreviousTalentPoints < 0 ||
            receipt.CurrentTalentPoints < 0 ||
            receipt.AwardedTalentPoints < 0 ||
            receipt.Wallet.Silver < 0 ||
            receipt.Wallet.Gold < 0 ||
            receipt.Wallet.BindingGold < 0 ||
            receipt.WalletRevision < 0 ||
            receipt.InventoryRevision < 0 ||
            receipt.ProgressionRevision < 0 ||
            receipt.FactionCrierRevision <= 0 ||
            string.IsNullOrWhiteSpace(receipt.AuditId) ||
            receipt.EventId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The Faction Crier receipt identity is inconsistent.");
        }
    }

    private async Task SendFactionCrierDurableResultAsync(
        uint npcId,
        FactionCrierOperationIdentity identity,
        FactionCrierExecutionReceipt receipt,
        FactionCrierExecutionDisposition disposition,
        string bagBefore,
        CancellationToken cancellationToken)
    {
        var firstCommit = disposition ==
            FactionCrierExecutionDisposition.Committed;
        foreach (var acknowledgement in
            PacketBuilder.KitBagMutationDeletionAcknowledgements(
                bagBefore,
                _character!.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "FactionCrierKitBagDeleteAck");
        }

        if (firstCommit)
        {
            var acquisitions = GetFactionCrierNameplateAcquisitions(
                bagBefore,
                _character!.KitBag);
            var scratchSlot = acquisitions.Count == 0
                ? -1
                : GetFactionCrierAcquisitionScratchSlot(
                    bagBefore,
                    _character.KitBag);
            if (scratchSlot >= 0)
            {
                foreach (var acquisition in acquisitions)
                {
                    await _session.SendAsync(
                        PacketBuilder.SystemAddItemWithAcquisitionLog(
                            acquisition),
                        cancellationToken,
                        "FactionCrierNameplateAcquisition");
                    await _session.SendAsync(
                        PacketBuilder.StorageItemKitBagDelete(scratchSlot),
                        cancellationToken,
                        "FactionCrierNameplateAcquisitionCleanup");
                }
            }
        }

        foreach (var levelUp in firstCommit ? receipt.LevelUps : [])
        {
            var experienceMaximum =
                PlayerExperienceCatalog.GetClientExperienceMaximum(
                    levelUp.Level,
                    _character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    experienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                "FactionCrierLevelUp");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerLevelUp(
                    CurrentPlayerObjectId,
                    levelUp.Level,
                    experienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                _session,
                "FactionCrierLevelUpWorld");
        }

        if (firstCommit && receipt.AwardedExperience > 0)
        {
            await _session.SendAsync(
                PacketBuilder.ExperienceGain(
                    receipt.AwardedExperience,
                    _character.Experience),
                cancellationToken,
                "FactionCrierExperienceRefresh");
        }
        if (firstCommit && receipt.AwardedTalentPoints > 0)
        {
            await _session.SendAsync(
                PacketBuilder.TalentPointGain(
                    receipt.AwardedTalentPoints),
                cancellationToken,
                "FactionCrierTalentPointGain");
        }
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "FactionCrierWalletProgressionStatus");
        await SendKitBagRefreshAsync(cancellationToken);

        // Keep the native text visible: the stock dialog is replaced by a
        // result response, so inventory refreshes must finish first.
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                FactionCrierProtocol.DialogIndex,
                receipt.NativeResultSubId),
            cancellationToken,
            "FactionCrierResult");
        if (identity.IsSecureClient)
        {
            await SendSecureGearMentorResultAsync(
                identity.OperationId,
                CommandFamily.FactionCrier,
                receipt.NativeResultSubId,
                disposition == FactionCrierExecutionDisposition.Committed
                    ? SecureLegacyCommandDisposition.Applied
                    : SecureLegacyCommandDisposition.Replayed,
                receipt.FactionCrierRevision,
                cancellationToken);
        }
    }
}
