using Godswar.Server.Application.Operators;
using Npgsql;

namespace Godswar.Server.Infrastructure.Operators;

// W5B: loads public.gm_power_policy at start-up and polls it (the editor's "GM powers" page writes it).
// Like server_rates and event_board_entries this is NOT a registered schema migration: idempotent DDL on first
// use, so an older binary still starts against a database this build has used, and a rollback is the dll alone.
// A failed load keeps the last good policy; before the first good load every power is allowed, which is the
// behaviour of the builds before this one. Never stops the game server.
internal static class GmPowerPolicyRuntime
{
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.gm_power_policy (
            power_key text PRIMARY KEY CHECK (power_key ~ '^[a-z][a-z0-9_.]{0,47}$'),
            allowed boolean NOT NULL,
            note text NOT NULL DEFAULT '' CHECK (char_length(note) <= 200),
            updated_at timestamptz NOT NULL DEFAULT now(),
            updated_by text NOT NULL DEFAULT session_user CHECK (char_length(updated_by) <= 128)
        );

        CREATE TABLE IF NOT EXISTS public.gm_power_policy_log (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            at timestamptz NOT NULL DEFAULT now(),
            power_key text NOT NULL,
            operation text NOT NULL,
            old_allowed boolean,
            new_allowed boolean,
            changed_by text NOT NULL DEFAULT session_user
        );

        CREATE OR REPLACE FUNCTION public.gm_power_policy_audit() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $fn$
        BEGIN
            IF TG_OP <> 'DELETE' THEN
                NEW.updated_at := now();
                NEW.updated_by := session_user;
            END IF;
            INSERT INTO public.gm_power_policy_log (power_key, operation, old_allowed, new_allowed, changed_by)
            VALUES (
                CASE WHEN TG_OP = 'DELETE' THEN OLD.power_key ELSE NEW.power_key END,
                TG_OP,
                CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.allowed END,
                CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE NEW.allowed END,
                session_user);
            RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
        END $fn$;

        DROP TRIGGER IF EXISTS trg_gm_power_policy_audit ON public.gm_power_policy;
        CREATE TRIGGER trg_gm_power_policy_audit
            BEFORE INSERT OR UPDATE OR DELETE ON public.gm_power_policy
            FOR EACH ROW EXECUTE FUNCTION public.gm_power_policy_audit();

        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'gweditor') THEN
                GRANT SELECT, INSERT, UPDATE, DELETE ON public.gm_power_policy TO gweditor;
                GRANT SELECT ON public.gm_power_policy_log TO gweditor;
            END IF;
        EXCEPTION WHEN insufficient_privilege THEN
            RAISE NOTICE 'gm_power_policy: not owner, gweditor grant skipped';
        END $$;
        """;

    private const string PollEnvironmentVariable = "GODSWAR_GM_POLICY_POLL_SECONDS";
    private const string InsufficientPrivilege = "42501";
    private const string UndefinedTable = "42P01";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static NpgsqlDataSource? _dataSource;
    private static bool _ensured;
    private static string? _lastError;
    private static string _lastState = "";

    public static TimeSpan PollInterval { get; } = ReadPollInterval(
        Environment.GetEnvironmentVariable(PollEnvironmentVariable));

    internal static TimeSpan ReadPollInterval(string? raw) =>
        int.TryParse(raw, out var seconds) && seconds is >= 1 and <= 3600
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(5);

    /// <summary>Never fails start-up.</summary>
    public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
            await ReloadAsync("startup", cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[gm-policy] no database; every GM power stays allowed: {error.GetType().Name}");
        }
    }

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken);
                await ReloadAsync("poll", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Console.WriteLine($"[gm-policy] poll failed: {error.GetType().Name}");
            }
        }
    }

    /// <summary>Reads the table and installs it; a failure keeps the last good policy. Never throws (except cancel).</summary>
    public static async Task ReloadAsync(string reason, CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            if (!_ensured)
            {
                await using var ddl = new NpgsqlCommand(SchemaSql, connection);
                await ddl.ExecuteNonQueryAsync(cancellationToken);
                _ensured = true;
            }

            var rows = new List<KeyValuePair<string, bool>>();
            var updatedBy = "";
            await using (var command = new NpgsqlCommand(
                "SELECT power_key, allowed, updated_by FROM public.gm_power_policy ORDER BY updated_at, power_key",
                connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(KeyValuePair.Create(reader.GetString(0), reader.GetBoolean(1)));
                    updatedBy = reader.GetString(2);
                }
            }

            Apply(rows, reason, updatedBy);
            _lastError = null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (error is PostgresException { SqlState: UndefinedTable })
            {
                _ensured = false;
            }

            var message = $"{error.GetType().Name}: {error.Message}";
            if (message != _lastError)
            {
                _lastError = message;
                var hint = error is PostgresException { SqlState: InsufficientPrivilege }
                    ? " (fix: as postgres run ALTER TABLE public.gm_power_policy OWNER TO godswar; same for gm_power_policy_log)"
                    : string.Empty;
                Console.WriteLine(
                    $"[gm-policy] load failed; keeping the last policy ({GmPowerPolicy.Current.Source}): {message}{hint}");
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Installs rows and logs once per change (also used by the checks).</summary>
    internal static void Apply(IReadOnlyList<KeyValuePair<string, bool>> rows, string reason, string updatedBy)
    {
        var unknown = GmPowerPolicy.Install(rows, "database", updatedBy);
        var denied = GmPowerPolicy.Current.Denied.Order(StringComparer.Ordinal).ToArray();
        var state = string.Join(",", denied) + "|" + string.Join(",", unknown);
        if (state == _lastState && reason != "startup")
        {
            return;
        }

        _lastState = state;
        Console.WriteLine(
            $"[gm-policy] loaded reason={reason} by={updatedBy} master={(GmPowerPolicy.Allows(GmPowerPolicy.Master) ? "on" : "off")} " +
            $"switched-off=[{string.Join(", ", denied)}]" +
            (unknown.Count > 0 ? $" unknown-keys-ignored=[{string.Join(", ", unknown)}]" : string.Empty));
    }
}
