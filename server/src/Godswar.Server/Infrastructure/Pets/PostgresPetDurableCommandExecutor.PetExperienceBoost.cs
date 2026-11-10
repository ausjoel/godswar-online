using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    /// <summary>
    /// Applies one reviewed pet-experience potion on the pet-only channel.
    /// </summary>
    private async Task<PetTransition> ApplyPetExperienceBoostPotionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        PetExperienceBoostDefinition definition,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.Stack < 1 || item.PropId != definition.ItemId)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }

        var active = await ReadActiveGrantAsync(
            connection,
            transaction,
            characterId,
            PetExperienceBoostPolicy.Kind,
            "More than one active pet-experience boost is authoritative.",
            cancellationToken);
        var grant = PetExperienceBoostPolicy.ResolveGrant(
            active.BonusBasisPoints,
            active.OnlineTicks,
            definition);
        return await ApplyExperienceBoostGrantAsync(
            connection,
            transaction,
            characterId,
            bagSlot,
            item,
            definition.ItemId,
            PetExperienceBoostPolicy.Kind,
            "pet-exp-potion:",
            grant,
            "The pet-experience boost row was not written exactly once.",
            "pet_experience_boost_potion_consumed",
            character,
            cancellationToken);
    }
}
