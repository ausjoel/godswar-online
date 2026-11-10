using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> UsePotionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CommandEnvelope<BagItemActivationCommand> envelope, LockedBagItem item, LockedCharacter character,
        BagConsumableEffect effect, CancellationToken token)
    {
        var slot = envelope.Command.KitBagSlot;
        if (item.Stack < 1 || !PotionUsePolicy.TryResolve(_itemContent.Templates,
            checked((uint)item.PropId), out var reviewed) || reviewed != effect)
            return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: slot);
        if (envelope.Command.PotionContext is not { IsValid: true } context ||
            context.ItemTemplateId != checked((uint)item.PropId) ||
            context.Before.Hp != character.CurrentHp || context.Before.Mp != character.CurrentMp ||
            context.Before.Revision != character.VitalsRevision)
            return new(PetDurableReceiptStatus.PotionContextMismatch, KitBagSlot: slot);
        if (!PotionUsePolicy.TryPlan(context.Before, effect, out var plan))
            return new(PetDurableReceiptStatus.PotionNotNeeded, KitBagSlot: slot);
        var evidence = new PotionUseEvidence(item.ItemId, checked((uint)item.PropId), slot, effect.SkillId,
            plan!.AppliedAmount, plan.Before, plan.PersistedVitals, envelope.Ownership);
        if (!evidence.IsValid) throw new InvalidDataException("Invalid potion settlement evidence.");
        await using (var update = CreateCommand("""
            UPDATE public.character_base SET "curHP"=@hp, "curMP"=@mp, vitals_revision=@revision
            WHERE id=@character AND account_id=@account AND lifecycle_state='active'
              AND checkpoint_owner_id=@owner AND checkpoint_owner_generation=@generation
              AND vitals_revision=@previousRevision AND "curHP"=@previousHp AND "curMP"=@previousMp;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("hp", evidence.After.Hp);
            update.Parameters.AddWithValue("mp", evidence.After.Mp);
            update.Parameters.AddWithValue("revision", evidence.After.Revision);
            update.Parameters.AddWithValue("character", envelope.Subject.CharacterId);
            update.Parameters.AddWithValue("account", envelope.Subject.AccountId);
            update.Parameters.AddWithValue("owner", envelope.Ownership.OwnerId);
            update.Parameters.AddWithValue("generation", envelope.Ownership.Generation);
            update.Parameters.AddWithValue("previousRevision", evidence.Before.Revision);
            update.Parameters.AddWithValue("previousHp", evidence.Before.Hp);
            update.Parameters.AddWithValue("previousMp", evidence.Before.Mp);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidDataException("The locked potion vitals did not advance exactly once.");
        }
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, envelope.Subject.CharacterId,
            slot, item, token);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction,
            envelope.Subject.CharacterId, character.InventoryRevision, token);
        return new(PetDurableReceiptStatus.PotionUsed, KitBagSlot: slot, Potion: evidence,
            InventoryMutations: [new InventoryMutation(item.ItemId, consumed.MutationKind,
                item.BeforeState, consumed.AfterState, "bag_potion_consumed", inventoryRevision)]);
    }
}
