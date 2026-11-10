using static Godswar.Server.Infrastructure.Inventory.PostgresItemAcquisitionPolicy;
using Godswar.Server.State;
using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Rewards;

internal sealed partial class PostgresMonsterRewardExtrasStore
{
    public async Task<MonsterLootPickupResult> PickupMonsterLootAsync(
        int accountId,
        int characterId,
        Guid deathEventId,
        int lootIndex,
        uint itemId,
        int quantity,
        bool? boundOnPickup = null,
        ItemGrantAttributes? attributes = null,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 ||
            deathEventId == Guid.Empty || lootIndex is < 0 or >= 32 ||
            itemId == 0 || quantity is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var itemPolicy = await ReadLootItemPolicyAsync(
            connection,
            transaction,
            itemId,
            cancellationToken);
        if (!itemPolicy.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(MonsterLootPickupStatus.Unsupported, null);
        }
        // The rule decides whether this drop binds; a rule that says nothing
        // keeps the item template's own BindType. Two stacks of one item only
        // merge when their bind flag matches, so a tradeable drop beside a bound
        // stack takes a slot of its own - which is what the client shows.
        var (stackCap, templateBound) = itemPolicy.Value;
        var bound = boundOnPickup is { } forcedBind
            ? (forcedBind ? (short)1 : (short)0)
            : templateBound;
        await LockDeathIdentityAsync(
            connection,
            transaction,
            $"loot:{deathEventId:N}:{lootIndex}",
            cancellationToken);

        var existing = await ReadLootClaimAsync(
            connection,
            transaction,
            deathEventId,
            lootIndex,
            accountId,
            characterId,
            itemId,
            quantity,
            cancellationToken);
        if (existing.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                existing.Value,
                existing.Value == MonsterLootPickupStatus.Duplicate
                    ? await GetCharacterByIdAsync(
                        characterId,
                        cancellationToken)
                    : null);
        }

        var inventoryRevision = await LockCharacterInventoryAsync(
            connection,
            transaction,
            accountId,
            characterId,
            cancellationToken);
        if (!inventoryRevision.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(MonsterLootPickupStatus.CharacterNotFound, null);
        }

        var bag = await LockLootBagAsync(
            connection,
            transaction,
            characterId,
            checked((int)itemId),
            bound,
            stackCap,
            cancellationToken);
        var plan = PlanLootBagMutation(bag, quantity, stackCap);
        if (plan is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                MonsterLootPickupStatus.InsufficientCapacity,
                await GetCharacterByIdAsync(
                    characterId,
                    cancellationToken));
        }

        var mutations = await ApplyLootBagMutationAsync(
            connection,
            transaction,
            characterId,
            checked((int)itemId),
            bound,
            plan,
            attributes ?? ItemGrantAttributes.None,
            cancellationToken);
        var nextRevision = checked(inventoryRevision.Value + 1);
        await AdvanceLootInventoryRevisionAsync(
            connection,
            transaction,
            accountId,
            characterId,
            inventoryRevision.Value,
            nextRevision,
            cancellationToken);
        var inboxId = await InsertLootCommandEvidenceAsync(
            connection,
            transaction,
            accountId,
            characterId,
            deathEventId,
            lootIndex,
            itemId,
            quantity,
            nextRevision,
            cancellationToken);
        await InsertLootInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            accountId,
            characterId,
            nextRevision,
            mutations,
            cancellationToken);
        await InsertLootClaimAsync(
            connection,
            transaction,
            deathEventId,
            lootIndex,
            accountId,
            characterId,
            itemId,
            quantity,
            nextRevision,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            MonsterLootPickupStatus.Added,
            await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    private static async Task LockDeathIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("identity", identity);
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<MonsterLootPickupStatus?> ReadLootClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deathEventId,
        int lootIndex,
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT account_id, character_id, item_id, quantity
            FROM public.monster_loot_pickup_claims
            WHERE death_event_id = @deathEventId
              AND loot_index = @lootIndex
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("deathEventId", deathEventId);
        command.Parameters.AddWithValue("lootIndex", checked((short)lootIndex));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return reader.GetInt32(0) == accountId &&
               reader.GetInt32(1) == characterId &&
               reader.GetInt32(2) == checked((int)itemId) &&
               reader.GetInt16(3) == quantity
            ? MonsterLootPickupStatus.Duplicate
            : MonsterLootPickupStatus.RequestConflict;
    }

    private static async Task<long?> LockCharacterInventoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId AND id = @characterId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long revision && revision >= 0
            ? revision
            : null;
    }

    private static async Task<LootBag> LockLootBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemId,
        short bound,
        short stackCap,
        CancellationToken cancellationToken)
    {
        var occupied = new bool[KitBagProjectionSlots];
        var stacks = new List<LootStack>();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, slot_index, prop_id, bound, stack,
                   to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId AND item_location = 1
              AND slot_index BETWEEN 0 AND 95
            ORDER BY slot_index, id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(1);
            if (occupied[slot])
            {
                throw new InvalidDataException(
                    "The kit bag contains a duplicate slot.");
            }
            occupied[slot] = true;
            if (reader.GetInt32(2) == itemId &&
                reader.GetInt16(3) == bound)
            {
                var stack = reader.GetInt16(4);
                if (stack > stackCap)
                {
                    throw new InvalidDataException(
                        "A loot target stack exceeds its pinned capacity.");
                }
                if (stack < stackCap)
                {
                    stacks.Add(new(
                        reader.GetInt64(0),
                        slot,
                        stack,
                        reader.GetString(5)));
                }
            }
        }
        return new(stacks, occupied);
    }

    private static LootPlan? PlanLootBagMutation(
        LootBag bag,
        int quantity,
        short stackCap)
    {
        var remaining = quantity;
        var updates = new List<LootUpdate>();
        foreach (var stack in bag.Stacks)
        {
            var added = Math.Min(remaining, stackCap - stack.Stack);
            if (added > 0)
            {
                updates.Add(new(
                    stack,
                    checked((short)(stack.Stack + added))));
                remaining -= added;
            }
            if (remaining == 0)
            {
                break;
            }
        }
        var inserts = new List<LootInsert>();
        for (short slot = 0;
             remaining > 0 && slot < KitBagProjectionSlots;
             slot++)
        {
            if (bag.Occupied[slot])
            {
                continue;
            }
            var stack = checked((short)Math.Min(remaining, stackCap));
            inserts.Add(new(slot, stack));
            remaining -= stack;
        }
        return remaining == 0 ? new(updates, inserts) : null;
    }

    private static async Task<IReadOnlyList<LootMutation>>
        ApplyLootBagMutationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            int itemId,
            short bound,
            LootPlan plan,
            ItemGrantAttributes attributes,
            CancellationToken cancellationToken)
    {
        var mutations = new List<LootMutation>(
            plan.Updates.Count + plan.Inserts.Count);
        foreach (var update in plan.Updates)
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE public.character_items
                SET stack = @stack, updated_at = transaction_timestamp()
                WHERE id = @id AND user_id = @characterId
                  AND item_location = 1 AND stack = @before
                RETURNING to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("stack", update.StackAfter);
            command.Parameters.AddWithValue("id", update.Stack.InstanceId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("before", update.Stack.Stack);
            var after = await command.ExecuteScalarAsync(cancellationToken)
                as string ?? throw new InvalidDataException(
                    "Loot stack update was not exact.");
            mutations.Add(new(
                update.Stack.InstanceId,
                "update",
                update.Stack.BeforeState,
                after));
        }
        foreach (var insert in plan.Inserts)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO public.character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack,
                    item_exp, holy_suit_code,
                    attribute1, attribute_level1,
                    attribute2, attribute_level2,
                    attribute3, attribute_level3,
                    attribute4, attribute_level4,
                    attribute5, attribute_level5)
                VALUES (@characterId, 1, @slot, @itemId,
                        @quality, @grade, @bound, @stack, 0, 0,
                        @attribute1, @attributeLevel1,
                        @attribute2, @attributeLevel2,
                        @attribute3, @attributeLevel3,
                        @attribute4, @attributeLevel4,
                        @attribute5, @attributeLevel5)
                RETURNING id, to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("slot", insert.Slot);
            command.Parameters.AddWithValue("itemId", itemId);
            command.Parameters.AddWithValue("bound", bound);
            command.Parameters.AddWithValue("stack", insert.Stack);
            command.Parameters.AddWithValue("quality", attributes.Quality);
            command.Parameters.AddWithValue("grade", attributes.Grade);
            AddAttributeSlot(
                command.Parameters,
                "attribute1",
                "attributeLevel1",
                attributes.Attribute1,
                attributes.AttributeLevel1);
            AddAttributeSlot(
                command.Parameters,
                "attribute2",
                "attributeLevel2",
                attributes.Attribute2,
                attributes.AttributeLevel2);
            AddAttributeSlot(
                command.Parameters,
                "attribute3",
                "attributeLevel3",
                attributes.Attribute3,
                attributes.AttributeLevel3);
            AddAttributeSlot(
                command.Parameters,
                "attribute4",
                "attributeLevel4",
                attributes.Attribute4,
                attributes.AttributeLevel4);
            AddAttributeSlot(
                command.Parameters,
                "attribute5",
                "attributeLevel5",
                attributes.Attribute5,
                attributes.AttributeLevel5);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "Loot item insert returned no state.");
            }
            mutations.Add(new(
                reader.GetInt64(0),
                "add",
                null,
                reader.GetString(1)));
        }
        return mutations;
    }

    /// <summary>
    /// Writes one attribute slot, empty slots as real <c>NULL</c>.
    /// </summary>
    /// <remarks>
    /// The parameter types are explicit because a <c>null</c> value with an
    /// inferred type sends nothing usable, and a slot left empty has to be
    /// <c>NULL</c>: attribute id 0 is <c>AttackA</c>, a real attribute the client
    /// draws as "physical attack I", so writing 0 puts a phantom line on every
    /// item that configured fewer than five.
    /// </remarks>
    private static void AddAttributeSlot(
        NpgsqlParameterCollection parameters,
        string idName,
        string levelName,
        short? id,
        short? level)
    {
        parameters.Add(new NpgsqlParameter(idName, NpgsqlDbType.Smallint)
        {
            Value = id.HasValue ? id.Value : DBNull.Value
        });
        parameters.Add(new NpgsqlParameter(levelName, NpgsqlDbType.Smallint)
        {
            Value = id.HasValue && level.HasValue ? level.Value : DBNull.Value
        });
    }

    private sealed record LootBag(
        IReadOnlyList<LootStack> Stacks,
        bool[] Occupied);

    private sealed record LootStack(
        long InstanceId,
        short Slot,
        short Stack,
        string BeforeState);

    private sealed record LootUpdate(LootStack Stack, short StackAfter);

    private sealed record LootInsert(short Slot, short Stack);

    private sealed record LootPlan(
        IReadOnlyList<LootUpdate> Updates,
        IReadOnlyList<LootInsert> Inserts);

    private sealed record LootMutation(
        long InstanceId,
        string Kind,
        string? BeforeState,
        string? AfterState);
}
