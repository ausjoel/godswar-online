using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// TRAINING: public.training_ground_rewards, plus daily-entry kind 10 (and, WARRIOR
/// F-51, kinds 21-24) in the shared legacy_instance_settings /
/// legacy_instance_daily_entries tables.
/// </summary>
/// <remarks>
/// Not a registered schema migration (brief addendum 2): idempotent DDL on first
/// use, so the previous binary still starts against a database this build used.
/// The two instance-kind check constraints are only ever WIDENED (their current
/// expression OR instance_kind = 10), so no existing row or kind is refused. The
/// same DDL is in Development/build/training/migration.sql (pre-release step).
/// </remarks>
internal sealed class PostgresTrainingGroundStore(NpgsqlDataSource dataSource)
    : ITrainingGroundStore
{
    internal const short TrainingGroundInstanceKind = 10;

    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.training_ground_rewards (
            run_id uuid NOT NULL,
            character_id integer NOT NULL,
            realm_id smallint NOT NULL,
            score integer NOT NULL CHECK (score >= 0),
            outcome text NOT NULL,
            elapsed_seconds integer NOT NULL CHECK (elapsed_seconds >= 0),
            experience integer NOT NULL CHECK (experience >= 0),
            talent_points integer NOT NULL CHECK (talent_points >= 0),
            created_at timestamptz NOT NULL DEFAULT now(),
            claimed_at timestamptz,
            paid_at timestamptz,
            PRIMARY KEY (run_id, character_id)
        );
        CREATE INDEX IF NOT EXISTS ix_training_ground_rewards_unclaimed
            ON public.training_ground_rewards (character_id)
            WHERE claimed_at IS NULL;
        COMMENT ON TABLE public.training_ground_rewards IS
            'TRAINING: one completion reward per (Training Ground run, member present at the end); paid once by the character''s own handler.';

        DO $training_ground_kind$
        DECLARE
            kind_check record;
        BEGIN
            FOR kind_check IN
                SELECT relation.relname AS table_name,
                       candidate.conname AS constraint_name,
                       pg_catalog.pg_get_constraintdef(candidate.oid) AS definition
                FROM pg_catalog.pg_constraint AS candidate
                JOIN pg_catalog.pg_class AS relation ON relation.oid = candidate.conrelid
                JOIN pg_catalog.pg_namespace AS schema_namespace
                  ON schema_namespace.oid = relation.relnamespace
                WHERE schema_namespace.nspname = 'public'
                  AND candidate.contype = 'c'
                  AND candidate.conname IN (
                      'ck_legacy_instance_settings_instance_kind',
                      'ck_legacy_instance_daily_entries_instance_kind')
                  AND strpos(pg_catalog.pg_get_constraintdef(candidate.oid),
                             'instance_kind = 10') = 0
            LOOP
                EXECUTE format('ALTER TABLE public.%I DROP CONSTRAINT %I',
                    kind_check.table_name, kind_check.constraint_name);
                EXECUTE format('ALTER TABLE public.%I ADD CONSTRAINT %I CHECK ((%s) OR instance_kind = 10)',
                    kind_check.table_name, kind_check.constraint_name,
                    substring(kind_check.definition FROM 7));
            END LOOP;
        END
        $training_ground_kind$;

        -- IGG acid=166: one entry a day, no paid retry.
        INSERT INTO public.legacy_instance_settings (instance_kind, free_entry_limit, paid_retry_limit)
        VALUES (10, 1, 0)
        ON CONFLICT DO NOTHING;

        -- WARRIOR (F-51): the four Warriors' Training Grounds, kinds 21-24, widened the
        -- same way (only ever OR'ed onto the current expression; never narrowed).
        DO $warrior_training_kind$
        DECLARE
            kind_check record;
        BEGIN
            FOR kind_check IN
                SELECT relation.relname AS table_name,
                       candidate.conname AS constraint_name,
                       pg_catalog.pg_get_constraintdef(candidate.oid) AS definition
                FROM pg_catalog.pg_constraint AS candidate
                JOIN pg_catalog.pg_class AS relation ON relation.oid = candidate.conrelid
                JOIN pg_catalog.pg_namespace AS schema_namespace
                  ON schema_namespace.oid = relation.relnamespace
                WHERE schema_namespace.nspname = 'public'
                  AND candidate.contype = 'c'
                  AND candidate.conname IN (
                      'ck_legacy_instance_settings_instance_kind',
                      'ck_legacy_instance_daily_entries_instance_kind')
                  AND strpos(pg_catalog.pg_get_constraintdef(candidate.oid),
                             'instance_kind >= 21') = 0
            LOOP
                EXECUTE format('ALTER TABLE public.%I DROP CONSTRAINT %I',
                    kind_check.table_name, kind_check.constraint_name);
                EXECUTE format('ALTER TABLE public.%I ADD CONSTRAINT %I CHECK ((%s) OR (instance_kind >= 21 AND instance_kind <= 24))',
                    kind_check.table_name, kind_check.constraint_name,
                    substring(kind_check.definition FROM 7));
            END LOOP;
        END
        $warrior_training_kind$;

        -- Wiki: each of the four grounds once a day, no paid retry.
        INSERT INTO public.legacy_instance_settings (instance_kind, free_entry_limit, paid_retry_limit)
        VALUES (21, 1, 0), (22, 1, 0), (23, 1, 0), (24, 1, 0)
        ON CONFLICT DO NOTHING;
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));
    private int _ensured;
    private int _tableSeen;
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _ensured) == 0)
            {
                await SchemaGate.WaitAsync(cancellationToken);
                try
                {
                    if (Volatile.Read(ref _ensured) == 0)
                    {
                        await using var schema = await connection.BeginTransactionAsync(cancellationToken);
                        await using (var guard = new NpgsqlCommand(
                            "SELECT pg_advisory_xact_lock(@key);", connection, schema))
                        {
                            guard.Parameters.AddWithValue("key", 0x5452474E44); // "TRGND"
                            await guard.ExecuteNonQueryAsync(cancellationToken);
                        }
                        await using (var ddl = new NpgsqlCommand(SchemaSql, connection, schema))
                        {
                            await ddl.ExecuteNonQueryAsync(cancellationToken);
                        }
                        await schema.CommitAsync(cancellationToken);
                        Volatile.Write(ref _ensured, 1);
                    }
                }
                finally
                {
                    SchemaGate.Release();
                }
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<int> RecordRewardsAsync(
        IReadOnlyCollection<TrainingGroundRewardGrant> grants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count == 0)
        {
            return 0;
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = 0;
        foreach (var grant in grants)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO public.training_ground_rewards " +
                "(run_id, character_id, realm_id, score, outcome, elapsed_seconds, experience, talent_points) " +
                "VALUES (@run, @character, @realm, @score, @outcome, @elapsed, @experience, @talent) " +
                "ON CONFLICT (run_id, character_id) DO NOTHING;",
                connection,
                transaction);
            command.Parameters.AddWithValue("run", grant.RunId);
            command.Parameters.AddWithValue("character", grant.CharacterId);
            command.Parameters.AddWithValue("realm", grant.RealmId);
            command.Parameters.AddWithValue("score", grant.Score);
            command.Parameters.AddWithValue("outcome", grant.Outcome);
            command.Parameters.AddWithValue("elapsed", grant.ElapsedSeconds);
            command.Parameters.AddWithValue("experience", grant.Experience);
            command.Parameters.AddWithValue("talent", grant.TalentPoints);
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<TrainingGroundRewardGrant>> ClaimRewardsAsync(
        int characterId,
        CancellationToken cancellationToken = default)
    {
        // The payout loop runs for every character every 20 s, so it never runs
        // DDL: until the first Training Ground entry created the table there is
        // simply nothing to claim.
        if (Volatile.Read(ref _ensured) == 0 && Volatile.Read(ref _tableSeen) == 0)
        {
            await using var probeConnection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var probe = new NpgsqlCommand(
                "SELECT to_regclass('public.training_ground_rewards') IS NOT NULL;", probeConnection);
            if (await probe.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return [];
            }
            Volatile.Write(ref _tableSeen, 1);
        }
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE public.training_ground_rewards SET claimed_at = now() " +
            "WHERE character_id = @character AND claimed_at IS NULL " +
            "RETURNING run_id, character_id, realm_id, score, outcome, elapsed_seconds, experience, talent_points;",
            connection);
        command.Parameters.AddWithValue("character", characterId);
        var claims = new List<TrainingGroundRewardGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(new(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }
        return claims;
    }

    public Task MarkPaidAsync(Guid runId, int characterId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE public.training_ground_rewards SET paid_at = now() " +
            "WHERE run_id = @run AND character_id = @character AND paid_at IS NULL;",
            runId, characterId, cancellationToken);

    private async Task ExecuteAsync(string sql, Guid runId, int characterId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("run", NpgsqlDbType.Uuid).Value = runId;
        command.Parameters.AddWithValue("character", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
