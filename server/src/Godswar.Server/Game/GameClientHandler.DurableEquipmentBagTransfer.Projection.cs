using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task
        ReloadDurableEquipmentBagTransferProjectionAsync(
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken,
            EquipmentBagTransferExecutionReceipt?
                committedReceipt = null)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The equipment owner changed during projection reload.");
        }

        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable equipment/bag transfer character could not " +
                "be reloaded.");
        }
        if (committedReceipt is not null)
        {
            ValidateCommittedEquipmentBagTransferProjection(
                hydrated.Character,
                committedReceipt);
        }

        ApplyDurableEquipmentBagTransferProjection(
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

    internal static void ApplyDurableEquipmentBagTransferProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "An equipment/bag transfer projection cannot change " +
                "character identity.");
        }
        if (persistedCharacter.CalculatedStats is null)
        {
            throw new InvalidDataException(
                "An equipment/bag transfer projection requires " +
                "calculated stats.");
        }

        var fashionHidden =
            ResolveFashionHiddenAfterEquipmentChange(
                liveCharacter,
                persistedCharacter);
        liveCharacter.Equipment = persistedCharacter.Equipment;
        liveCharacter.FashionHidden = fashionHidden;
        liveCharacter.KitBag = persistedCharacter.KitBag;
        liveCharacter.HolySuitPoints =
            persistedCharacter.HolySuitPoints;
        ApplyDurableEquipmentStatsProjection(
            liveCharacter,
            persistedCharacter.CalculatedStats);
    }

    private static void ApplyDurableEquipmentStatsProjection(
        GameCharacter liveCharacter,
        CharacterStats persistedStats)
    {
        CharacterCalculatedStatsProjectionApplier.Apply(
            liveCharacter,
            persistedStats,
            CharacterHealthProjectionMode.PreserveAbsolute);
    }
}
