using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    /// <summary>
    /// Applies one reviewed experience potion. Both the short 60-minute grants
    /// and the longer grants land on the family's own channel, so the tier rule
    /// sees one another and the fighter and pet stacks both pick the row up.
    /// </summary>
    private async Task<PetTransition> ApplyExperienceBoostPotionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        ExperienceBoostPotionDefinition definition,
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
            ExperienceBoostPotionPolicy.Kind,
            "More than one active experience-potion grant is authoritative.",
            cancellationToken);
        var grant = ExperienceBoostPotionPolicy.ResolveGrant(
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
            ExperienceBoostPotionPolicy.Kind,
            "enduring-exp-potion:",
            grant,
            "The experience-potion row was not written exactly once.",
            "experience_boost_potion_consumed",
            character,
            cancellationToken);
    }
}
