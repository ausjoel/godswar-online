using Npgsql;

namespace Godswar.Server.Infrastructure.EventsNpc;

// EVENTSNPC: durable state for the Lost Book, Marathon and Random Quest Manager events. Like server_rates and
// NpcContentRuntime this is NOT a registered schema migration: the tables are created idempotently on first
// use (and by Development/build/eventsnpc/migration.sql), so an older binary still starts against a database
// this build has used. No foreign key to character_base (a REFERENCES lock on character_base stalls
// character writes on a live server).
internal static class EventsNpcRuntime
{
    public static PostgresEventsNpcStore? Store { get; private set; }

    public static void Initialize(string connectionString)
    {
        try
        {
            Store = new PostgresEventsNpcStore(NpgsqlDataSource.Create(connectionString));
            var store = Store;
            _ = Task.Run(async () =>
            {
                try { await store.WarmUpAsync(CancellationToken.None); }
                catch (Exception error) { Console.WriteLine($"[eventsnpc] start-up DDL deferred: {error.Message}"); }
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[eventsnpc] start-up failed; the events stay menu-only: {error.Message}");
        }
    }
}

/// <summary>One character's event counters. Day counters belong to <see cref="Day"/> only.</summary>
internal sealed record EventsNpcState(
    DateOnly Day,
    int ElderScrolls,
    int IncompleteWishes,
    int CompleteWishes,
    int Races,
    DateTimeOffset? RaceStartedAt,
    int ChainStep,
    long ChainQuestId,
    bool SackPending = false,
    int RewardStep = 0)
{
    // SackPending: one Quest Sack was handed out for ChainStep and has not been opened yet. Only that one open
    // pays; any other sack (a stashed or re-issued copy) opens to nothing. RewardStep: a chain hand-in whose
    // payout failed (bag full) owes the reward for that step; the manager pays it on the next claim.
    public static EventsNpcState Fresh(DateOnly day) => new(day, 0, 0, 0, 0, null, 0, 0);
}

internal sealed class PostgresEventsNpcStore
{
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.eventsnpc_character_state (
            character_id integer PRIMARY KEY,
            day date NOT NULL,
            elder_scrolls smallint NOT NULL DEFAULT 0,
            incomplete_wishes smallint NOT NULL DEFAULT 0,
            complete_wishes smallint NOT NULL DEFAULT 0,
            races smallint NOT NULL DEFAULT 0,
            race_started_at timestamptz NULL,
            chain_step smallint NOT NULL DEFAULT 0,
            chain_quest_id bigint NOT NULL DEFAULT 0,
            updated_at timestamptz NOT NULL DEFAULT now()
        );

        -- Review fix (2026-11-03): one-shot Quest Sack and the owed chain reward.
        ALTER TABLE public.eventsnpc_character_state
            ADD COLUMN IF NOT EXISTS sack_pending boolean NOT NULL DEFAULT false,
            ADD COLUMN IF NOT EXISTS reward_step smallint NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS public.eventsnpc_marathon_finishes (
            id bigserial PRIMARY KEY,
            character_id integer NOT NULL,
            finished_at timestamptz NOT NULL,
            milliseconds bigint NOT NULL CHECK (milliseconds >= 0),
            binding_gold integer NOT NULL
        );

        CREATE INDEX IF NOT EXISTS eventsnpc_marathon_finishes_time
            ON public.eventsnpc_marathon_finishes (finished_at);

        -- FINALISE2 (P4): per-character daily counters of the item/NPC handlers (B-Gold Trader, Mystery Merchant,
        -- War Supplier). Not a registered migration; empty = nothing used today.
        CREATE TABLE IF NOT EXISTS public.finalise2_npc_daily (
            character_id integer NOT NULL,
            day date NOT NULL,
            counter text NOT NULL,
            used integer NOT NULL DEFAULT 0,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (character_id, day, counter)
        );

        -- W3 seasons halloween / may day: one Halloween suit per character and season year; each faction's May Day
        -- tree per year (row-locked, UPDATE .. RETURNING), the year's winner (first insert wins) and each character's
        -- Fertiliser and claim. Not a registered migration; no rows = nothing planted or claimed.
        CREATE TABLE IF NOT EXISTS public.halloween_suit_claims (
            character_id integer NOT NULL,
            season_year integer NOT NULL,
            claimed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (character_id, season_year)
        );
        CREATE TABLE IF NOT EXISTS public.may_day_tree (
            season_year integer NOT NULL,
            camp smallint NOT NULL,
            points integer NOT NULL DEFAULT 0 CHECK (points >= 0),
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (season_year, camp)
        );
        CREATE TABLE IF NOT EXISTS public.may_day_result (
            season_year integer PRIMARY KEY,
            winner_camp smallint NOT NULL CHECK (winner_camp BETWEEN -1 AND 1),
            reason text NOT NULL,
            decided_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS public.may_day_contributions (
            character_id integer NOT NULL,
            season_year integer NOT NULL,
            camp smallint NOT NULL,
            points integer NOT NULL DEFAULT 0 CHECK (points >= 0),
            claimed boolean NOT NULL DEFAULT false,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (character_id, season_year)
        );

        -- W1 dead items (2026-11-08): Cypress Leave mask until. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.masked_state (
            character_id integer PRIMARY KEY,
            expires_at timestamptz NOT NULL
        );

        -- ITEMS-HERMES (2026-11-09): Hermes Hood until, per character. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.hermes_hood_state (
            character_id integer PRIMARY KEY,
            expires_at timestamptz NOT NULL
        );

        -- ITEMS-HERMES (2026-11-09): pets that have used their one free rename. No foreign key (see above); a
        -- deleted pet's row is harmless. Not a registered migration; no row = the next rename is free.
        CREATE TABLE IF NOT EXISTS public.pet_rename_state (
            pet_id bigint PRIMARY KEY,
            character_id integer NOT NULL,
            renames integer NOT NULL DEFAULT 1 CHECK (renames >= 1),
            renamed_at timestamptz NOT NULL DEFAULT now()
        );

        -- W1 dead items (2026-11-08): Chat Bubble 1/2 style per character. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.chat_bubble_state (
            character_id integer PRIMARY KEY,
            style smallint NOT NULL CHECK (style BETWEEN 0 AND 255),
            expires_at timestamptz NOT NULL
        );

        -- W3 autoloot (2026-11-08): Plump Wheat / Golden Leaf autoloot until. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.autoloot_state (
            character_id integer PRIMARY KEY,
            expires_at timestamptz NOT NULL
        );

        -- W3 Magic Bell (2026-11-09, F-136): quests a pet is doing, until due_at. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.pet_dispatch_state (
            character_id integer NOT NULL,
            quest_id integer NOT NULL,
            pet_id bigint NOT NULL,
            pet_name text NOT NULL DEFAULT '',
            started_at timestamptz NOT NULL DEFAULT now(),
            due_at timestamptz NOT NULL,
            PRIMARY KEY (character_id, quest_id)
        );
        CREATE INDEX IF NOT EXISTS pet_dispatch_state_due ON public.pet_dispatch_state (due_at);
        -- PETWORK (F-137, 2026-11-09): the one pet a character has sent to work, with the reward fixed at the
        -- start. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.pet_work_state (
            character_id integer PRIMARY KEY,
            pet_id bigint NOT NULL,
            pay_type smallint NOT NULL CHECK (pay_type IN (1, 2)),
            hours smallint NOT NULL CHECK (hours IN (1, 3, 8, 24)),
            cost integer NOT NULL CHECK (cost >= 0),
            experience bigint NOT NULL CHECK (experience >= 0),
            talent_points integer NOT NULL CHECK (talent_points >= 0),
            started_at timestamptz NOT NULL DEFAULT now(),
            ends_at timestamptz NOT NULL
        );

        -- W1 prize claims (2026-11-08): beginner-gift online time and gifts claimed. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.time_prize_state (
            character_id integer PRIMARY KEY,
            online_seconds integer NOT NULL DEFAULT 0 CHECK (online_seconds >= 0),
            beginner_claimed smallint NOT NULL DEFAULT 0 CHECK (beginner_claimed >= 0),
            updated_at timestamptz NOT NULL DEFAULT now()
        );

        -- F-131 offline prize (2026-11-08): the last-online time each character was last paid for. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.offline_prize_state (
            character_id integer PRIMARY KEY,
            paid_for timestamptz NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT now()
        );

        -- F-138 newbie growth rewards: one row per character and Rookie Guide entry, state 1 = claimed (paid),
        -- 2 = deleted by the player. The primary key is the once-only guard. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.rookie_growth_claims (
            character_id integer NOT NULL,
            entry_id smallint NOT NULL CHECK (entry_id BETWEEN 1 AND 255),
            state smallint NOT NULL CHECK (state IN (1, 2)),
            created_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (character_id, entry_id)
        );

        -- W2 F-27: the Rich Man Envoy's "Rags to Riches" lowest-unique-number lottery. Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.eventsnpc_lottery_rounds (
            round_id serial PRIMARY KEY,
            opened_at timestamptz NOT NULL DEFAULT now(),
            closed_at timestamptz,
            winner_character_id integer,
            winning_number integer,
            claimed boolean NOT NULL DEFAULT false
        );
        CREATE TABLE IF NOT EXISTS public.eventsnpc_lottery_entries (
            round_id integer NOT NULL,
            character_id integer NOT NULL,
            number integer NOT NULL CHECK (number BETWEEN 1 AND 10000),
            created_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS eventsnpc_lottery_entries_round ON public.eventsnpc_lottery_entries (round_id, number);

        -- PROF slice 1 (2026-11-09): professions. One row per character that has learned a profession (profession 0
        -- = unlearned, AP kept). Not a registered migration; no rows = nobody has a profession.
        CREATE TABLE IF NOT EXISTS public.profession_state (
            character_id integer PRIMARY KEY,
            profession smallint NOT NULL DEFAULT 0 CHECK (profession BETWEEN 0 AND 4),
            level smallint NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 30),
            experience integer NOT NULL DEFAULT 0 CHECK (experience >= 0),
            ap integer NOT NULL DEFAULT 0 CHECK (ap >= 0),
            ap_updated_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS public.profession_blueprints (
            character_id integer NOT NULL,
            blueprint_id integer NOT NULL,
            proficiency integer NOT NULL DEFAULT 0 CHECK (proficiency BETWEEN 0 AND 10000),
            learned_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (character_id, blueprint_id)
        );
        -- PROF slice 2: the end of a Type 1 blueprint's cooldown (NULL = ready).
        ALTER TABLE public.profession_blueprints ADD COLUMN IF NOT EXISTS cooldown_until timestamptz;
        -- W2 F-27 TROY: Trojan Expedition scores (one row per character per event day; guild at the first kill) and the
        -- guild leaders' claims. Not a registered migration; no foreign keys (see the header).
        CREATE TABLE IF NOT EXISTS public.troy_scores (
            event_day date NOT NULL,
            character_id integer NOT NULL,
            guild_id bigint NULL,
            points integer NOT NULL DEFAULT 0 CHECK (points >= 0),
            personal_claimed boolean NOT NULL DEFAULT false,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (event_day, character_id)
        );
        CREATE INDEX IF NOT EXISTS troy_scores_guild ON public.troy_scores (event_day, guild_id);
        CREATE TABLE IF NOT EXISTS public.troy_guild_claims (
            event_day date NOT NULL,
            guild_id bigint NOT NULL,
            claimed_by integer NOT NULL,
            score bigint NOT NULL,
            silver bigint NOT NULL,
            gold bigint NOT NULL,
            claimed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (event_day, guild_id)
        );
        -- W3 Christmas: each faction's Gigantic Reindeer Model per season year, and level rewards claimed per
        -- (character, year, level). Not a registered migration.
        CREATE TABLE IF NOT EXISTS public.christmas_model (
            season_year integer NOT NULL,
            camp smallint NOT NULL,
            points integer NOT NULL DEFAULT 0 CHECK (points >= 0),
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (season_year, camp)
        );
        CREATE TABLE IF NOT EXISTS public.christmas_claims (
            character_id integer NOT NULL,
            season_year integer NOT NULL,
            level smallint NOT NULL CHECK (level BETWEEN 1 AND 5),
            camp smallint NOT NULL,
            claimed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (character_id, season_year, level)
        );

        -- WARRIOR scrolls (F-146 b, 2026-11-09): hidden "kill 1 Magma Scorpion" tasks armed by scrolls 4838-4851.
        -- One open task per character (partial unique index); the reward is fixed when armed and paid once (paid_at
        -- is claimed before paying). Not a registered migration; an older dll ignores it.
        CREATE TABLE IF NOT EXISTS public.warrior_scroll_tasks (
            id bigserial PRIMARY KEY,
            character_id integer NOT NULL,
            item_id integer NOT NULL CHECK (item_id BETWEEN 4838 AND 4851),
            experience integer NOT NULL CHECK (experience >= 0),
            armed_at timestamptz NOT NULL DEFAULT now(),
            completed_at timestamptz NULL,
            completed_instance text NULL,
            paid_at timestamptz NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS warrior_scroll_tasks_open
            ON public.warrior_scroll_tasks (character_id) WHERE completed_at IS NULL;
        CREATE INDEX IF NOT EXISTS warrior_scroll_tasks_unpaid
            ON public.warrior_scroll_tasks (character_id) WHERE completed_at IS NOT NULL AND paid_at IS NULL;

        -- KELSIS (F-15, 2026-11-09): Delighted Kelsis' dog contest. One row per dog handed in; pet_snapshot holds the
        -- pet row and its stat/bonus/skill rows as they were, so a taken dog can be restored
        -- (server/docs/kelsis-contest/restore-pet.sql). Not a registered migration; an older dll ignores it.
        CREATE TABLE IF NOT EXISTS public.farm_kelsis_submissions (
            id bigserial PRIMARY KEY,
            round_start timestamptz NOT NULL,
            stat smallint NOT NULL CHECK (stat BETWEEN 1 AND 7),
            character_id integer NOT NULL,
            pet_id bigint NOT NULL,
            value numeric(18, 6) NOT NULL,
            status text NOT NULL CHECK (status IN ('pending', 'taken', 'aborted')),
            pet_snapshot jsonb NOT NULL,
            submitted_at timestamptz NOT NULL DEFAULT now(),
            prize_claimed_at timestamptz NULL,
            restored_at timestamptz NULL
        );
        CREATE INDEX IF NOT EXISTS farm_kelsis_submissions_round
            ON public.farm_kelsis_submissions (round_start, value, id);
        CREATE INDEX IF NOT EXISTS farm_kelsis_submissions_character
            ON public.farm_kelsis_submissions (character_id, round_start);
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _ensured;

    public PostgresEventsNpcStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>One statement outside any character lock, with parameters (W1: the batched beginner-gift accrual).</summary>
    public async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>One read-only scalar outside any character lock (W1: the start-up mask load).</summary>
    public async Task<object?> ReadScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="body"/> in one transaction with the character row locked, so two clicks cannot
    /// spend the same scroll or the same daily allowance. The body commits by returning; an exception rolls
    /// everything back.
    /// </summary>
    public async Task<T> RunAsync<T>(int characterId, DateOnly day, Func<EventsNpcTransaction, Task<T>> body,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockRow = new NpgsqlCommand(
            "SELECT true FROM public.character_base WHERE id = @c FOR UPDATE;", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("c", characterId);
            if (await lockRow.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new InvalidOperationException($"Character {characterId} does not exist.");
            }
        }

        var tx = new EventsNpcTransaction(connection, transaction, characterId, day, cancellationToken);
        await tx.LoadStateAsync();
        var result = await body(tx);
        if (tx.RolledBack)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await tx.SaveStateAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>The fastest race finished since <paramref name="since"/>, in milliseconds.</summary>
    public async Task<long?> BestRaceSinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT min(milliseconds) FROM public.eventsnpc_marathon_finishes WHERE finished_at >= @s;", connection);
        command.Parameters.AddWithValue("s", since);
        return await command.ExecuteScalarAsync(cancellationToken) is long value ? value : null;
    }

    /// <summary>
    /// Treasure Map digs: the Place-stamping trigger and backfill. Separate from <see cref="SchemaSql"/> so a failure
    /// (privilege, lock timeout) is logged and leaves every other event feature working.
    /// </summary>
    private static async Task EnsureTreasureMapStampAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var stamp = new NpgsqlCommand(
                Godswar.Server.Application.Finalise2.TreasureMapPlaces.StampSql(), connection);
            await stamp.ExecuteNonQueryAsync(cancellationToken);
            await using var reset = new NpgsqlCommand("RESET lock_timeout;", connection);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[treasure-map] stamp DDL failed (maps keep no dig spot): {error.Message}");
        }
    }

    /// <summary>Runs the idempotent DDL at start-up, so held Treasure Maps are stamped before anyone logs in.</summary>
    public Task WarmUpAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_ensured)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (!_ensured)
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                await using var ddl = new NpgsqlCommand(SchemaSql, connection);
                await ddl.ExecuteNonQueryAsync(cancellationToken);
                await EnsureTreasureMapStampAsync(connection, cancellationToken);
                _ensured = true;
            }
        }
        finally
        {
            _schemaGate.Release();
        }
    }
}
