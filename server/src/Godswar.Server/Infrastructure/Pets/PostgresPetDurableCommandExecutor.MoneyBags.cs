using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Commands;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> OpenMoneyBagAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, int bagSlot, LockedBagItem item,
        LockedCharacter character, BagConsumableEffect effect, CancellationToken token)
    {
        if (item.Stack < 1 || !MoneyBagPolicy.TryResolve(_itemContent.Templates,
            checked((uint)item.PropId), out var reviewed) || reviewed != effect)
            return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        if (character.Silver < 0 || character.Silver > int.MaxValue - effect.Amount ||
            character.WalletRevision is < 0 or long.MaxValue)
            return new(PetDurableReceiptStatus.MoneyBagWalletFull, KitBagSlot: bagSlot);

        var evidence = new MoneyBagOpenEvidence(item.ItemId, checked((uint)item.PropId), bagSlot,
            effect.SkillId, effect.Amount, character.Silver, checked(character.Silver + effect.Amount),
            character.WalletRevision, checked(character.WalletRevision + 1));
        if (!evidence.IsValid) throw new InvalidDataException("Invalid money-bag settlement evidence.");
        await using (var update = CreateCommand("""
            UPDATE public.character_base SET "Money" = @silver, wallet_revision = @revision
            WHERE id = @characterId AND wallet_revision = @expected AND "Money" = @previous;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("silver", evidence.NewSilver);
            update.Parameters.AddWithValue("revision", evidence.NewWalletRevision);
            update.Parameters.AddWithValue("characterId", characterId);
            update.Parameters.AddWithValue("expected", evidence.PreviousWalletRevision);
            update.Parameters.AddWithValue("previous", evidence.PreviousSilver);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidDataException("The locked money-bag wallet did not advance exactly once.");
        }
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, item, token);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction,
            characterId, character.InventoryRevision, token);
        return new(PetDurableReceiptStatus.MoneyBagOpened, KitBagSlot: bagSlot, MoneyBag: evidence,
            InventoryMutations: [new InventoryMutation(item.ItemId, consumed.MutationKind,
                item.BeforeState, consumed.AfterState, "money_bag_consumed", inventoryRevision)]);
    }
    private async Task PersistMoneyBagCurrencyLedgerAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CommandSubject subject, long inboxId, MoneyBagOpenEvidence evidence,
        CancellationToken token)
    {
        if (!evidence.IsValid) throw new InvalidDataException("Invalid money-bag wallet ledger.");
        await using var command = CreateCommand("""
            INSERT INTO public.character_currency_ledger(command_inbox_id,account_id,character_id,wallet_revision,
                currency_code,delta,balance_before,balance_after,reason_code)
            VALUES(@inbox,@account,@character,@revision,'silver',@delta,@before,@after,'bag_money_bag');
            """, connection, transaction);
        command.Parameters.AddWithValue("inbox", inboxId);
        command.Parameters.AddWithValue("account", subject.AccountId);
        command.Parameters.AddWithValue("character", subject.CharacterId);
        command.Parameters.AddWithValue("revision", evidence.NewWalletRevision);
        command.Parameters.AddWithValue("delta", evidence.Amount);
        command.Parameters.AddWithValue("before", evidence.PreviousSilver);
        command.Parameters.AddWithValue("after", evidence.NewSilver);
        await command.ExecuteNonQueryAsync(token);
    }

}
