using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> UseProgressItemAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, int bagSlot, LockedBagItem item,
        LockedCharacter character, ProgressItemGrant grant, CancellationToken token)
    {
        if (item.Stack < 1) return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        if (grant.Kind is ProgressItemKind.Firework or ProgressItemKind.StatusBuff or ProgressItemKind.Teleport or ProgressItemKind.Sigil)
            return await UseFireworkAsync(connection, transaction, characterId, bagSlot, item, character, grant, token);
        if (grant.Kind is ProgressItemKind.GuildSilver or ProgressItemKind.GuildGold)
            return await UseGuildTokenAsync(connection, transaction, characterId, bagSlot, item, character, grant, token);
        if (grant.Kind == ProgressItemKind.Title)
            return await UseTitleItemAsync(connection, transaction, characterId, bagSlot, item, character, grant, token);
        if (grant.MaximumAmount > grant.Amount)
            grant = grant with { Amount = Random.Shared.Next(grant.Amount, grant.MaximumAmount + 1) };
        var (balanceColumn, revisionColumn) = grant.Kind switch
        {
            ProgressItemKind.Gold => ("\"Stone\"", "wallet_revision"),
            ProgressItemKind.Silver => ("\"Money\"", "wallet_revision"),
            ProgressItemKind.BindingGold => ("\"BindingGold\"", "wallet_revision"),
            ProgressItemKind.Honor => ("medusa_honor_points", "wallet_revision"),
            ProgressItemKind.ExchangePoint => ("exchange_point", "wallet_revision"),
            ProgressItemKind.TalentPoints => ("\"SkillPoint\"", "progression_reward_revision"),
            _ => throw new InvalidDataException("Unknown progress item kind.")
        };

        // ExecuteAsync already holds this character's ownership fence and row lock.
        int balance;
        long revision;
        await using (var read = CreateCommand($"""
            SELECT {balanceColumn}, {revisionColumn} FROM public.character_base WHERE id = @characterId FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("The locked item recipient disappeared.");
            balance = reader.GetInt32(0);
            revision = reader.GetInt64(1);
        }
        if (balance < 0 || balance > int.MaxValue - grant.Amount || revision is < 0 or long.MaxValue)
            return new(PetDurableReceiptStatus.ProgressItemBalanceFull, KitBagSlot: bagSlot);

        var evidence = new ProgressItemEvidence(item.ItemId, checked((uint)item.PropId), bagSlot, grant.Kind,
            grant.Amount, balance, balance + grant.Amount, revision, revision + 1);
        if (!evidence.IsValid) throw new InvalidDataException("Invalid progress item settlement evidence.");
        await using (var update = CreateCommand($"""
            UPDATE public.character_base SET {balanceColumn} = @balance, {revisionColumn} = @revision
            WHERE id = @characterId AND {revisionColumn} = @expected AND {balanceColumn} = @previous;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("balance", evidence.NewBalance);
            update.Parameters.AddWithValue("revision", evidence.NewRevision);
            update.Parameters.AddWithValue("characterId", characterId);
            update.Parameters.AddWithValue("expected", evidence.PreviousRevision);
            update.Parameters.AddWithValue("previous", evidence.PreviousBalance);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidDataException("The locked progress balance did not advance exactly once.");
        }
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, item, token);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction,
            characterId, character.InventoryRevision, token);
        return new(PetDurableReceiptStatus.ProgressItemUsed, KitBagSlot: bagSlot, ProgressItem: evidence,
            InventoryMutations: [new InventoryMutation(item.ItemId, consumed.MutationKind,
                item.BeforeState, consumed.AfterState, "progress_item_consumed", inventoryRevision)]);
    }

    // No balance moves. The evidence records one use against the inventory revision it advanced.
    private async Task<PetTransition> UseFireworkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int characterId, int bagSlot, LockedBagItem item, LockedCharacter character, ProgressItemGrant grant,
        CancellationToken token)
    {
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, item, token);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction,
            characterId, character.InventoryRevision, token);
        var evidence = new ProgressItemEvidence(item.ItemId, checked((uint)item.PropId), bagSlot, grant.Kind,
            grant.Amount, 0, grant.Amount, inventoryRevision - 1, inventoryRevision);
        if (!evidence.IsValid) throw new InvalidDataException("Invalid firework evidence.");
        return new(PetDurableReceiptStatus.ProgressItemUsed, KitBagSlot: bagSlot, ProgressItem: evidence,
            InventoryMutations: [new InventoryMutation(item.ItemId, consumed.MutationKind,
                item.BeforeState, consumed.AfterState, "progress_item_consumed", inventoryRevision)]);
    }

    // Amount is the title id. A title already owned keeps the item. The grant's trigger advances the reward revision.
    private async Task<PetTransition> UseTitleItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int characterId, int bagSlot, LockedBagItem item, LockedCharacter character, ProgressItemGrant grant,
        CancellationToken token)
    {
        await using (var owned = CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM public.character_title_entitlements
                WHERE character_id = @characterId AND title_id = @titleId);
            """, connection, transaction))
        {
            owned.Parameters.AddWithValue("characterId", characterId);
            owned.Parameters.AddWithValue("titleId", grant.Amount);
            if ((bool)(await owned.ExecuteScalarAsync(token))!)
                return new(PetDurableReceiptStatus.ProgressItemBalanceFull, KitBagSlot: bagSlot);
        }
        await using (var insert = CreateCommand("""
            INSERT INTO public.character_title_grants(character_id,title_id,granted_by,note)
            VALUES(@characterId,@titleId,'item',@note);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("characterId", characterId);
            insert.Parameters.AddWithValue("titleId", grant.Amount);
            insert.Parameters.AddWithValue("note", $"bag item {item.PropId}");
            await insert.ExecuteNonQueryAsync(token);
        }
        long revision;
        await using (var read = CreateCommand("""
            SELECT medusa_reward_revision FROM public.character_base WHERE id = @characterId;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            revision = (long)(await read.ExecuteScalarAsync(token))!;
        }
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, item, token);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction,
            characterId, character.InventoryRevision, token);
        var evidence = new ProgressItemEvidence(item.ItemId, checked((uint)item.PropId), bagSlot, grant.Kind,
            grant.Amount, 0, grant.Amount, revision - 1, revision);
        if (!evidence.IsValid) throw new InvalidDataException("Invalid title item evidence.");
        return new(PetDurableReceiptStatus.ProgressItemUsed, KitBagSlot: bagSlot, ProgressItem: evidence,
            InventoryMutations: [new InventoryMutation(item.ItemId, consumed.MutationKind,
                item.BeforeState, consumed.AfterState, "progress_item_consumed", inventoryRevision)]);
    }

    // Same lock order as guild donations: character (already held by ExecuteAsync), then guild.
    private async Task<PetTransition> UseGuildTokenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int characterId, int bagSlot, LockedBagItem item, LockedCharacter character, ProgressItemGrant grant,
        CancellationToken token)
    {
        var column = grant.Kind == ProgressItemKind.GuildGold ? "gold" : "silver";
        long guildId, balance;
        await using (var read = CreateCommand($"""
            SELECT g.id, g.{column} FROM public.guild_members AS m
            JOIN public.guilds AS g ON g.id = m.guild_id
            WHERE m.character_id = @characterId FOR UPDATE OF g;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return new(PetDurableReceiptStatus.ProgressItemNoGuild, KitBagSlot: bagSlot);
            guildId = reader.GetInt64(0);
            balance = reader.GetInt64(1);
        }
        if (balance < 0 || balance > int.MaxValue - grant.Amount)
            return new(PetDurableReceiptStatus.ProgressItemBalanceFull, KitBagSlot: bagSlot);

        await using (var update = CreateCommand($"""
            UPDATE public.guilds SET {column} = @balance, updated_at = now() WHERE id = @guildId AND {column} = @previous;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("balance", balance + grant.Amount);
            update.Parameters.AddWithValue("guildId", guildId);
            update.Parameters.AddWithValue("previous", balance);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidDataException("The locked guild balance did not advance exactly once.");
        }
        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, item, token);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(connection, transaction,
            characterId, character.InventoryRevision, token);
        var evidence = new ProgressItemEvidence(item.ItemId, checked((uint)item.PropId), bagSlot, grant.Kind,
            grant.Amount, (int)balance, (int)balance + grant.Amount, inventoryRevision - 1, inventoryRevision);
        if (!evidence.IsValid) throw new InvalidDataException("Invalid guild token evidence.");
        return new(PetDurableReceiptStatus.ProgressItemUsed, KitBagSlot: bagSlot, ProgressItem: evidence,
            InventoryMutations: [new InventoryMutation(item.ItemId, consumed.MutationKind,
                item.BeforeState, consumed.AfterState, "progress_item_consumed", inventoryRevision)]);
    }

    private async Task PersistProgressItemCurrencyLedgerAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CommandSubject subject, long inboxId, ProgressItemEvidence evidence,
        CancellationToken token)
    {
        if (!evidence.IsValid) throw new InvalidDataException("Invalid progress item ledger.");
        var currency = evidence.Kind switch
        {
            ProgressItemKind.Gold => "gold",
            ProgressItemKind.BindingGold => "binding_gold",
            ProgressItemKind.Honor => "honor",
            ProgressItemKind.ExchangePoint => "exchange_point",
            _ => null // Talent points are not a wallet currency; the command audit keeps them.
        };
        if (currency is null) return;
        await using var command = CreateCommand("""
            INSERT INTO public.character_currency_ledger(command_inbox_id,account_id,character_id,wallet_revision,
                currency_code,delta,balance_before,balance_after,reason_code)
            VALUES(@inbox,@account,@character,@revision,@currency,@delta,@before,@after,'bag_progress_item');
            """, connection, transaction);
        command.Parameters.AddWithValue("inbox", inboxId);
        command.Parameters.AddWithValue("account", subject.AccountId);
        command.Parameters.AddWithValue("character", subject.CharacterId);
        command.Parameters.AddWithValue("revision", evidence.NewRevision);
        command.Parameters.AddWithValue("currency", currency);
        command.Parameters.AddWithValue("delta", evidence.Amount);
        command.Parameters.AddWithValue("before", evidence.PreviousBalance);
        command.Parameters.AddWithValue("after", evidence.NewBalance);
        await command.ExecuteNonQueryAsync(token);
    }
}
