using System.Text.Json;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private Task<PetTransition> OpenWonderlandSackAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, int bagSlot, LockedBagItem sack,
        LockedCharacter character, CancellationToken token) =>
        ExecuteWithBagConsumableCooldownAsync(connection, transaction, characterId, bagSlot, sack,
            ct => OpenWonderlandSackCoreAsync(connection, transaction, characterId, bagSlot, sack, character, ct), token);

    private async Task<PetTransition> OpenWonderlandSackCoreAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, int bagSlot, LockedBagItem sack,
        LockedCharacter character, CancellationToken token)
    {
        var sackId = checked((uint)sack.PropId);
        if (sack.Stack <= 0 || !_itemContent.Templates.TryGet(sackId, out var template))
            return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        using (var stats = JsonDocument.Parse(template.StatsJson))
            if (!HasNativeInteger(stats.RootElement, "Use", 1) ||
                !HasNativeInteger(stats.RootElement, "Skill", checked((int)sackId - 4450 + 5400)))
                return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);

        var outcomes = WonderlandSackRewardPolicy.Outcomes(sackId);
        var binding = new Dictionary<uint, short>();
        foreach (var outcome in outcomes)
        {
            if (!_itemContent.Templates.TryGet(outcome.ItemId, out var rewardTemplate))
                return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
            using var stats = JsonDocument.Parse(rewardTemplate.StatsJson);
            if (!HasNativeInteger(stats.RootElement, "Overlap", 99))
                return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
            binding[outcome.ItemId] = sack.Bound || stats.RootElement.TryGetProperty("BindType", out _) ? (short)1 : (short)0;
        }
        var bag = await ReadWonderlandSackBagAsync(connection, transaction, characterId, token);
        if (!bag.TryGetValue(checked((short)bagSlot), out var source) || source.Id != sack.ItemId)
            throw new InvalidDataException("The locked Wonderland sack is absent from its bag.");
        var afterConsume = string.Empty;
        foreach (var (slot, row) in bag)
        {
            if (slot == bagSlot && sack.Stack == 1) continue;
            var item = slot == bagSlot ? row.Item with { Stack = checked((short)(row.Item.Stack - 1)) } : row.Item;
            afterConsume = KitBagSlots.SetSlot(afterConsume, slot, item.ToCompactString());
        }
        // Validate every possible single outcome before drawing RNG. Bag capacity
        // must never expose a selective retry that favors particular rewards.
        foreach (var outcome in outcomes)
            if (!KitBagItemGrantPlanner.TryAdd(afterConsume, outcome.ItemId, outcome.Quantity, 99,
                    binding[outcome.ItemId], out _))
                return new(PetDurableReceiptStatus.WonderlandSackBagFull, KitBagSlot: bagSlot);

        var totalWeight = WonderlandSackRewardPolicy.TotalWeight(sackId);
        var roll = _wonderlandSackRollSource.NextRoll(totalWeight);
        var index = WonderlandSackRewardPolicy.SelectIndex(sackId, roll);
        var reward = outcomes[index];
        var bound = binding[reward.ItemId];
        if (!KitBagItemGrantPlanner.TryAdd(afterConsume, reward.ItemId, reward.Quantity, 99, bound, out var planned))
            throw new InvalidDataException("A preflighted Wonderland sack reward no longer fits.");
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, sack, token);
        var revision = await AdvanceInventoryRevisionAsync(connection, transaction, characterId, character.InventoryRevision, token);
        var mutations = new List<InventoryMutation>
        {
            new(sack.ItemId, consumed.MutationKind, sack.BeforeState, consumed.AfterState, "wonderland_sack_consumed", revision)
        };
        mutations.AddRange(await GrantWonderlandSackRewardAsync(connection, transaction, characterId,
            bag, planned, reward.ItemId, bagSlot, sack.Stack == 1, revision, token));
        var evidence = new WonderlandSackOpenEvidence(sack.ItemId, sackId, bagSlot,
            WonderlandSackRewardPolicy.Revision, _itemContent.Templates.Revision.Sha256,
            roll, totalWeight, index, reward.ItemId, reward.Quantity, bound);
        return new(PetDurableReceiptStatus.WonderlandSackOpened, KitBagSlot: bagSlot,
            InventoryMutations: mutations, WonderlandSack: evidence);
    }

    private static bool HasNativeInteger(JsonElement stats, string key, int expected) =>
        stats.TryGetProperty(key, out var value) &&
        (value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out var number) && number == expected :
            value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var textNumber) && textNumber == expected);
}
