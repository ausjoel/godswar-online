using Npgsql;

namespace Godswar.Server.Infrastructure.EventsNpc;

/// <summary>
/// EVENTSNPC: the item, wallet and counter moves one event click makes, all inside the store's transaction.
/// The kit bag is character_items location 1, 96 slots, stack cap from the template's Overlap (the same rules
/// as PostgresZeusGiftStore). Grants are bound: IGG says the Lost Book items "cannot be traded", and the
/// manuals and scrolls carry BindType 1 in the client.
/// </summary>
internal sealed class EventsNpcTransaction
{
    private const short KitBagLocation = 1;
    private const int KitBagSlotCount = 96;
    private const int DefaultStackCap = 99;

    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly CancellationToken _token;
    private readonly DateOnly _day;

    public EventsNpcTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, int characterId,
        DateOnly day, CancellationToken token)
    {
        _connection = connection;
        _transaction = transaction;
        CharacterId = characterId;
        _day = day;
        _token = token;
        State = EventsNpcState.Fresh(day);
    }

    public int CharacterId { get; }

    public EventsNpcState State { get; set; }

    public bool RolledBack { get; private set; }

    /// <summary>Undo every move made so far (a full bag half way through a grant).</summary>
    public void Rollback() => RolledBack = true;

    internal async Task LoadStateAsync()
    {
        await using var command = Command("""
            SELECT day, elder_scrolls, incomplete_wishes, complete_wishes, races, race_started_at,
                   chain_step, chain_quest_id, sack_pending, reward_step
            FROM public.eventsnpc_character_state WHERE character_id = @c;
            """);
        await using var reader = await command.ExecuteReaderAsync(_token);
        if (!await reader.ReadAsync(_token))
        {
            return;
        }

        var started = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5);
        var chainStep = reader.GetInt16(6);
        var chainQuest = reader.GetInt64(7);
        var sackPending = reader.GetBoolean(8);
        var rewardStep = reader.GetInt16(9);
        // A row from an earlier realm day keeps only what is not a day counter.
        State = reader.GetFieldValue<DateOnly>(0) == _day
            ? new EventsNpcState(_day, reader.GetInt16(1), reader.GetInt16(2), reader.GetInt16(3),
                reader.GetInt16(4), started, chainStep, chainQuest, sackPending, rewardStep)
            : EventsNpcState.Fresh(_day) with
            {
                RaceStartedAt = started, ChainStep = chainStep, ChainQuestId = chainQuest, SackPending = sackPending,
                RewardStep = rewardStep
            };
    }

    internal async Task SaveStateAsync()
    {
        await using var command = Command("""
            INSERT INTO public.eventsnpc_character_state (character_id, day, elder_scrolls, incomplete_wishes,
                complete_wishes, races, race_started_at, chain_step, chain_quest_id, sack_pending, reward_step,
                updated_at)
            VALUES (@c, @day, @elder, @inc, @comp, @races, @started, @step, @quest, @sack, @owed, now())
            ON CONFLICT (character_id) DO UPDATE SET day = EXCLUDED.day, elder_scrolls = EXCLUDED.elder_scrolls,
                incomplete_wishes = EXCLUDED.incomplete_wishes, complete_wishes = EXCLUDED.complete_wishes,
                races = EXCLUDED.races, race_started_at = EXCLUDED.race_started_at,
                chain_step = EXCLUDED.chain_step, chain_quest_id = EXCLUDED.chain_quest_id,
                sack_pending = EXCLUDED.sack_pending, reward_step = EXCLUDED.reward_step, updated_at = now();
            """);
        command.Parameters.AddWithValue("day", State.Day);
        command.Parameters.AddWithValue("elder", (short)State.ElderScrolls);
        command.Parameters.AddWithValue("inc", (short)State.IncompleteWishes);
        command.Parameters.AddWithValue("comp", (short)State.CompleteWishes);
        command.Parameters.AddWithValue("races", (short)State.Races);
        command.Parameters.Add(new NpgsqlParameter("started", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = State.RaceStartedAt is { } at ? at.ToUniversalTime() : DBNull.Value
        });
        command.Parameters.AddWithValue("step", (short)State.ChainStep);
        command.Parameters.AddWithValue("quest", State.ChainQuestId);
        command.Parameters.AddWithValue("sack", State.SackPending);
        command.Parameters.AddWithValue("owed", (short)State.RewardStep);
        await command.ExecuteNonQueryAsync(_token);
    }

    /// <summary>How many of an item the bag holds.</summary>
    public async Task<int> CountAsync(int itemId)
    {
        await using var command = Command(
            "SELECT COALESCE(SUM(stack), 0)::int FROM public.character_items " +
            "WHERE user_id = @c AND item_location = @l AND prop_id = @i;");
        command.Parameters.AddWithValue("l", KitBagLocation);
        command.Parameters.AddWithValue("i", itemId);
        return (int)(await command.ExecuteScalarAsync(_token))!;
    }

    /// <summary>Takes <paramref name="count"/> of an item, lowest slot first. All or nothing.</summary>
    public async Task<bool> ConsumeAsync(int itemId, int count)
    {
        if (count <= 0)
        {
            return true;
        }

        var stacks = new List<(short Slot, short Stack)>();
        await using (var read = Command(
            "SELECT slot_index, stack FROM public.character_items WHERE user_id = @c AND item_location = @l " +
            "AND prop_id = @i ORDER BY slot_index FOR UPDATE;"))
        {
            read.Parameters.AddWithValue("l", KitBagLocation);
            read.Parameters.AddWithValue("i", itemId);
            await using var reader = await read.ExecuteReaderAsync(_token);
            while (await reader.ReadAsync(_token))
            {
                stacks.Add((reader.GetInt16(0), reader.GetInt16(1)));
            }
        }

        if (stacks.Sum(static stack => (int)stack.Stack) < count)
        {
            return false;
        }

        var remaining = count;
        foreach (var (slot, stack) in stacks)
        {
            if (remaining <= 0)
            {
                break;
            }

            var taken = Math.Min(remaining, (int)stack);
            remaining -= taken;
            await using var write = Command(taken == stack
                ? "DELETE FROM public.character_items WHERE user_id = @c AND item_location = @l AND slot_index = @s;"
                : "UPDATE public.character_items SET stack = stack - @t WHERE user_id = @c AND item_location = @l " +
                  "AND slot_index = @s;");
            write.Parameters.AddWithValue("l", KitBagLocation);
            write.Parameters.AddWithValue("s", slot);
            write.Parameters.AddWithValue("t", (short)taken);
            await write.ExecuteNonQueryAsync(_token);
        }

        return true;
    }

    /// <summary>
    /// Adds items, topping up stacks of the same binding first. False (nothing usable) when the bag is full.
    /// <paramref name="bound"/>: true/false forces the binding; null follows the template's BindType (1 = bound).
    /// </summary>
    public async Task<bool> GrantAsync(int itemId, int count, bool? bound = true, int? stackCap = null)
    {
        if (count <= 0)
        {
            return true;
        }

        var cap = DefaultStackCap;
        var templateBound = false;
        await using (var capCommand = Command(
            "SELECT stats ->> 'Overlap', stats ->> 'BindType' FROM public.item_templates WHERE id = @i;"))
        {
            capCommand.Parameters.AddWithValue("i", itemId);
            await using var reader = await capCommand.ExecuteReaderAsync(_token);
            if (await reader.ReadAsync(_token))
            {
                if (!reader.IsDBNull(0) && int.TryParse(reader.GetString(0), out var parsed) && parsed > 0)
                {
                    cap = Math.Min(parsed, DefaultStackCap); // ROUND3: the admin's rule, no stack above 99 (Overlap above 99 is ignored)
                }

                templateBound = !reader.IsDBNull(1) && reader.GetString(1).Trim() == "1";
            }
        }

        if (stackCap is > 0)
        {
            cap = Math.Min(cap, stackCap.Value); // PROF: the craft path never stacks above 99
        }

        var binding = (bound ?? templateBound) ? 1 : 0;

        var occupied = new HashSet<short>();
        var same = new List<(short Slot, short Stack)>();
        await using (var read = Command(
            "SELECT slot_index, prop_id, stack, bound FROM public.character_items WHERE user_id = @c " +
            "AND item_location = @l ORDER BY slot_index FOR UPDATE;"))
        {
            read.Parameters.AddWithValue("l", KitBagLocation);
            await using var reader = await read.ExecuteReaderAsync(_token);
            while (await reader.ReadAsync(_token))
            {
                var slot = reader.GetInt16(0);
                occupied.Add(slot);
                if (reader.GetInt32(1) == itemId && Convert.ToInt32(reader.GetValue(3)) == binding)
                {
                    same.Add((slot, reader.GetInt16(2)));
                }
            }
        }

        var remaining = count;
        foreach (var (slot, stack) in same)
        {
            if (remaining <= 0 || stack >= cap)
            {
                continue;
            }

            var added = Math.Min(remaining, cap - stack);
            remaining -= added;
            await using var update = Command(
                "UPDATE public.character_items SET stack = stack + @a WHERE user_id = @c AND item_location = @l " +
                "AND slot_index = @s;");
            update.Parameters.AddWithValue("a", (short)added);
            update.Parameters.AddWithValue("l", KitBagLocation);
            update.Parameters.AddWithValue("s", slot);
            await update.ExecuteNonQueryAsync(_token);
        }

        for (short slot = 0; remaining > 0 && slot < KitBagSlotCount; slot++)
        {
            if (!occupied.Add(slot))
            {
                continue;
            }

            var stack = Math.Min(remaining, cap);
            remaining -= stack;
            await using var insert = Command("""
                INSERT INTO public.character_items (user_id, item_location, slot_index, prop_id, item_quality,
                    item_grade, bound, stack, item_exp, holy_suit_code)
                VALUES (@c, @l, @s, @i, 1, 1, @b, @n, 0, 0);
                """);
            insert.Parameters.AddWithValue("b", (short)binding);
            insert.Parameters.AddWithValue("l", KitBagLocation);
            insert.Parameters.AddWithValue("s", slot);
            insert.Parameters.AddWithValue("i", itemId);
            insert.Parameters.AddWithValue("n", (short)stack);
            await insert.ExecuteNonQueryAsync(_token);
        }

        return remaining <= 0;
    }

    /// <summary>Credits B-Gold ("BindingGold"), as the mentor patch does. Returns the new balance.</summary>
    public async Task<int> AddBindingGoldAsync(int amount)
    {
        await using var command = Command("""
            UPDATE public.character_base
            SET "BindingGold" = LEAST("BindingGold"::bigint + @n, 2147483647)::integer,
                wallet_revision = wallet_revision + 1
            WHERE id = @c RETURNING "BindingGold";
            """);
        command.Parameters.AddWithValue("n", (long)amount);
        return (int)(await command.ExecuteScalarAsync(_token))!;
    }

    /// <summary>Credits whole Talent Points ("SkillPoint"), like a Talent Stone. Returns the new total.</summary>
    public async Task<int> AddTalentPointsAsync(int points)
    {
        await using var command = Command("""
            UPDATE public.character_base
            SET "SkillPoint" = LEAST("SkillPoint"::bigint + @n, 2147483647)::integer,
                progression_reward_revision = progression_reward_revision + 1
            WHERE id = @c RETURNING "SkillPoint";
            """);
        command.Parameters.AddWithValue("n", (long)points);
        return (int)(await command.ExecuteScalarAsync(_token))!;
    }

    /// <summary>
    /// W3 seasons mid-autumn: adds whole Zodiac energy up to the storage limit of the character's Zodiac level
    /// (ZodiacEnergyCatalog). Returns the energy actually added, in hundredths (0 = the store is full). The online
    /// accrual re-reads the row FOR UPDATE, so it adds on top of this rather than overwriting it.
    /// </summary>
    public async Task<int> AddZodiacEnergyAsync(int energy)
    {
        if (energy <= 0) return 0;
        int level, current, remainder;
        await using (var read = Command(
            "SELECT zodiac_level, zodiac_energy, zodiac_energy_remainder_x100 FROM public.character_base " +
            "WHERE id = @c FOR UPDATE;"))
        await using (var reader = await read.ExecuteReaderAsync(_token))
        {
            if (!await reader.ReadAsync(_token)) return 0;
            level = reader.GetInt16(0);
            current = reader.GetInt32(1);
            remainder = reader.GetInt32(2);
        }

        var capacityX100 = (long)Godswar.Server.State.ZodiacEnergyCatalog.GetStorageLimit(Math.Clamp(level, 1, 30)) * 100L;
        var beforeX100 = (long)current * 100L + remainder;
        var afterX100 = Math.Min(capacityX100, beforeX100 + (long)energy * 100L);
        if (afterX100 <= beforeX100) return 0;
        await using var command = Command("""
            UPDATE public.character_base
            SET zodiac_energy = @e, zodiac_energy_remainder_x100 = @r,
                progression_reward_revision = progression_reward_revision + 1
            WHERE id = @c;
            """);
        command.Parameters.AddWithValue("e", checked((int)(afterX100 / 100L)));
        command.Parameters.AddWithValue("r", checked((int)(afterX100 % 100L)));
        await command.ExecuteNonQueryAsync(_token);
        return checked((int)(afterX100 - beforeX100));
    }

    public async Task RecordFinishAsync(DateTimeOffset finishedAt, long milliseconds, int bindingGold)
    {
        await using var command = Command(
            "INSERT INTO public.eventsnpc_marathon_finishes (character_id, finished_at, milliseconds, binding_gold) " +
            "VALUES (@c, @f, @m, @b);");
        command.Parameters.AddWithValue("f", finishedAt.ToUniversalTime());
        command.Parameters.AddWithValue("m", milliseconds);
        command.Parameters.AddWithValue("b", bindingGold);
        await command.ExecuteNonQueryAsync(_token);
    }

    // FINALISE2 (P4): wallet moves, daily counters and one raw scalar for the item/NPC handlers
    // (GameClientHandler.Finalise2*.cs). "Stone" is the character's Gold, "Money" its Silver.

    /// <summary>FINALISE2: takes Gold ("Stone"). Null (nothing changed) when the character has less.</summary>
    /// <summary>W2: spends Silver ("Money") if the character has it. Returns the new balance, or null.</summary>
    public async Task<int?> TrySpendSilverAsync(int amount)
    {
        await using var command = Command("""
            UPDATE public.character_base SET "Money" = "Money" - @n, wallet_revision = wallet_revision + 1
            WHERE id = @c AND "Money" >= @n RETURNING "Money";
            """);
        command.Parameters.AddWithValue("n", amount);
        return await command.ExecuteScalarAsync(_token) is int left ? left : null;
    }

    public async Task<int?> TrySpendGoldAsync(int amount)
    {
        await using var command = Command("""
            UPDATE public.character_base SET "Stone" = "Stone" - @n, wallet_revision = wallet_revision + 1
            WHERE id = @c AND "Stone" >= @n RETURNING "Stone";
            """);
        command.Parameters.AddWithValue("n", amount);
        return await command.ExecuteScalarAsync(_token) is int left ? left : null;
    }

    /// <summary>FINALISE2: credits Silver ("Money"), capped at int.MaxValue. Returns the new balance.</summary>
    public async Task<int> AddSilverAsync(int amount)
    {
        await using var command = Command("""
            UPDATE public.character_base
            SET "Money" = LEAST("Money"::bigint + @n, 2147483647)::integer, wallet_revision = wallet_revision + 1
            WHERE id = @c RETURNING "Money";
            """);
        command.Parameters.AddWithValue("n", (long)amount);
        return (int)(await command.ExecuteScalarAsync(_token))!;
    }

    /// <summary>FINALISE2: how often <paramref name="counter"/> was used today (realm day).</summary>
    public async Task<int> DailyUsedAsync(string counter)
    {
        await using var command = Command(
            "SELECT used FROM public.finalise2_npc_daily WHERE character_id = @c AND day = @d AND counter = @k;");
        command.Parameters.AddWithValue("d", _day);
        command.Parameters.AddWithValue("k", counter);
        return await command.ExecuteScalarAsync(_token) is int used ? used : 0;
    }

    /// <summary>FINALISE2: adds <paramref name="count"/> to today's counter. Returns the new total.</summary>
    public async Task<int> DailyAddAsync(string counter, int count)
    {
        await using var command = Command("""
            INSERT INTO public.finalise2_npc_daily (character_id, day, counter, used) VALUES (@c, @d, @k, @n)
            ON CONFLICT (character_id, day, counter) DO UPDATE SET used = finalise2_npc_daily.used + EXCLUDED.used,
                updated_at = now()
            RETURNING used;
            """);
        command.Parameters.AddWithValue("d", _day);
        command.Parameters.AddWithValue("k", counter);
        command.Parameters.AddWithValue("n", count);
        return (int)(await command.ExecuteScalarAsync(_token))!;
    }

    /// <summary>FINALISE2: one parameterised scalar on this transaction (@c = the character id).</summary>
    public async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(_token);
    }

    /// <summary>W2: the kit-bag item in one slot (row id, item, bound, stack), locked; null when empty.</summary>
    public async Task<(long Id, int ItemId, bool Bound, short Stack)?> ReadSlotAsync(int slot)
    {
        await using var read = Command(
            "SELECT id, prop_id, bound, stack FROM public.character_items WHERE user_id = @c AND item_location = @l " +
            "AND slot_index = @s FOR UPDATE;");
        read.Parameters.AddWithValue("l", KitBagLocation);
        read.Parameters.AddWithValue("s", (short)slot);
        await using var reader = await read.ExecuteReaderAsync(_token);
        if (!await reader.ReadAsync(_token)) return null;
        return (reader.GetInt64(0), reader.GetInt32(1), Convert.ToInt32(reader.GetValue(2)) != 0, reader.GetInt16(3));
    }

    /// <summary>
    /// PAN CHAIN slice 2: the kit-bag item in one slot with its quality (item_quality, 1 = Common, 3 = Delicate in the
    /// client ItemColor.xml) and the number of set options (non-null attribute1-5: the inventory writer stores an empty append as NULL and any id >= 0, 0 included, as set); locked; null when empty.
    /// </summary>
    public async Task<(long Id, int ItemId, short Quality, int Options)?> ReadEquipmentSlotAsync(int slot)
    {
        await using var read = Command(
            "SELECT id, prop_id, item_quality, " +
            "(CASE WHEN attribute1 IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN attribute2 IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN attribute3 IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN attribute4 IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN attribute5 IS NOT NULL THEN 1 ELSE 0 END) " +
            "FROM public.character_items WHERE user_id = @c AND item_location = @l AND slot_index = @s FOR UPDATE;");
        read.Parameters.AddWithValue("l", KitBagLocation);
        read.Parameters.AddWithValue("s", (short)slot);
        await using var reader = await read.ExecuteReaderAsync(_token);
        if (!await reader.ReadAsync(_token)) return null;
        return (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt16(2), reader.GetInt32(3));
    }

    /// <summary>W2: turn one owned item row into another item (same slot, all other columns kept).</summary>
    public async Task ReplaceItemAsync(long rowId, int newItemId, bool bound)
    {
        await using var write = Command(
            "UPDATE public.character_items SET prop_id = @i, bound = @b, updated_at = now() WHERE id = @id AND user_id = @c;");
        write.Parameters.AddWithValue("i", newItemId);
        write.Parameters.AddWithValue("b", bound ? (short)1 : (short)0);
        write.Parameters.AddWithValue("id", rowId);
        if (await write.ExecuteNonQueryAsync(_token) != 1) throw new InvalidOperationException("item row changed");
    }

    /// <summary>W2: remove one owned item row entirely.</summary>
    public async Task DeleteItemAsync(long rowId)
    {
        await using var write = Command("DELETE FROM public.character_items WHERE id = @id AND user_id = @c;");
        write.Parameters.AddWithValue("id", rowId);
        if (await write.ExecuteNonQueryAsync(_token) != 1) throw new InvalidOperationException("item row changed");
    }

    private NpgsqlCommand Command(string sql)
    {
        var command = new NpgsqlCommand(sql, _connection, _transaction);
        command.Parameters.AddWithValue("c", CharacterId);
        return command;
    }
}
