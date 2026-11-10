using Godswar.Server.Application.Rates;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rates;

// RATES: PostgreSQL side of the server-rates table (GodsWar-2.51/Polish patch).
//
// Deliberately NOT a registered schema migration: the tables are created with
// idempotent DDL at start-up, so the stock server binary (which refuses to start
// when schema_migrations is ahead of it) can still be started against a database
// this build has used. Rolling back therefore needs no database surgery.

internal sealed record ServerRatesRow(string Key, decimal Value);

internal sealed record ServerRatesTableState(
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy,
    IReadOnlyList<ServerRatesRow> Rows);

internal static class PostgresServerRatesStore
{
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.server_rates (
            rate_key text PRIMARY KEY,
            value numeric(20,4) NOT NULL,
            default_value numeric(20,4) NOT NULL,
            min_value numeric(20,4) NOT NULL,
            max_value numeric(20,4) NOT NULL,
            step numeric(20,4) NOT NULL DEFAULT 0.05,
            kind text NOT NULL DEFAULT 'multiplier',
            category text NOT NULL,
            label text NOT NULL,
            unit text NOT NULL,
            applies_to text NOT NULL DEFAULT '',
            description text NOT NULL DEFAULT '',
            sort_order integer NOT NULL DEFAULT 0,
            updated_at timestamptz NOT NULL DEFAULT now(),
            updated_by text NOT NULL DEFAULT 'server',
            CONSTRAINT ck_server_rates_key
                CHECK (rate_key ~ '^[a-z][a-z0-9_.]{2,63}$'),
            CONSTRAINT ck_server_rates_kind
                CHECK (kind IN ('multiplier', 'absolute')),
            CONSTRAINT ck_server_rates_bounds
                CHECK (min_value <= max_value),
            CONSTRAINT ck_server_rates_value
                CHECK (value BETWEEN min_value AND max_value),
            CONSTRAINT ck_server_rates_default
                CHECK (default_value BETWEEN min_value AND max_value),
            CONSTRAINT ck_server_rates_updated_by
                CHECK (length(btrim(updated_by)) BETWEEN 1 AND 128)
        );

        COMMENT ON TABLE public.server_rates IS
            'RATES (Polish patch): server-wide multipliers and limits. The game server hot-reloads this table (poll, or POST /rates/reload on the management port). Bounds come from the server binary.';

        CREATE TABLE IF NOT EXISTS public.server_rates_state (
            setting_id smallint PRIMARY KEY DEFAULT 1
                CHECK (setting_id = 1),
            revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
            updated_at timestamptz NOT NULL DEFAULT now(),
            updated_by text NOT NULL DEFAULT 'server'
        );

        INSERT INTO public.server_rates_state (setting_id)
        VALUES (1)
        ON CONFLICT (setting_id) DO NOTHING;

        CREATE TABLE IF NOT EXISTS public.server_rates_log (
            id bigserial PRIMARY KEY,
            changed_at timestamptz NOT NULL DEFAULT now(),
            changed_by text NOT NULL,
            rate_key text NOT NULL,
            old_value numeric(20,4),
            new_value numeric(20,4),
            revision bigint NOT NULL,
            note text NOT NULL DEFAULT ''
        );

        CREATE OR REPLACE FUNCTION public.server_rates_bump_revision()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $server_rates_bump_revision$
        BEGIN
            UPDATE public.server_rates_state
            SET revision = revision + 1,
                updated_at = now(),
                updated_by = COALESCE(
                    NULLIF(current_setting('godswar.rates_actor', true), ''),
                    current_user)
            WHERE setting_id = 1;
            RETURN NULL;
        END;
        $server_rates_bump_revision$;

        CREATE OR REPLACE FUNCTION public.server_rates_log_change()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $server_rates_log_change$
        BEGIN
            IF TG_OP = 'UPDATE' AND NEW.value IS NOT DISTINCT FROM OLD.value THEN
                RETURN NULL;
            END IF;
            INSERT INTO public.server_rates_log (
                changed_by, rate_key, old_value, new_value, revision, note)
            VALUES (
                CASE WHEN TG_OP = 'DELETE' THEN OLD.updated_by ELSE NEW.updated_by END,
                CASE WHEN TG_OP = 'DELETE' THEN OLD.rate_key ELSE NEW.rate_key END,
                CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.value END,
                CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE NEW.value END,
                (SELECT revision + 1 FROM public.server_rates_state WHERE setting_id = 1),
                COALESCE(current_setting('godswar.rates_note', true), ''));
            RETURN NULL;
        END;
        $server_rates_log_change$;

        CREATE OR REPLACE TRIGGER trg_server_rates_log
        AFTER INSERT OR UPDATE OR DELETE ON public.server_rates
        FOR EACH ROW
        EXECUTE FUNCTION public.server_rates_log_change();

        CREATE OR REPLACE TRIGGER trg_server_rates_revision
        AFTER INSERT OR UPDATE OR DELETE ON public.server_rates
        FOR EACH STATEMENT
        EXECUTE FUNCTION public.server_rates_bump_revision();

        -- Atlantis HardPoints were constrained to the stock tier values
        -- (200..2800, one list of tiers). A multiplier needs any positive
        -- amount. Looser than stock, so the stock binary still accepts it.
        DO $server_rates_atlantis$
        BEGIN
            IF to_regclass('public.atlantis_completion_rewards') IS NOT NULL THEN
                IF EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'public.atlantis_completion_rewards'::regclass
                      AND conname = 'ck_atlantis_completion_rewards_hard_points') THEN
                    ALTER TABLE public.atlantis_completion_rewards
                        DROP CONSTRAINT ck_atlantis_completion_rewards_hard_points;
                END IF;
                IF EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'public.atlantis_completion_rewards'::regclass
                      AND conname = 'ck_atlantis_completion_rewards_incomplete_tier') THEN
                    ALTER TABLE public.atlantis_completion_rewards
                        DROP CONSTRAINT ck_atlantis_completion_rewards_incomplete_tier;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'public.atlantis_completion_rewards'::regclass
                      AND conname = 'ck_atlantis_completion_rewards_hard_points_rates') THEN
                    ALTER TABLE public.atlantis_completion_rewards
                        ADD CONSTRAINT ck_atlantis_completion_rewards_hard_points_rates
                        CHECK (hard_points > 0);
                END IF;
            END IF;
        END;
        $server_rates_atlantis$;

        -- ROUND2: Lucky Gods wishes were stored with CHECK (used_count <= 10).
        -- cap.lucky_gods_daily may allow more, so keep only used_count >= 0.
        -- Looser than stock, so the stock binary still accepts every row.
        DO $server_rates_lucky_gods$
        DECLARE
            stock_check text;
        BEGIN
            IF to_regclass('public.character_lucky_gods_wish') IS NOT NULL THEN
                FOR stock_check IN
                    SELECT conname FROM pg_constraint
                    WHERE conrelid = 'public.character_lucky_gods_wish'::regclass
                      AND contype = 'c'
                      AND pg_get_constraintdef(oid) LIKE '%used_count <= 10%'
                LOOP
                    EXECUTE format(
                        'ALTER TABLE public.character_lucky_gods_wish DROP CONSTRAINT %I',
                        stock_check);
                END LOOP;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'public.character_lucky_gods_wish'::regclass
                      AND conname = 'ck_character_lucky_gods_wish_used_count_rates') THEN
                    ALTER TABLE public.character_lucky_gods_wish
                        ADD CONSTRAINT ck_character_lucky_gods_wish_used_count_rates
                        CHECK (used_count >= 0);
                END IF;
            END IF;
        END;
        $server_rates_lucky_gods$;
        """;

    // One statement, so the revision moves once per start-up. Metadata always
    // follows the binary; a stored value is kept and only clamped into new
    // bounds. Rows whose metadata is already current are not touched.
    private const string UpsertDefinitionsSql =
        """
        INSERT INTO public.server_rates AS r (
            rate_key, value, default_value, min_value, max_value, step, kind,
            category, label, unit, applies_to, description, sort_order,
            updated_by)
        SELECT t.k, t.d, t.d, t.mn, t.mx, t.st, t.kd, t.c, t.l, t.u, t.a,
               t.ds, t.so, 'server-catalog'
        FROM unnest(
            @keys, @defaults, @minimums, @maximums, @steps, @kinds,
            @categories, @labels, @units, @appliesTo, @descriptions,
            @sortOrders)
            AS t(k, d, mn, mx, st, kd, c, l, u, a, ds, so)
        ON CONFLICT (rate_key) DO UPDATE SET
            default_value = EXCLUDED.default_value,
            min_value = EXCLUDED.min_value,
            max_value = EXCLUDED.max_value,
            step = EXCLUDED.step,
            kind = EXCLUDED.kind,
            category = EXCLUDED.category,
            label = EXCLUDED.label,
            unit = EXCLUDED.unit,
            applies_to = EXCLUDED.applies_to,
            description = EXCLUDED.description,
            sort_order = EXCLUDED.sort_order,
            value = LEAST(GREATEST(r.value, EXCLUDED.min_value), EXCLUDED.max_value),
            updated_by = CASE
                WHEN r.value BETWEEN EXCLUDED.min_value AND EXCLUDED.max_value
                    THEN r.updated_by
                ELSE 'server-catalog-clamp'
            END
        WHERE (r.default_value, r.min_value, r.max_value, r.step, r.kind,
               r.category, r.label, r.unit, r.applies_to, r.description,
               r.sort_order)
            IS DISTINCT FROM
              (EXCLUDED.default_value, EXCLUDED.min_value, EXCLUDED.max_value,
               EXCLUDED.step, EXCLUDED.kind, EXCLUDED.category, EXCLUDED.label,
               EXCLUDED.unit, EXCLUDED.applies_to, EXCLUDED.description,
               EXCLUDED.sort_order);
        """;

    public static async Task EnsureAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var serialize = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('godswar.server_rates'));",
            connection,
            transaction))
        {
            await serialize.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var schema = new NpgsqlCommand(
            SchemaSql,
            connection,
            transaction))
        {
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        var definitions = ServerRateCatalog.Definitions;
        await using (var upsert = new NpgsqlCommand(
            UpsertDefinitionsSql,
            connection,
            transaction))
        {
            upsert.Parameters.AddWithValue(
                "keys",
                definitions.Select(static d => d.Key).ToArray());
            upsert.Parameters.AddWithValue(
                "defaults",
                definitions.Select(static d => d.DefaultValue).ToArray());
            upsert.Parameters.AddWithValue(
                "minimums",
                definitions.Select(static d => d.MinimumValue).ToArray());
            upsert.Parameters.AddWithValue(
                "maximums",
                definitions.Select(static d => d.MaximumValue).ToArray());
            upsert.Parameters.AddWithValue(
                "steps",
                definitions.Select(static d => d.Step).ToArray());
            upsert.Parameters.AddWithValue(
                "kinds",
                definitions
                    .Select(static d => d.Kind == ServerRateKind.Absolute
                        ? "absolute"
                        : "multiplier")
                    .ToArray());
            upsert.Parameters.AddWithValue(
                "categories",
                definitions.Select(static d => d.Category).ToArray());
            upsert.Parameters.AddWithValue(
                "labels",
                definitions.Select(static d => d.Label).ToArray());
            upsert.Parameters.AddWithValue(
                "units",
                definitions.Select(static d => d.Unit).ToArray());
            upsert.Parameters.AddWithValue(
                "appliesTo",
                definitions.Select(static d => d.AppliesTo).ToArray());
            upsert.Parameters.AddWithValue(
                "descriptions",
                definitions.Select(static d => d.Description).ToArray());
            upsert.Parameters.AddWithValue(
                "sortOrders",
                definitions.Select(static d => d.SortOrder).ToArray());
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task<long> ReadRevisionAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT revision FROM public.server_rates_state WHERE setting_id = 1;");
        return await command.ExecuteScalarAsync(cancellationToken) is long revision
            ? revision
            : throw new InvalidDataException(
                "The server-rates state row is missing.");
    }

    public static async Task<ServerRatesTableState> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        // One statement, so the revision and the values come from one snapshot.
        await using var command = dataSource.CreateCommand(
            """
            SELECT s.revision, s.updated_at, s.updated_by, r.rate_key, r.value
            FROM public.server_rates_state s
            LEFT JOIN public.server_rates r ON true
            WHERE s.setting_id = 1
            ORDER BY r.rate_key;
            """);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        long? revision = null;
        var updatedAt = DateTimeOffset.UnixEpoch;
        var updatedBy = "unknown";
        var rows = new List<ServerRatesRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            revision ??= reader.GetInt64(0);
            updatedAt = new DateTimeOffset(
                reader.GetDateTime(1).ToUniversalTime());
            updatedBy = reader.GetString(2);
            if (!reader.IsDBNull(3))
            {
                rows.Add(new ServerRatesRow(
                    reader.GetString(3),
                    reader.GetDecimal(4)));
            }
        }

        return new ServerRatesTableState(
            revision ?? throw new InvalidDataException(
                "The server-rates state row is missing."),
            updatedAt,
            updatedBy,
            rows);
    }
}
