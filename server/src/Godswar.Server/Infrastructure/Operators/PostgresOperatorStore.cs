using System.Text;
using Godswar.Server.Application.Operators;
using Npgsql;

namespace Godswar.Server.Infrastructure.Operators;

// ROUND2: database side of the operator (GM) commands. Every write runs in one
// transaction, locks the character row first (the same order the game's own
// inventory writers use) and advances character_base.inventory_revision when
// it changes the bag, so a durable command prepared against the old bag fails
// its precondition instead of acting on a stale picture.
internal sealed partial class PostgresOperatorStore : IOperatorStore
{
    private const short KitBagLocation = 1;
    private const int KitBagSlots = 96;

    // Pet rows that describe the pet itself and must follow a copied pet.
    // Previews are short-lived operation state and are not copied.
    private static readonly string[] PetChildTables =
    [
        "character_pet_skills",
        "character_pet_stat_values",
        "character_pet_character_bonuses"
    ];

    private readonly NpgsqlDataSource _dataSource;

    public PostgresOperatorStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public const string EnsureSql =
        """
        CREATE TABLE IF NOT EXISTS public.operator_log (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            at timestamptz NOT NULL DEFAULT now(),
            actor_account_id integer NOT NULL,
            actor_character varchar(64) NOT NULL,
            command varchar(32) NOT NULL,
            target varchar(128) NOT NULL DEFAULT '',
            detail text NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_operator_log_at ON public.operator_log (at);
        """;

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(EnsureSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ReadCharacterGmAsync(int accountId, int characterId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM public.character_base c JOIN public.accounts a ON a.id=c.account_id
              WHERE c.id=@character AND c.account_id=@account AND c."GM"=1
                AND c.deleted_at IS NULL AND c.lifecycle_state='active' AND a.status=0);
            """);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("character", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<OperatorCharacterRef?> FindCharacterAsync(
        string characterName,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT a.id, a.username, a.status, c.id, c.name, COALESCE(c.mutetime, 0)
            FROM public.character_base c
            JOIN public.accounts a ON a.id = c.account_id
            WHERE lower(c.name) = lower(@name) AND c.deleted_at IS NULL
            ORDER BY c.id
            LIMIT 2;
            """);
        command.Parameters.AddWithValue("name", characterName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var found = new OperatorCharacterRef(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetInt16(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetInt32(5));
        // Two live characters with the same name (case-insensitive) would be
        // ambiguous: refuse rather than act on the wrong one.
        return await reader.ReadAsync(cancellationToken) ? null : found;
    }

    public async Task<short> ReadAccountStatusAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT status FROM public.accounts WHERE id = @id;");
        command.Parameters.AddWithValue("id", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) is short status
            ? status
            : (short)0;
    }

    public async Task<bool> SetAccountStatusAsync(
        int accountId,
        short status,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "UPDATE public.accounts SET status = @status WHERE id = @id;");
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> SetMuteAsync(
        int characterId,
        int untilUnixSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "UPDATE public.character_base SET mutetime = @until WHERE id = @id;");
        command.Parameters.AddWithValue("until", untilUnixSeconds);
        command.Parameters.AddWithValue("id", characterId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> MoveOfflineCharacterAsync(
        int characterId,
        byte mapId,
        float x,
        float z,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET "Map" = @map, "Pos_X" = @x, "Pos_Z" = @z
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("map", (short)mapId);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("z", z);
        command.Parameters.AddWithValue("id", characterId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<OperatorBagItem>> ReadBagAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT i.id, i.slot_index, i.prop_id, i.item_quality, i.item_grade,
                   i.bound, i.stack, i.item_exp, s.pet_id
            FROM public.character_items i
            LEFT JOIN public.sealed_pet_items s ON s.item_instance_id = i.id
            WHERE i.user_id = @characterId AND i.item_location = @kitBag
            ORDER BY i.slot_index;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("kitBag", KitBagLocation);
        var items = new List<OperatorBagItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new OperatorBagItem(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt32(2),
                reader.GetInt16(3),
                reader.GetInt16(4),
                reader.GetInt16(5),
                reader.GetInt16(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        }

        return items;
    }

    public async Task<OperatorItemResult> GrantItemAsync(
        int characterId,
        uint itemId,
        int quantity,
        short stackCap,
        short bound,
        CancellationToken cancellationToken,
        short quality = 1,
        short grade = 1)
    {
        if (quantity < 1 || stackCap < 1 || bound is < 0 or > 1 ||
            quality is < 0 or > 30 || grade is < 0 or > 30)
        {
            return new(OperatorItemOutcome.InvalidValue, "bad quantity or stack size");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCharacterAsync(connection, transaction, characterId, cancellationToken))
        {
            return new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }

        var free = await FreeSlotsAsync(connection, transaction, characterId, cancellationToken);
        var stacks = (quantity + stackCap - 1) / stackCap;
        if (free.Count < stacks)
        {
            return new(
                OperatorItemOutcome.NoFreeSlot,
                $"needs {stacks} free bag slots, {free.Count} free");
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
            VALUES (@characterId, @kitBag, @slot, @itemId, @quality, @grade, @bound, @stack, 0, 0);
            """,
            connection,
            transaction);
        var remaining = quantity;
        var written = 0;
        foreach (var slot in free)
        {
            if (remaining <= 0)
            {
                break;
            }

            var stack = (short)Math.Min(remaining, stackCap);
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("characterId", characterId);
            insert.Parameters.AddWithValue("kitBag", KitBagLocation);
            insert.Parameters.AddWithValue("slot", slot);
            insert.Parameters.AddWithValue("itemId", checked((int)itemId));
            insert.Parameters.AddWithValue("bound", bound);
            insert.Parameters.AddWithValue("stack", stack);
            insert.Parameters.AddWithValue("quality", quality);
            insert.Parameters.AddWithValue("grade", grade);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            remaining -= stack;
            written++;
        }

        await AdvanceInventoryRevisionAsync(connection, transaction, characterId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            OperatorItemOutcome.Done,
            $"slots {string.Join(',', free.Take(written))}",
            written);
    }

    public async Task<OperatorItemResult> SetBagItemFieldAsync(
        int characterId,
        short slot,
        OperatorItemField field,
        int value,
        CancellationToken cancellationToken)
    {
        var (column, minimum, maximum) = field switch
        {
            OperatorItemField.Quality => ("item_quality", 0, 30),
            OperatorItemField.Grade => ("item_grade", 0, 30),
            OperatorItemField.Bound => ("bound", 0, 1),
            OperatorItemField.Stack => ("stack", 1, 255),
            OperatorItemField.ItemExperience => ("item_exp", 0, int.MaxValue),
            _ => (string.Empty, 0, 0)
        };
        if (column.Length == 0 || value < minimum || value > maximum ||
            slot is < 0 or >= KitBagSlots)
        {
            return new(
                OperatorItemOutcome.InvalidValue,
                $"value must be {minimum}..{maximum}, slot 0..{KitBagSlots - 1}");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCharacterAsync(connection, transaction, characterId, cancellationToken))
        {
            return new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }

        await using var update = new NpgsqlCommand(
            $"""
            UPDATE public.character_items
            SET {column} = @value, updated_at = now()
            WHERE user_id = @characterId AND item_location = @kitBag AND slot_index = @slot
            RETURNING prop_id;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("characterId", characterId);
        update.Parameters.AddWithValue("kitBag", KitBagLocation);
        update.Parameters.AddWithValue("slot", slot);
        if (field == OperatorItemField.ItemExperience)
        {
            update.Parameters.AddWithValue("value", value);
        }
        else
        {
            update.Parameters.AddWithValue("value", checked((short)value));
        }

        object? itemId;
        try
        {
            itemId = await update.ExecuteScalarAsync(cancellationToken);
        }
        catch (PostgresException error)
        {
            // A CHECK or trigger (for example a bound sealed pet) refused it.
            return new(OperatorItemOutcome.Refused, error.MessageText);
        }

        if (itemId is null)
        {
            return new(OperatorItemOutcome.SlotEmpty, $"bag slot {slot} is empty");
        }

        await AdvanceInventoryRevisionAsync(connection, transaction, characterId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OperatorItemOutcome.Done, $"item {itemId} slot {slot} {column}={value}", 1);
    }

    public async Task<OperatorItemResult> DuplicateBagItemAsync(
        int characterId,
        short slot,
        CancellationToken cancellationToken)
    {
        if (slot is < 0 or >= KitBagSlots)
        {
            return new(OperatorItemOutcome.InvalidValue, $"slot must be 0..{KitBagSlots - 1}");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCharacterAsync(connection, transaction, characterId, cancellationToken))
        {
            return new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }

        long sourceRowId;
        long? sourcePetId;
        await using (var read = new NpgsqlCommand(
            """
            SELECT i.id, s.pet_id
            FROM public.character_items i
            LEFT JOIN public.sealed_pet_items s ON s.item_instance_id = i.id
            WHERE i.user_id = @characterId AND i.item_location = @kitBag AND i.slot_index = @slot
            FOR UPDATE OF i;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            read.Parameters.AddWithValue("kitBag", KitBagLocation);
            read.Parameters.AddWithValue("slot", slot);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new(OperatorItemOutcome.SlotEmpty, $"bag slot {slot} is empty");
            }

            sourceRowId = reader.GetInt64(0);
            sourcePetId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        var free = await FreeSlotsAsync(connection, transaction, characterId, cancellationToken);
        if (free.Count == 0)
        {
            return new(OperatorItemOutcome.NoFreeSlot, "the bag has no free slot");
        }

        var target = free[0];
        try
        {
            var newRowId = await CopyRowAsync(
                connection,
                transaction,
                "character_items",
                "id",
                sourceRowId,
                new Dictionary<string, object>
                {
                    ["slot_index"] = target
                },
                cancellationToken);
            var detail = $"slot {slot} -> slot {target}";
            if (sourcePetId is { } petId)
            {
                var newPetId = await CopyRowAsync(
                    connection,
                    transaction,
                    "character_pets",
                    "id",
                    petId,
                    new Dictionary<string, object>
                    {
                        ["revision"] = 0L
                    },
                    cancellationToken);
                foreach (var table in PetChildTables)
                {
                    await CopyChildRowsAsync(
                        connection,
                        transaction,
                        table,
                        petId,
                        newPetId,
                        cancellationToken);
                }

                await using var link = new NpgsqlCommand(
                    """
                    INSERT INTO public.sealed_pet_items (
                        item_instance_id, pet_id, owner_character_id, seal_request_id,
                        item_instance_id_snapshot, pet_id_snapshot,
                        owner_character_id_snapshot, pet_species_id_snapshot,
                        pet_name_snapshot, pet_bound_snapshot)
                    SELECT @item, @pet, l.owner_character_id, gen_random_uuid(),
                           @item, @pet, l.owner_character_id_snapshot,
                           l.pet_species_id_snapshot, l.pet_name_snapshot,
                           l.pet_bound_snapshot
                    FROM public.sealed_pet_items l
                    WHERE l.item_instance_id = @sourceItem;
                    """,
                    connection,
                    transaction);
                link.Parameters.AddWithValue("item", newRowId);
                link.Parameters.AddWithValue("pet", newPetId);
                link.Parameters.AddWithValue("sourceItem", sourceRowId);
                if (await link.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("The sealed-pet link was not copied.");
                }

                detail += $" sealed pet {petId} -> pet {newPetId}";
            }

            await AdvanceInventoryRevisionAsync(connection, transaction, characterId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(OperatorItemOutcome.Done, detail, 1);
        }
        catch (PostgresException error)
        {
            return new(OperatorItemOutcome.Refused, error.MessageText);
        }
    }

    public async Task<OperatorItemResult> RemoveBagItemAsync(
        int characterId,
        short slot,
        CancellationToken cancellationToken)
    {
        if (slot is < 0 or >= KitBagSlots)
        {
            return new(OperatorItemOutcome.InvalidValue, $"slot must be 0..{KitBagSlots - 1}");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCharacterAsync(connection, transaction, characterId, cancellationToken))
        {
            return new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }

        await using var delete = new NpgsqlCommand(
            """
            DELETE FROM public.character_items i
            WHERE i.user_id = @characterId AND i.item_location = @kitBag AND i.slot_index = @slot
              AND NOT EXISTS (SELECT 1 FROM public.sealed_pet_items s WHERE s.item_instance_id = i.id)
            RETURNING i.prop_id, i.stack;
            """,
            connection,
            transaction);
        delete.Parameters.AddWithValue("characterId", characterId);
        delete.Parameters.AddWithValue("kitBag", KitBagLocation);
        delete.Parameters.AddWithValue("slot", slot);
        string detail;
        await using (var reader = await delete.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new(
                    OperatorItemOutcome.SlotEmpty,
                    $"bag slot {slot} is empty or holds a sealed pet (not removed)");
            }

            detail = $"removed item {reader.GetInt32(0)} x{reader.GetInt16(1)} from slot {slot}";
        }

        await AdvanceInventoryRevisionAsync(connection, transaction, characterId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OperatorItemOutcome.Done, detail, 1);
    }

    // PROF (q09): /craft make. Materials come from the lowest slots first; sealed pets are never touched.
    public async Task<OperatorItemResult> CraftAsync(
        int characterId,
        IReadOnlyList<(uint ItemId, int Count)> materials,
        uint productId,
        int productCount,
        short productStackCap,
        short bound,
        CancellationToken cancellationToken)
    {
        if (materials.Count is < 1 or > 4 || materials.Any(static m => m.Count < 1) ||
            productCount < 0 || productStackCap < 1 || bound is < 0 or > 1)
        {
            return new(OperatorItemOutcome.InvalidValue, "bad recipe");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCharacterAsync(connection, transaction, characterId, cancellationToken))
        {
            return new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }

        var rows = new List<(long Id, short Slot, uint ItemId, int Stack)>();
        await using (var select = new NpgsqlCommand(
            """
            SELECT i.id, i.slot_index, i.prop_id, i.stack
            FROM public.character_items i
            WHERE i.user_id = @characterId AND i.item_location = @kitBag AND i.prop_id = ANY(@items)
              AND NOT EXISTS (SELECT 1 FROM public.sealed_pet_items s WHERE s.item_instance_id = i.id)
            ORDER BY i.slot_index
            FOR UPDATE OF i;
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("characterId", characterId);
            select.Parameters.AddWithValue("kitBag", KitBagLocation);
            select.Parameters.AddWithValue("items", materials.Select(static m => checked((int)m.ItemId)).ToArray());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt64(0), reader.GetInt16(1), checked((uint)reader.GetInt32(2)),
                    Math.Max(1, (int)reader.GetInt16(3))));
            }
        }

        var missing = materials
            .Select(m => (m.ItemId, Short: m.Count - rows.Where(r => r.ItemId == m.ItemId).Sum(static r => r.Stack)))
            .Where(static m => m.Short > 0)
            .ToArray();
        if (missing.Length > 0)
        {
            return new(OperatorItemOutcome.Refused,
                "missing " + string.Join(", ", missing.Select(static m => $"{m.Short} x {m.ItemId}")));
        }

        if (productCount == 0)
        {
            return new(OperatorItemOutcome.Done, "nothing changed");
        }

        await using var delete = new NpgsqlCommand(
            "DELETE FROM public.character_items WHERE id = @id;", connection, transaction);
        await using var update = new NpgsqlCommand(
            "UPDATE public.character_items SET stack = @stack, updated_at = now() WHERE id = @id;",
            connection,
            transaction);
        var taken = new List<string>();
        foreach (var material in materials)
        {
            var remaining = material.Count;
            foreach (var row in rows.Where(r => r.ItemId == material.ItemId))
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (row.Stack <= remaining)
                {
                    delete.Parameters.Clear();
                    delete.Parameters.AddWithValue("id", row.Id);
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                    remaining -= row.Stack;
                    taken.Add($"slot {row.Slot} {material.ItemId} x{row.Stack}");
                }
                else
                {
                    update.Parameters.Clear();
                    update.Parameters.AddWithValue("id", row.Id);
                    update.Parameters.AddWithValue("stack", checked((short)(row.Stack - remaining)));
                    await update.ExecuteNonQueryAsync(cancellationToken);
                    taken.Add($"slot {row.Slot} {material.ItemId} x{remaining}");
                    remaining = 0;
                }
            }
        }

        var free = await FreeSlotsAsync(connection, transaction, characterId, cancellationToken);
        var stacks = (productCount + productStackCap - 1) / productStackCap;
        if (free.Count < stacks)
        {
            // Disposing the transaction without a commit rolls the material removal back.
            return new(OperatorItemOutcome.NoFreeSlot, $"needs {stacks} free bag slots, {free.Count} free");
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
            VALUES (@characterId, @kitBag, @slot, @itemId, 1, 1, @bound, @stack, 0, 0);
            """,
            connection,
            transaction);
        var left = productCount;
        var written = new List<short>();
        foreach (var slot in free)
        {
            if (left <= 0)
            {
                break;
            }

            var stack = (short)Math.Min(left, productStackCap);
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("characterId", characterId);
            insert.Parameters.AddWithValue("kitBag", KitBagLocation);
            insert.Parameters.AddWithValue("slot", slot);
            insert.Parameters.AddWithValue("itemId", checked((int)productId));
            insert.Parameters.AddWithValue("bound", bound);
            insert.Parameters.AddWithValue("stack", stack);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            left -= stack;
            written.Add(slot);
        }

        await AdvanceInventoryRevisionAsync(connection, transaction, characterId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            OperatorItemOutcome.Done,
            $"took {string.Join("; ", taken)}; product in slots {string.Join(',', written)}",
            written.Count);
    }

    public async Task<OperatorItemResult> MoveBagItemAsync(
        int characterId,
        short fromSlot,
        short toSlot,
        CancellationToken cancellationToken)
    {
        if (fromSlot is < 0 or >= KitBagSlots || toSlot is < 0 or >= KitBagSlots || fromSlot == toSlot)
        {
            return new(OperatorItemOutcome.InvalidValue, $"slots must differ and be 0..{KitBagSlots - 1}");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCharacterAsync(connection, transaction, characterId, cancellationToken))
        {
            return new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }

        var free = await FreeSlotsAsync(connection, transaction, characterId, cancellationToken);
        if (!free.Contains(toSlot))
        {
            return new(OperatorItemOutcome.Refused, $"bag slot {toSlot} is not empty");
        }

        await using var move = new NpgsqlCommand(
            """
            UPDATE public.character_items
            SET slot_index = @to, updated_at = now()
            WHERE user_id = @characterId AND item_location = @kitBag AND slot_index = @from
            RETURNING prop_id;
            """,
            connection,
            transaction);
        move.Parameters.AddWithValue("characterId", characterId);
        move.Parameters.AddWithValue("kitBag", KitBagLocation);
        move.Parameters.AddWithValue("from", fromSlot);
        move.Parameters.AddWithValue("to", toSlot);
        var itemId = await move.ExecuteScalarAsync(cancellationToken);
        if (itemId is null)
        {
            return new(OperatorItemOutcome.SlotEmpty, $"bag slot {fromSlot} is empty");
        }

        await AdvanceInventoryRevisionAsync(connection, transaction, characterId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OperatorItemOutcome.Done, $"moved item {itemId} slot {fromSlot} -> {toSlot}", 1);
    }

    public async Task<OperatorItemResult> SetProgressionAsync(
        int characterId,
        short? level,
        long? experience,
        int? talentPoints,
        int? zodiacEnergy,
        CancellationToken cancellationToken)
    {
        if (level is < 1 or > 200 || experience is < 0 or > uint.MaxValue ||
            talentPoints is < 0 || zodiacEnergy is < 0)
        {
            return new(OperatorItemOutcome.InvalidValue,
                "level 1..200, experience 0..4294967295, talentPoints and zodiacEnergy >= 0");
        }

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET fighter_job_lv = COALESCE(@level, fighter_job_lv),
                fighter_job_exp = COALESCE(@experience, CASE WHEN @level IS NULL THEN fighter_job_exp ELSE 0 END),
                "SkillPoint" = COALESCE(@talentPoints, "SkillPoint"),
                zodiac_energy = COALESCE(@zodiacEnergy, zodiac_energy),
                zodiac_energy_remainder_x100 = CASE WHEN @zodiacEnergy IS NULL THEN zodiac_energy_remainder_x100 ELSE 0 END,
                progression_reward_revision = progression_reward_revision + 1
            WHERE id = @id
            RETURNING fighter_job_lv, fighter_job_exp, "SkillPoint", zodiac_energy;
            """);
        command.Parameters.Add(new NpgsqlParameter<short?>("level", level) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Smallint });
        command.Parameters.Add(new NpgsqlParameter<long?>("experience", experience) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter<int?>("talentPoints", talentPoints) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter<int?>("zodiacEnergy", zodiacEnergy) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        command.Parameters.AddWithValue("id", characterId);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new(OperatorItemOutcome.Done,
                    $"level={reader.GetInt16(0)} experience={reader.GetInt64(1)} " +
                    $"talentPoints={reader.GetInt32(2)} zodiacEnergy={reader.GetInt32(3)}", 1)
                : new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }
        catch (PostgresException error)
        {
            return new(OperatorItemOutcome.Refused, error.MessageText);
        }
    }

    public async Task<OperatorItemResult> SetWalletAsync(
        int characterId,
        int? silver,
        int? gold,
        int? bindGold,
        CancellationToken cancellationToken)
    {
        if (silver is < 0 || gold is < 0 || bindGold is < 0)
        {
            return new(OperatorItemOutcome.InvalidValue, "silver, gold and bindGold must be 0..2147483647");
        }

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET "Money" = COALESCE(@silver, "Money"),
                "Stone" = COALESCE(@gold, "Stone"),
                "BindingGold" = COALESCE(@bindGold, "BindingGold"),
                wallet_revision = wallet_revision + 1
            WHERE id = @id
            RETURNING "Money", "Stone", "BindingGold";
            """);
        command.Parameters.Add(new NpgsqlParameter<int?>("silver", silver) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter<int?>("gold", gold) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter<int?>("bindGold", bindGold) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        command.Parameters.AddWithValue("id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(OperatorItemOutcome.Done,
                $"silver={reader.GetInt32(0)} gold={reader.GetInt32(1)} bindGold={reader.GetInt32(2)}", 1)
            : new(OperatorItemOutcome.CharacterNotFound, "character not found");
    }

    public async Task LogAsync(
        int actorAccountId,
        string actorCharacter,
        string command,
        string target,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var insert = _dataSource.CreateCommand(
            """
            INSERT INTO public.operator_log (actor_account_id, actor_character, command, target, detail)
            VALUES (@account, @character, @command, @target, @detail);
            """);
        insert.Parameters.AddWithValue("account", actorAccountId);
        insert.Parameters.AddWithValue("character", Truncate(actorCharacter, 64));
        insert.Parameters.AddWithValue("command", Truncate(command, 32));
        insert.Parameters.AddWithValue("target", Truncate(target, 128));
        insert.Parameters.AddWithValue("detail", detail);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static async Task<bool> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM public.character_base WHERE id = @id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<List<short>> FreeSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var used = new HashSet<short>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT slot_index FROM public.character_items
            WHERE user_id = @characterId AND item_location = @kitBag
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBag", KitBagLocation);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                used.Add(reader.GetInt16(0));
            }
        }

        var free = new List<short>();
        for (short slot = 0; slot < KitBagSlots; slot++)
        {
            if (!used.Contains(slot))
            {
                free.Add(slot);
            }
        }

        return free;
    }

    private static async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = inventory_revision + 1
            WHERE id = @id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The insertable columns of a table (no identity or generated columns).</summary>
    private static async Task<List<string>> InsertableColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
              AND is_identity = 'NO' AND is_generated = 'NEVER'
            ORDER BY ordinal_position;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>Copies one row, overriding some columns; returns the new id.</summary>
    private static async Task<long> CopyRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string keyColumn,
        long keyValue,
        IReadOnlyDictionary<string, object> overrides,
        CancellationToken cancellationToken)
    {
        var columns = await InsertableColumnsAsync(connection, transaction, table, cancellationToken);
        var sql = new StringBuilder();
        sql.Append("INSERT INTO public.").Append(table).Append(" (");
        sql.AppendJoin(", ", columns.Select(Quote));
        sql.Append(") SELECT ");
        sql.AppendJoin(", ", columns.Select(column =>
            overrides.ContainsKey(column) ? "@o_" + column : Quote(column)));
        sql.Append(" FROM public.").Append(table)
            .Append(" WHERE ").Append(Quote(keyColumn)).Append(" = @key")
            .Append(" RETURNING ").Append(Quote(keyColumn)).Append(';');
        await using var command = new NpgsqlCommand(sql.ToString(), connection, transaction);
        command.Parameters.AddWithValue("key", keyValue);
        foreach (var (column, value) in overrides)
        {
            command.Parameters.AddWithValue("o_" + column, value);
        }

        return await command.ExecuteScalarAsync(cancellationToken) is long id
            ? id
            : throw new InvalidOperationException($"{table} row {keyValue} was not copied.");
    }

    private static async Task CopyChildRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        long sourcePetId,
        long targetPetId,
        CancellationToken cancellationToken)
    {
        var columns = await InsertableColumnsAsync(connection, transaction, table, cancellationToken);
        if (columns.Count == 0)
        {
            return;
        }

        var sql =
            $"INSERT INTO public.{table} ({string.Join(", ", columns.Select(Quote))}) " +
            $"SELECT {string.Join(", ", columns.Select(c => c == "pet_id" ? "@target" : Quote(c)))} " +
            $"FROM public.{table} WHERE pet_id = @source;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("source", sourcePetId);
        command.Parameters.AddWithValue("target", targetPetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Start-up wiring; never stops the server.</summary>
internal static class OperatorRuntime
{
    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var store = new PostgresOperatorStore(NpgsqlDataSource.Create(connectionString));
            await store.EnsureAsync(cancellationToken);
            OperatorControls.Store = store;
            Godswar.Server.Infrastructure.Characters.CharacterIdentityRuntime.Initialize(connectionString);
            Godswar.Server.Infrastructure.Characters.CharacterClassChangeRuntime.Initialize(connectionString); // CLASSCHANGE
            Godswar.Server.Infrastructure.Characters.MarriageRuntime.Initialize(connectionString); // MARRIAGE
            Console.WriteLine("[operator] commands ready (operator_log ensured)");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                "[operator] start-up failed; mute/ban/item commands unavailable: " +
                error.Message);
        }
    }
}
