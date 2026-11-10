using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ApplyStatusExperienceBoostAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, int bagSlot, LockedBagItem item,
        StatusExperienceBoostDefinition definition, LockedCharacter character, CancellationToken token)
    {
        if (item.Stack < 1 || item.PropId != definition.ItemId)
            return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        var active = await ReadActiveGrantAsync(connection, transaction, characterId, definition.Kind,
            "More than one active experience boost is authoritative.", token);
        var grant = StatusExperienceBoostPolicy.ResolveGrant(active.BonusBasisPoints, active.OnlineTicks, definition);
        return await ApplyExperienceBoostGrantAsync(connection, transaction, characterId, bagSlot, item,
            definition.ItemId, definition.Kind, "status-exp-potion:", grant,
            "The experience boost row was not written exactly once.", "status_experience_boost_consumed",
            character, token);
    }
}
