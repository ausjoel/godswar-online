using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

/// <summary>MARRIAGE: one couple, engaged or married (ended rows are history).</summary>
internal sealed record MarriageRow(
    long Id,
    int GroomId,
    int BrideId,
    string Status,
    DateTimeOffset EngagedAt,
    DateTimeOffset? CeremonySlot,
    DateTimeOffset? MarriedAt,
    bool GroomGiftClaimed,
    bool BrideGiftClaimed,
    bool GroomNecklaceClaimed,
    bool BrideNecklaceClaimed)
{
    public const string Engaged = "engaged";
    public const string Married = "married";

    public bool IsMarried => Status == Married;

    public int PartnerOf(int characterId) => characterId == GroomId ? BrideId : GroomId;
}

internal readonly record struct MarriageWallet(int Silver, int Gold, int Reputation);

internal enum MarriageOutcome
{
    Done,
    NotFound,
    Stale,
    AlreadyLinked,
    NotEnoughSilver,
    NotEnoughGold,
    SlotTaken,
    AlreadyBooked,
    AlreadyClaimed,
    BagFull,
    CannotPayFine,
    NecklaceMissing
}

internal readonly record struct MarriageResult(
    MarriageOutcome Outcome,
    MarriageRow? Row = null,
    MarriageWallet? Wallet = null);

internal enum MarriageEndKind
{
    MutualEngagement,
    ForcedEngagement,
    MutualDivorce,
    ForcedDivorce
}

internal interface IMarriageStore
{
    Task EnsureAsync(CancellationToken cancellationToken);
    Task<MarriageRow?> GetActiveAsync(int characterId, CancellationToken cancellationToken);
    Task<DateTimeOffset?> LastOnlineAsync(int characterId, CancellationToken cancellationToken);
    Task<bool> HasBagItemAsync(int characterId, int itemId, CancellationToken cancellationToken);
    Task<MarriageWallet> WalletAsync(int characterId, CancellationToken cancellationToken);
    Task<MarriageResult> EngageAsync(int groomId, int brideId, int silver, CancellationToken cancellationToken);
    Task<MarriageResult> BookAsync(long marriageId, int groomId, DateTimeOffset slotUtc, int gold, CancellationToken cancellationToken);
    Task<MarriageResult> AbandonSlotAsync(long marriageId, int groomId, CancellationToken cancellationToken);
    Task<MarriageResult> MarryAsync(long marriageId, bool necklaceRequired, CancellationToken cancellationToken);
    Task<MarriageResult> ClaimGiftAsync(long marriageId, int characterId, CancellationToken cancellationToken);
    Task<MarriageResult> ClaimNecklaceAsync(int characterId, CancellationToken cancellationToken);
    Task<MarriageResult> EndAsync(
        long marriageId, int initiatorId, MarriageEndKind kind, MarriageFine fine, CancellationToken cancellationToken);
}

internal static class MarriageControls
{
    public static IMarriageStore? Store { get; set; }
}

/// <summary>
/// MARRIAGE: every state change is one transaction that locks both characters' rows in id order, re-checks
/// the rule on the locked rows, moves the money, writes the couple row, grants or removes the titles
/// (<c>character_title_grants</c>, <c>granted_by = 'marriage'</c>) and logs to <c>operator_log</c>
/// (command <c>marriage</c>). The tables are created on first use with idempotent DDL (the same SQL is
/// Development/build/marriage/migration.sql), so no registered schema migration pins the binary.
/// </summary>
internal sealed class PostgresMarriageStore(NpgsqlDataSource dataSource) : IMarriageStore
{
    internal const string GrantedBy = "marriage";
    internal const string LogCommand = "marriage";
    private const short KitBagLocation = 1;
    private const int KitBagSlots = 96;

    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS public.character_marriages (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            groom_id integer REFERENCES public.character_base(id) ON DELETE SET NULL,
            bride_id integer REFERENCES public.character_base(id) ON DELETE SET NULL,
            status text NOT NULL CHECK (status IN ('engaged', 'married', 'ended')),
            engaged_at timestamptz NOT NULL DEFAULT now(),
            engagement_silver integer NOT NULL DEFAULT 0,
            ceremony_slot timestamptz,
            ceremony_booked_at timestamptz,
            booking_gold integer NOT NULL DEFAULT 0,
            married_at timestamptz,
            groom_necklace_at timestamptz,
            bride_necklace_at timestamptz,
            groom_gift_at timestamptz,
            bride_gift_at timestamptz,
            ended_at timestamptz,
            end_kind text CHECK (end_kind IS NULL OR end_kind IN
                ('mutual-engagement', 'forced-engagement', 'mutual-divorce', 'forced-divorce')),
            ended_by integer,
            end_fine_reputation integer NOT NULL DEFAULT 0,
            end_fine_silver integer NOT NULL DEFAULT 0,
            CHECK (groom_id <> bride_id),
            CHECK ((status = 'ended') = (ended_at IS NOT NULL)),
            CHECK (status <> 'married' OR married_at IS NOT NULL)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_character_marriages_active_groom
            ON public.character_marriages (groom_id) WHERE status <> 'ended';
        CREATE UNIQUE INDEX IF NOT EXISTS ux_character_marriages_active_bride
            ON public.character_marriages (bride_id) WHERE status <> 'ended';
        CREATE UNIQUE INDEX IF NOT EXISTS ux_character_marriages_ceremony_slot
            ON public.character_marriages (ceremony_slot) WHERE status = 'engaged' AND ceremony_slot IS NOT NULL;
        -- REVIEW FIX 1: a character that is deleted (tombstoned) or hard-deleted while engaged or married ends
        -- the couple in the same transaction: forced end, no fine, ended_by = the deleted character, and the
        -- 'marriage' titles (1401/1402) are stripped. Restoring the character does not revive the couple.
        CREATE OR REPLACE FUNCTION public.character_marriages_end_on_delete() RETURNS trigger
        LANGUAGE plpgsql AS $marriage$
        DECLARE
            couple record;
            partner integer;
        BEGIN
            FOR couple IN
                UPDATE public.character_marriages
                SET status = 'ended', ended_at = now(),
                    end_kind = CASE WHEN status = 'married' THEN 'forced-divorce' ELSE 'forced-engagement' END,
                    ended_by = OLD.id, end_fine_reputation = 0, end_fine_silver = 0
                WHERE status <> 'ended' AND (groom_id = OLD.id OR bride_id = OLD.id)
                RETURNING id, groom_id, bride_id, end_kind
            LOOP
                partner := CASE WHEN couple.groom_id = OLD.id THEN couple.bride_id ELSE couple.groom_id END;
                -- On a hard DELETE the deleted row's own grants go with it (FK cascade); touching that row
                -- again from here would abort the DELETE, so only the partner's grants are removed then.
                DELETE FROM public.character_title_grants
                WHERE title_id IN (1401, 1402) AND granted_by = 'marriage'
                  AND (character_id = partner OR (TG_OP = 'UPDATE' AND character_id = OLD.id));
                INSERT INTO public.operator_log (actor_account_id, actor_character, command, target, detail)
                VALUES (OLD.account_id, OLD.name, 'marriage', partner::text,
                        'ended couple=' || couple.id || ' kind=' || couple.end_kind ||
                        ' by=' || OLD.id || ' reason=character-deleted fine-rep=0 fine-silver=0');
            END LOOP;
            IF TG_OP = 'DELETE' THEN
                RETURN OLD;
            END IF;
            RETURN NULL;
        END;
        $marriage$;
        CREATE OR REPLACE TRIGGER character_marriages_end_on_tombstone
            AFTER UPDATE OF lifecycle_state ON public.character_base
            FOR EACH ROW WHEN (OLD.lifecycle_state = 'active' AND NEW.lifecycle_state IS DISTINCT FROM 'active')
            EXECUTE FUNCTION public.character_marriages_end_on_delete();
        CREATE OR REPLACE TRIGGER character_marriages_end_on_hard_delete
            BEFORE DELETE ON public.character_base
            FOR EACH ROW WHEN (OLD.lifecycle_state = 'active')
            EXECUTE FUNCTION public.character_marriages_end_on_delete();
        """;

    /// <summary>
    /// REVIEW FIX 1: "last seen" for the 15-day rule uses only the character's own timestamps
    /// (<c>zodiac_last_online_at</c> = end of its last online interval, <c>"LastLogin_time"</c> = creation
    /// floor), never the account's, so playing another character on the same account does not reset it.
    /// A character that is not active (deleted) returns NULL, which <see cref="MarriagePolicy.IsAbsent"/>
    /// treats as absent: a free forced end.
    /// </summary>
    internal const string LastOnlineSql = """
        SELECT CASE WHEN cb.lifecycle_state = 'active'
                    THEN GREATEST(cb.zodiac_last_online_at, cb."LastLogin_time") END
        FROM public.character_base cb
        WHERE cb.id = @id;
        """;

    private const string RowColumns = """
        id, groom_id, bride_id, status, engaged_at, ceremony_slot, married_at,
        groom_gift_at IS NOT NULL, bride_gift_at IS NOT NULL,
        groom_necklace_at IS NOT NULL, bride_necklace_at IS NOT NULL
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));
    /// <summary>
    /// Creates the table when it is missing. Checked on every operation (one catalog lookup) rather than
    /// cached, so an undo.sql run while the server is up cannot leave a stale "already created" flag.
    /// </summary>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('public.character_marriages') IS NOT NULL AND " +
            "EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'character_marriages_end_on_tombstone');", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
            {
                await using var create = new NpgsqlCommand(SchemaSql, connection);
                await create.ExecuteNonQueryAsync(cancellationToken);
                Console.WriteLine("[marriage] created character_marriages (and its delete triggers) on first use");
            }
        }
    }

    public async Task<MarriageRow?> GetActiveAsync(int characterId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadActiveAsync(connection, null, characterId, forUpdate: false, cancellationToken);
    }

    public async Task<DateTimeOffset?> LastOnlineAsync(int characterId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(LastOnlineSql, connection);
        command.Parameters.AddWithValue("id", characterId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
            DateTimeOffset offset => offset,
            _ => null
        };
    }

    public async Task<bool> HasBagItemAsync(int characterId, int itemId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await CountBagItemAsync(connection, null, characterId, itemId, cancellationToken) > 0;
    }

    public async Task<MarriageWallet> WalletAsync(int characterId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadWalletAsync(connection, null, characterId, cancellationToken) ?? default;
    }

    public async Task<MarriageResult> EngageAsync(
        int groomId, int brideId, int silver, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var genders = await LockAsync(connection, transaction, [groomId, brideId], cancellationToken);
        if (genders.Count != 2)
        {
            return new(MarriageOutcome.NotFound);
        }

        if (genders[groomId] != "male" || genders[brideId] != "female")
        {
            return new(MarriageOutcome.Stale);
        }

        if (await ReadActiveAsync(connection, transaction, groomId, false, cancellationToken) is not null ||
            await ReadActiveAsync(connection, transaction, brideId, false, cancellationToken) is not null)
        {
            return new(MarriageOutcome.AlreadyLinked);
        }

        if (!await DebitAsync(connection, transaction, groomId, "\"Money\"", silver, cancellationToken))
        {
            return new(MarriageOutcome.NotEnoughSilver);
        }

        long id;
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO public.character_marriages (groom_id, bride_id, status, engagement_silver)
            VALUES (@groom, @bride, 'engaged', @silver) RETURNING id;
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("groom", groomId);
            insert.Parameters.AddWithValue("bride", brideId);
            insert.Parameters.AddWithValue("silver", silver);
            id = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        await GrantTitleAsync(connection, transaction, [groomId, brideId], MarriagePolicy.EngagedTitleId, cancellationToken);
        await LogAsync(connection, transaction, groomId, brideId,
            $"engaged couple={id} groom={groomId} bride={brideId} silver={silver}", cancellationToken);
        var row = await ReadActiveAsync(connection, transaction, groomId, false, cancellationToken);
        var wallet = await ReadWalletAsync(connection, transaction, groomId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MarriageOutcome.Done, row, wallet);
    }

    public async Task<MarriageResult> BookAsync(
        long marriageId, int groomId, DateTimeOffset slotUtc, int gold, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, [groomId], cancellationToken);
        var row = await ReadActiveAsync(connection, transaction, groomId, true, cancellationToken);
        if (row is null || row.Id != marriageId || row.IsMarried || row.GroomId != groomId)
        {
            return new(MarriageOutcome.Stale);
        }

        if (row.CeremonySlot is { } booked && booked.AddHours(1) > DateTimeOffset.UtcNow)
        {
            return new(MarriageOutcome.AlreadyBooked, row);
        }

        await using (var taken = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM public.character_marriages
                           WHERE status = 'engaged' AND ceremony_slot = @slot AND id <> @id);
            """,
            connection,
            transaction))
        {
            taken.Parameters.AddWithValue("slot", slotUtc);
            taken.Parameters.AddWithValue("id", marriageId);
            if (await taken.ExecuteScalarAsync(cancellationToken) is true)
            {
                return new(MarriageOutcome.SlotTaken, row);
            }
        }

        if (!await DebitAsync(connection, transaction, groomId, "\"Stone\"", gold, cancellationToken))
        {
            return new(MarriageOutcome.NotEnoughGold, row);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_marriages
            SET ceremony_slot = @slot, ceremony_booked_at = now(), booking_gold = booking_gold + @gold
            WHERE id = @id;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("slot", slotUtc);
            update.Parameters.AddWithValue("gold", gold);
            update.Parameters.AddWithValue("id", marriageId);
            try
            {
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return new(MarriageOutcome.SlotTaken, row);
            }
        }

        await LogAsync(connection, transaction, groomId, row.BrideId,
            $"booked couple={marriageId} slot={slotUtc:O} gold={gold}", cancellationToken);
        row = await ReadActiveAsync(connection, transaction, groomId, false, cancellationToken);
        var wallet = await ReadWalletAsync(connection, transaction, groomId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MarriageOutcome.Done, row, wallet);
    }

    public async Task<MarriageResult> AbandonSlotAsync(long marriageId, int groomId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadActiveAsync(connection, transaction, groomId, true, cancellationToken);
        if (row is null || row.Id != marriageId || row.IsMarried || row.CeremonySlot is null)
        {
            return new(MarriageOutcome.Stale, row);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE public.character_marriages SET ceremony_slot = NULL, ceremony_booked_at = NULL WHERE id = @id;",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("id", marriageId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await LogAsync(connection, transaction, groomId, row.BrideId,
            $"slot abandoned couple={marriageId} slot={row.CeremonySlot:O}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MarriageOutcome.Done, row with { CeremonySlot = null });
    }

    public async Task<MarriageResult> MarryAsync(long marriageId, bool necklaceRequired, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadByIdAsync(connection, transaction, marriageId, cancellationToken);
        if (row is null)
        {
            return new(MarriageOutcome.Stale);
        }

        await LockAsync(connection, transaction, [row.GroomId, row.BrideId], cancellationToken);
        row = await ReadByIdAsync(connection, transaction, marriageId, cancellationToken);
        if (row is null || row.Status != MarriageRow.Engaged ||
            MarriagePolicy.SlotState(row.CeremonySlot, DateTimeOffset.UtcNow) != MarriageSlotState.Open)
        {
            return new(MarriageOutcome.Stale, row);
        }

        if (necklaceRequired &&
            (await CountBagItemAsync(connection, transaction, row.GroomId, MarriagePolicy.WhelkNecklaceItemId, cancellationToken) == 0 ||
             await CountBagItemAsync(connection, transaction, row.BrideId, MarriagePolicy.WhelkNecklaceItemId, cancellationToken) == 0))
        {
            return new(MarriageOutcome.NecklaceMissing, row);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE public.character_marriages SET status = 'married', married_at = now() WHERE id = @id;",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("id", marriageId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await RemoveTitleAsync(connection, transaction, [row.GroomId, row.BrideId], MarriagePolicy.EngagedTitleId, cancellationToken);
        await GrantTitleAsync(connection, transaction, [row.GroomId, row.BrideId], MarriagePolicy.NewlyWedTitleId, cancellationToken);
        await LogAsync(connection, transaction, row.BrideId, row.GroomId,
            $"married couple={marriageId} groom={row.GroomId} bride={row.BrideId}", cancellationToken);
        row = await ReadByIdAsync(connection, transaction, marriageId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MarriageOutcome.Done, row);
    }

    public async Task<MarriageResult> ClaimGiftAsync(long marriageId, int characterId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, [characterId], cancellationToken);
        var row = await ReadActiveAsync(connection, transaction, characterId, true, cancellationToken);
        if (row is null || row.Id != marriageId || !row.IsMarried)
        {
            return new(MarriageOutcome.Stale, row);
        }

        var groom = characterId == row.GroomId;
        if (groom ? row.GroomGiftClaimed : row.BrideGiftClaimed)
        {
            return new(MarriageOutcome.AlreadyClaimed, row);
        }

        if (!await InsertBagItemAsync(connection, transaction, characterId, MarriagePolicy.WeddingGiftItemId, cancellationToken))
        {
            return new(MarriageOutcome.BagFull, row);
        }

        await using (var update = new NpgsqlCommand(
            groom
                ? "UPDATE public.character_marriages SET groom_gift_at = now() WHERE id = @id;"
                : "UPDATE public.character_marriages SET bride_gift_at = now() WHERE id = @id;",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("id", marriageId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await LogAsync(connection, transaction, characterId, characterId,
            $"gift couple={marriageId} item={MarriagePolicy.WeddingGiftItemId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MarriageOutcome.Done, row);
    }

    public async Task<MarriageResult> ClaimNecklaceAsync(int characterId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, [characterId], cancellationToken);
        var row = await ReadActiveAsync(connection, transaction, characterId, true, cancellationToken);
        if (row is null || row.IsMarried)
        {
            return new(MarriageOutcome.Stale, row);
        }

        var groom = characterId == row.GroomId;
        if (groom ? row.GroomNecklaceClaimed : row.BrideNecklaceClaimed)
        {
            // Wiki-56: "one per character, only while engaged".
            return new(MarriageOutcome.AlreadyClaimed, row);
        }

        if (!await InsertBagItemAsync(connection, transaction, characterId, MarriagePolicy.WhelkNecklaceItemId, cancellationToken))
        {
            return new(MarriageOutcome.BagFull, row);
        }

        await using (var update = new NpgsqlCommand(
            groom
                ? "UPDATE public.character_marriages SET groom_necklace_at = now() WHERE id = @id;"
                : "UPDATE public.character_marriages SET bride_necklace_at = now() WHERE id = @id;",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await LogAsync(connection, transaction, characterId, characterId,
            $"necklace couple={row.Id} item={MarriagePolicy.WhelkNecklaceItemId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MarriageOutcome.Done, row);
    }

    public async Task<MarriageResult> EndAsync(
        long marriageId, int initiatorId, MarriageEndKind kind, MarriageFine fine, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadByIdAsync(connection, transaction, marriageId, cancellationToken);
        if (row is null)
        {
            return new(MarriageOutcome.Stale);
        }

        await LockAsync(connection, transaction, [row.GroomId, row.BrideId], cancellationToken);
        row = await ReadByIdAsync(connection, transaction, marriageId, cancellationToken);
        var wantMarried = kind is MarriageEndKind.MutualDivorce or MarriageEndKind.ForcedDivorce;
        if (row is null || (row.GroomId != initiatorId && row.BrideId != initiatorId) ||
            row.Status != (wantMarried ? MarriageRow.Married : MarriageRow.Engaged))
        {
            return new(MarriageOutcome.Stale, row);
        }

        if (fine.Reputation > 0)
        {
            if (!await DebitAsync(connection, transaction, initiatorId, "medusa_honor_points", fine.Reputation, cancellationToken))
            {
                return new(MarriageOutcome.CannotPayFine, row);
            }
        }
        else if (fine.Silver > 0 &&
                 !await DebitAsync(connection, transaction, initiatorId, "\"Money\"", fine.Silver, cancellationToken))
        {
            return new(MarriageOutcome.CannotPayFine, row);
        }

        if (kind == MarriageEndKind.MutualDivorce)
        {
            // IGG/wiki: both lose 100 Rep. Clamped at zero (design choice; the sources do not say).
            await using var both = new NpgsqlCommand(
                """
                UPDATE public.character_base
                SET medusa_honor_points = GREATEST(COALESCE(medusa_honor_points, 0) - @rep, 0),
                    medusa_reward_revision = medusa_reward_revision + 1
                WHERE id = ANY(@ids);
                """,
                connection,
                transaction);
            both.Parameters.AddWithValue("rep", MarriagePolicy.MutualDivorceReputation);
            both.Parameters.AddWithValue("ids", new[] { row.GroomId, row.BrideId });
            await both.ExecuteNonQueryAsync(cancellationToken);
        }

        var kindText = kind switch
        {
            MarriageEndKind.MutualEngagement => "mutual-engagement",
            MarriageEndKind.ForcedEngagement => "forced-engagement",
            MarriageEndKind.MutualDivorce => "mutual-divorce",
            _ => "forced-divorce"
        };
        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_marriages
            SET status = 'ended', ended_at = now(), end_kind = @kind, ended_by = @by,
                end_fine_reputation = @rep, end_fine_silver = @silver
            WHERE id = @id;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("kind", kindText);
            update.Parameters.AddWithValue("by", initiatorId);
            update.Parameters.AddWithValue("rep", fine.Reputation);
            update.Parameters.AddWithValue("silver", fine.Silver);
            update.Parameters.AddWithValue("id", marriageId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await RemoveTitleAsync(connection, transaction, [row.GroomId, row.BrideId], MarriagePolicy.EngagedTitleId, cancellationToken);
        await RemoveTitleAsync(connection, transaction, [row.GroomId, row.BrideId], MarriagePolicy.NewlyWedTitleId, cancellationToken);
        await LogAsync(connection, transaction, initiatorId, row.PartnerOf(initiatorId),
            $"ended couple={marriageId} kind={kindText} by={initiatorId} fine-rep={fine.Reputation} fine-silver={fine.Silver}",
            cancellationToken);
        var wallet = await ReadWalletAsync(connection, transaction, initiatorId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MarriageOutcome.Done, row, wallet);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<Dictionary<int, string>> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int[] ids, CancellationToken cancellationToken)
    {
        var genders = new Dictionary<int, string>();
        await using var command = new NpgsqlCommand(
            "SELECT id, gender, lifecycle_state FROM public.character_base WHERE id = ANY(@ids) ORDER BY id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("ids", ids);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // REVIEW FIX 1: a deleted (tombstoned) character is locked but not returned, so a proposal or vow
            // still pending in memory from a character deleted since cannot commit (EngageAsync: NotFound).
            if (reader.GetString(2) == "active")
            {
                genders[reader.GetInt32(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        return genders;
    }

    private static async Task<MarriageRow?> ReadActiveAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int characterId, bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {RowColumns} FROM public.character_marriages " +
            "WHERE status <> 'ended' AND (groom_id = @id OR bride_id = @id) ORDER BY id DESC LIMIT 1" +
            (forUpdate ? " FOR UPDATE;" : ";"),
            connection,
            transaction);
        command.Parameters.AddWithValue("id", characterId);
        return await ReadRowAsync(command, cancellationToken);
    }

    private static async Task<MarriageRow?> ReadByIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {RowColumns} FROM public.character_marriages WHERE id = @id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        return await ReadRowAsync(command, cancellationToken);
    }

    private static async Task<MarriageRow?> ReadRowAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MarriageRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1), // NULL only on an ended row whose character was purged
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10));
    }

    private static async Task<MarriageWallet?> ReadWalletAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT "Money", "Stone", COALESCE(medusa_honor_points, 0)
            FROM public.character_base WHERE id = @id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MarriageWallet(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
            : null;
    }

    /// <summary>Takes <paramref name="amount"/> from one balance column; false (nothing changed) when short.</summary>
    private static async Task<bool> DebitAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int characterId, string column, int amount,
        CancellationToken cancellationToken)
    {
        if (amount <= 0)
        {
            return true;
        }

        var revision = column == "medusa_honor_points" ? "medusa_reward_revision" : "wallet_revision";
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE public.character_base
            SET {column} = COALESCE({column}, 0) - @amount, {revision} = {revision} + 1
            WHERE id = @id AND COALESCE({column}, 0) >= @amount;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("id", characterId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<long> CountBagItemAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int characterId, int itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM public.character_items
            WHERE user_id = @id AND item_location = @kitBag AND prop_id = @item;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("kitBag", KitBagLocation);
        command.Parameters.AddWithValue("item", itemId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>One bound item into the lowest free bag slot (the operator store's /give pattern).</summary>
    private static async Task<bool> InsertBagItemAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int characterId, int itemId,
        CancellationToken cancellationToken)
    {
        var used = new HashSet<short>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT slot_index FROM public.character_items
            WHERE user_id = @id AND item_location = @kitBag FOR UPDATE;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            command.Parameters.AddWithValue("kitBag", KitBagLocation);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                used.Add(reader.GetInt16(0));
            }
        }

        short slot = -1;
        for (short candidate = 0; candidate < KitBagSlots; candidate++)
        {
            if (!used.Contains(candidate))
            {
                slot = candidate;
                break;
            }
        }

        if (slot < 0)
        {
            return false;
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
            VALUES (@id, @kitBag, @slot, @item, 1, 1, 1, 1, 0, 0);
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("id", characterId);
            insert.Parameters.AddWithValue("kitBag", KitBagLocation);
            insert.Parameters.AddWithValue("slot", slot);
            insert.Parameters.AddWithValue("item", itemId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var revision = new NpgsqlCommand(
            "UPDATE public.character_base SET inventory_revision = inventory_revision + 1 WHERE id = @id;",
            connection,
            transaction))
        {
            revision.Parameters.AddWithValue("id", characterId);
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }

        return true;
    }

    private static async Task GrantTitleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int[] ids, int titleId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_title_grants (character_id, title_id, granted_by, note)
            SELECT id, @title, @by, 'Cupid' FROM unnest(@ids) AS id
            ON CONFLICT (character_id, title_id) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("title", titleId);
        command.Parameters.AddWithValue("by", GrantedBy);
        command.Parameters.AddWithValue("ids", ids);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Only the grants this feature made; a title granted by hand is never touched.</summary>
    private static async Task RemoveTitleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int[] ids, int titleId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM public.character_title_grants
            WHERE character_id = ANY(@ids) AND title_id = @title AND granted_by = @by;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("title", titleId);
        command.Parameters.AddWithValue("by", GrantedBy);
        command.Parameters.AddWithValue("ids", ids);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LogAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int actorId, int targetId, string detail,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO public.operator_log (actor_account_id, actor_character, command, target, detail)
            SELECT account_id, name, @command, @target, @detail FROM public.character_base WHERE id = @actor;
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("command", LogCommand);
        insert.Parameters.AddWithValue("target", targetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("detail", detail);
        insert.Parameters.AddWithValue("actor", actorId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Start-up wiring, called beside the operator store's; never stops the server.</summary>
internal static class MarriageRuntime
{
    public static void Initialize(string connectionString)
    {
        try
        {
            MarriageControls.Store = new PostgresMarriageStore(NpgsqlDataSource.Create(connectionString));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine("[marriage] start-up failed; Cupid stays menu-only: " + error.Message);
        }
    }
}
