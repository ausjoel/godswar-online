using Godswar.Server.Application.Villagers;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

/// <summary>
/// VILLAGERS: storage for the Village Leader book re-issue and the Festival Envoy
/// holiday gifts. Only the Postgres store implements it; the handler checks for it.
/// </summary>
internal interface IVillagersStore
{
    Task<(VillageLeaderGrantStatus Status, IReadOnlyList<VillageLeaderBook> Granted, GameCharacter? Character)>
        ReissueVillageLeaderBooksAsync(
            int accountId,
            int characterId,
            IReadOnlyList<VillageLeaderBook> books,
            CancellationToken cancellationToken = default);

    Task<FestivalGiftDay?> ReadFestivalGiftDayAsync(
        DateOnly day,
        CancellationToken cancellationToken = default);

    Task<FestivalGiftDay?> ReadNextFestivalGiftDayAsync(
        DateOnly after,
        CancellationToken cancellationToken = default);

    Task<(FestivalGiftClaimStatus Status, GameCharacter? Character)> ClaimFestivalGiftAsync(
        int accountId,
        int characterId,
        FestivalGiftDay gift,
        CancellationToken cancellationToken = default);
}

internal sealed partial class PostgresGameStore : IVillagersStore
{
    private static int _villagersTablesReady;

    /// <summary>
    /// Re-issues the books the character has neither learned nor owns, all of them
    /// or none (one transaction, character row locked).
    /// </summary>
    public async Task<(VillageLeaderGrantStatus Status, IReadOnlyList<VillageLeaderBook> Granted, GameCharacter? Character)>
        ReissueVillageLeaderBooksAsync(
            int accountId,
            int characterId,
            IReadOnlyList<VillageLeaderBook> books,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockVillagerCharacterAsync(connection, transaction, accountId, characterId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return (VillageLeaderGrantStatus.CharacterNotFound, [], null);
        }

        var missing = new List<VillageLeaderBook>();
        await using (var probe = new NpgsqlCommand("""
            SELECT
                EXISTS (SELECT 1 FROM character_skills
                        WHERE user_id = @characterId AND skill_id = @skillId),
                EXISTS (SELECT 1 FROM character_items
                        WHERE user_id = @characterId AND prop_id = @bookId);
            """, connection, transaction))
        {
            foreach (var book in books)
            {
                probe.Parameters.Clear();
                probe.Parameters.AddWithValue("characterId", characterId);
                probe.Parameters.AddWithValue("skillId", book.SkillId);
                probe.Parameters.AddWithValue("bookId", book.BookItemId);
                await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken) &&
                    !reader.GetBoolean(0) &&
                    !reader.GetBoolean(1))
                {
                    missing.Add(book);
                }
            }
        }

        if (missing.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return (VillageLeaderGrantStatus.NothingMissing, [], null);
        }

        var freeSlots = await ReadFreeKitBagSlotsAsync(connection, transaction, characterId, cancellationToken);
        if (freeSlots.Count < missing.Count)
        {
            await transaction.CommitAsync(cancellationToken);
            return (VillageLeaderGrantStatus.InsufficientCapacity, [], null);
        }

        for (var index = 0; index < missing.Count; index++)
        {
            await InsertVillagerItemAsync(
                connection, transaction, characterId, freeSlots[index],
                missing[index].BookItemId, 1, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (
            VillageLeaderGrantStatus.Granted,
            missing,
            await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    public async Task<FestivalGiftDay?> ReadFestivalGiftDayAsync(
        DateOnly day,
        CancellationToken cancellationToken = default)
    {
        await EnsureVillagersTablesAsync(cancellationToken);
        return await ReadFestivalGiftDayAsync(
            "WHERE enabled AND gift_day = @day", day, cancellationToken);
    }

    public async Task<FestivalGiftDay?> ReadNextFestivalGiftDayAsync(
        DateOnly after,
        CancellationToken cancellationToken = default)
    {
        await EnsureVillagersTablesAsync(cancellationToken);
        return await ReadFestivalGiftDayAsync(
            "WHERE enabled AND gift_day > @day", after, cancellationToken);
    }

    /// <summary>
    /// Records the claim and inserts the item in one transaction. EXP and Talent are
    /// paid by the caller after the commit (the Lucky Gods order: a failure costs the
    /// player the prize rather than minting a second one).
    /// </summary>
    public async Task<(FestivalGiftClaimStatus Status, GameCharacter? Character)> ClaimFestivalGiftAsync(
        int accountId,
        int characterId,
        FestivalGiftDay gift,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gift);
        await EnsureVillagersTablesAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockVillagerCharacterAsync(connection, transaction, accountId, characterId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return (FestivalGiftClaimStatus.CharacterNotFound, null);
        }

        await using (var claimed = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM public.festival_gift_claims
                           WHERE character_id = @characterId AND gift_day = @day);
            """, connection, transaction))
        {
            claimed.Parameters.AddWithValue("characterId", characterId);
            claimed.Parameters.Add("day", NpgsqlDbType.Date).Value = gift.Day;
            if (await claimed.ExecuteScalarAsync(cancellationToken) is true)
            {
                await transaction.CommitAsync(cancellationToken);
                return (FestivalGiftClaimStatus.AlreadyClaimed, null);
            }
        }

        var hasItem = gift.ItemId > 0 && gift.ItemCount > 0;
        IReadOnlyList<short> freeSlots = [];
        if (hasItem)
        {
            freeSlots = await ReadFreeKitBagSlotsAsync(connection, transaction, characterId, cancellationToken);
            if (freeSlots.Count < 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return (FestivalGiftClaimStatus.InsufficientCapacity, null);
            }
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO public.festival_gift_claims (
                character_id, gift_day, festival_index, experience, talent_points,
                item_id, item_count)
            VALUES (@characterId, @day, @festival, @experience, @talent, @itemId, @itemCount);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("characterId", characterId);
            insert.Parameters.Add("day", NpgsqlDbType.Date).Value = gift.Day;
            insert.Parameters.AddWithValue("festival", (short)gift.FestivalIndex);
            insert.Parameters.AddWithValue("experience", gift.Experience);
            insert.Parameters.AddWithValue("talent", gift.TalentPoints);
            insert.Parameters.AddWithValue("itemId", gift.ItemId);
            insert.Parameters.AddWithValue("itemCount", (short)gift.ItemCount);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        if (hasItem)
        {
            await InsertVillagerItemAsync(
                connection, transaction, characterId, freeSlots[0],
                gift.ItemId, checked((short)gift.ItemCount), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (
            FestivalGiftClaimStatus.Granted,
            await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    private async Task<FestivalGiftDay?> ReadFestivalGiftDayAsync(
        string where,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT gift_day, festival_index, min_level, experience, talent_points,
                   COALESCE(item_id, 0), item_count
            FROM public.festival_gift_days
            {where}
            ORDER BY gift_day
            LIMIT 1;
            """, connection);
        command.Parameters.Add("day", NpgsqlDbType.Date).Value = day;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FestivalGiftDay(
            reader.GetFieldValue<DateOnly>(0),
            reader.GetInt16(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt16(6));
    }

    private static async Task<bool> LockVillagerCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT true
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<IReadOnlyList<short>> ReadFreeKitBagSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var occupied = new HashSet<int>();
        await using (var command = new NpgsqlCommand("""
            SELECT slot_index
            FROM character_items
            WHERE user_id = @characterId AND item_location = @kitBagLocation
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                occupied.Add(reader.GetInt16(0));
            }
        }

        var free = new List<short>();
        for (short slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            if (!occupied.Contains(slot))
            {
                free.Add(slot);
            }
        }
        return free;
    }

    // Same row shape as AddWishingPoolSkillBookAsync: bound, quality 1, grade 1.
    private static async Task InsertVillagerItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        int itemId,
        short stack,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code
            )
            VALUES (
                @characterId, @itemLocation, @slotIndex, @itemId,
                1, 1, 1, @stack, 0, 0
            );
            """, connection, transaction);
        insert.Parameters.AddWithValue("characterId", characterId);
        insert.Parameters.AddWithValue("itemLocation", ItemLocationKitBag);
        insert.Parameters.AddWithValue("slotIndex", slot);
        insert.Parameters.AddWithValue("itemId", itemId);
        insert.Parameters.AddWithValue("stack", stack);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Kit-bag slot {slot} changed while granting villager item {itemId}.");
        }
    }

    /// <summary>
    /// Creates the two festival tables on first use (idempotent, the server_rates
    /// pattern; no registered migration so an older binary still starts after a
    /// rollback). The generated gift calendar is seeded only when the table is
    /// created, so rows the admin edits or deletes stay that way.
    /// </summary>
    private async Task EnsureVillagersTablesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _villagersTablesReady) == 1)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
            VillagersSchemaSql.Ensure, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        Volatile.Write(ref _villagersTablesReady, 1);
    }
}

/// <summary>The DDL and seed; the same text is in Development/build/villagers/migration.sql.</summary>
internal static class VillagersSchemaSql
{
    public const string Ensure = """
        SELECT pg_advisory_xact_lock(hashtext('godswar.villagers.festival_tables'));
        DO $villagers$
        DECLARE
            created boolean := to_regclass('public.festival_gift_days') IS NULL;
        BEGIN
            CREATE TABLE IF NOT EXISTS public.festival_gift_days (
                gift_day       date PRIMARY KEY,
                festival_index smallint NOT NULL CHECK (festival_index BETWEEN 0 AND 14),
                enabled        boolean  NOT NULL DEFAULT true,
                min_level      integer  NOT NULL DEFAULT 30 CHECK (min_level BETWEEN 1 AND 300),
                experience     integer  NOT NULL DEFAULT 0 CHECK (experience BETWEEN 0 AND 2000000),
                talent_points  integer  NOT NULL DEFAULT 0 CHECK (talent_points BETWEEN 0 AND 2000000),
                item_id        integer  NULL CHECK (item_id IS NULL OR item_id > 0),
                item_count     smallint NOT NULL DEFAULT 1 CHECK (item_count BETWEEN 1 AND 99),
                note           text     NOT NULL DEFAULT 'generated'
            );
            CREATE TABLE IF NOT EXISTS public.festival_gift_claims (
                character_id   integer  NOT NULL,
                gift_day       date     NOT NULL,
                festival_index smallint NOT NULL,
                experience     integer  NOT NULL,
                talent_points  integer  NOT NULL,
                item_id        integer  NOT NULL,
                item_count     smallint NOT NULL,
                claimed_at     timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (character_id, gift_day)
            );
            IF created THEN
                -- GENERATED calendar and amounts (not retail): the dates are the
                -- client's own festival list (NF_L0_GIFT200-214) for Nov 2026 to
                -- Dec 2027, plus Dec 24/26 from the GodsArena 2024 notes (wiki-70).
                INSERT INTO public.festival_gift_days
                    (gift_day, festival_index, min_level, experience, talent_points, item_id, item_count, note)
                VALUES
                    ('2026-11-26', 12, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2026-12-24', 13, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2026-12-25', 13, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2026-12-26', 13, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2026-12-31', 14, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-01-01',  0, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-02-06',  1, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-02-14',  2, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-03-28',  4, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-04-01',  3, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-05-09',  5, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-06-12',  6, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-06-20',  7, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-07-04',  8, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-10-31',  9, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-11-01', 10, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-11-02', 11, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-11-25', 12, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-12-25', 13, 30, 100000, 2, 4165, 1, 'generated'),
                    ('2027-12-31', 14, 30, 100000, 2, 4165, 1, 'generated')
                ON CONFLICT (gift_day) DO NOTHING;
            END IF;
        END
        $villagers$;
        """;
}
