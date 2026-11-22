using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Operations.Health;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W14: faults seen in the live server log on 2026-11-22.
internal static partial class GameHandlerCheckpointLifecycleChecks
{
    private static readonly FieldInfo CharacterStateGateField =
        RequiredField("_characterStateGate");
    private static readonly FieldInfo HermesHoodExpiryTaskField =
        RequiredField("_hermesHoodExpiryTask");
    private static readonly MethodInfo ScheduleHermesHoodExpiryMethod =
        RequiredMethod("ScheduleHermesHoodExpiry");
    private static readonly MethodInfo StopHermesHoodExpiryMethod =
        RequiredMethod("StopHermesHoodExpiryAsync");

    internal static async Task RunW14Async()
    {
        await CheckUnownedSessionEndSkipsFinalFlushAsync();
        await CheckOwnedSessionEndStillFlushesAsync();
        await CheckHermesHoodTimerJoinedBeforeGateDisposalAsync();
        CheckSessionHealthPruneIsTwoSingleStatements();
        await CheckSessionHealthPruneRunsOnPostgresAsync();
    }

    // Live: account 44 left from the character screen (client-ready=False) and the end logged
    // "final position/vitals flush failed reason=InvalidOperationException" ("no active checkpoint owner").
    private static async Task CheckUnownedSessionEndSkipsFinalFlushAsync()
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = CharacterLoadSnapshotHydrator.Hydrate(snapshot)!.Character!;
        var coordinator = new RecordingCoordinator(
            character.PositionRevision,
            character.VitalsRevision);
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = CreateHandler(session, new RejectingSnapshotReader(), coordinator);
        InstallIdentity(handler, snapshot.AccountId, character);
        Check.True(character.PositionRevision > 0, "W14 fixture: the loaded character has a stored revision");

        var output = await CaptureConsoleAsync(() => InvokeAsync(FinalizeOwnershipMethod, handler));

        Check.Equal(0, coordinator.Operations.Count,
            "W14: a session that never owned its character writes no final checkpoint");
        Check.True(!output.Contains("flush failed", StringComparison.Ordinal),
            "W14: the character-screen exit logs no final flush failure");
    }

    private static async Task CheckOwnedSessionEndStillFlushesAsync()
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = CharacterLoadSnapshotHydrator.Hydrate(snapshot)!.Character!;
        var coordinator = new RecordingCoordinator(
            character.PositionRevision,
            character.VitalsRevision);
        character.CheckpointOwnerId = coordinator.Owner.OwnerId;
        character.CheckpointOwnerGeneration = coordinator.Owner.Generation;
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = CreateHandler(session, new RejectingSnapshotReader(), coordinator);
        InstallIdentity(handler, snapshot.AccountId, character);
        OwnershipAcquiredField.SetValue(handler, true);

        var output = await CaptureConsoleAsync(() => InvokeAsync(FinalizeOwnershipMethod, handler));

        Check.True(
            coordinator.Operations.SequenceEqual(["position", "vitals", "release"]),
            "W14: a world-entered session still flushes position and vitals, then releases");
        Check.True(!output.Contains("flush failed", StringComparison.Ordinal),
            "W14: the owned final flush succeeds");
    }

    // Live: "[hermes-hood] expiry refresh failed: ObjectDisposedException ... SemaphoreSlim" after a logout
    // with a hood still running.
    private static async Task CheckHermesHoodTimerJoinedBeforeGateDisposalAsync()
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = CharacterLoadSnapshotHydrator.Hydrate(snapshot)!.Character!;
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = CreateHandler(session, new RejectingSnapshotReader(), characterCheckpoints: null);
        InstallIdentity(handler, snapshot.AccountId, character);
        var gate = (SemaphoreSlim)CharacterStateGateField.GetValue(handler)!;
        try
        {
            var output = await CaptureConsoleAsync(async () =>
            {
                // A packet holds the gate; the hood runs out, so the timer ends up waiting on the gate.
                await gate.WaitAsync();
                HermesHoods.Set(character.Id, DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(20));
                Invoke(ScheduleHermesHoodExpiryMethod, handler, CancellationToken.None);
                var timer = (Task?)HermesHoodExpiryTaskField.GetValue(handler);
                Check.True(timer is not null, "W14: the hood timer task is tracked");
                await Task.Delay(TimeSpan.FromMilliseconds(1_400));
                Check.True(!timer!.IsCompleted, "W14 fixture: the hood timer is blocked on the state gate");

                // Session end: join the timer, then the gate goes (as GameClientHandler.RunAsync's finally).
                await InvokeAsync(StopHermesHoodExpiryMethod, handler);
                Check.True(timer.IsCompleted, "W14: session end joins the hood timer");
                Check.True(HermesHoodExpiryTaskField.GetValue(handler) is null,
                    "W14: session end forgets the hood timer");
                gate.Release();
                gate.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            });
            Check.True(!output.Contains("expiry refresh failed", StringComparison.Ordinal),
                "W14: a hood running at logout ends without an ObjectDisposedException");
        }
        finally
        {
            HermesHoods.Clear(character.Id);
        }
    }

    // Live Postgres log, hourly: "cannot insert multiple commands into a prepared statement" for the prune.
    private static void CheckSessionHealthPruneIsTwoSingleStatements()
    {
        foreach (var sql in new[] { SessionHealthRecorder.PruneByAgeSql, SessionHealthRecorder.PruneByCountSql })
        {
            Check.True(!sql.Contains(';', StringComparison.Ordinal),
                "W14: each session-health prune command is one statement");
        }
        using var batch = SessionHealthRecorder.BuildPruneBatch(connection: null, retentionDays: 30, maximumRows: 200_000);
        Check.Equal(2, batch.BatchCommands.Count, "W14: the prune is a batch of two commands");
        Check.True(batch.BatchCommands.Cast<Npgsql.NpgsqlBatchCommand>().All(command => command.Parameters.Count == 1),
            "W14: each prune command carries its own single parameter");
    }

    // Runs only with GODSWAR_W14_PRUNE_POSTGRES set to a disposable database (never the live one).
    private static async Task CheckSessionHealthPruneRunsOnPostgresAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_W14_PRUNE_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var setup = new Npgsql.NpgsqlCommand(
            """
            DROP TABLE IF EXISTS public.session_health;
            CREATE TABLE public.session_health (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                started_at timestamptz NOT NULL);
            INSERT INTO public.session_health (started_at)
            SELECT now() - make_interval(days => d) FROM generate_series(0, 39) AS d;
            """, connection))
        {
            await setup.ExecuteNonQueryAsync();
        }

        // 40 rows, one per day back to 39 days: the age rule keeps days 0..29 (30 rows), the count cap (25)
        // then keeps the newest 25 ids.
        await using (var batch = SessionHealthRecorder.BuildPruneBatch(connection, retentionDays: 30, maximumRows: 25))
        {
            await batch.ExecuteNonQueryAsync();
        }

        await using var count = new Npgsql.NpgsqlCommand("SELECT count(*) FROM public.session_health", connection);
        Check.Equal(25L, (long)(await count.ExecuteScalarAsync())!, "W14: the prune batch runs on PostgreSQL");
        Console.WriteLine("[w14] session-health prune executed on PostgreSQL");
    }

    private static async Task<string> CaptureConsoleAsync(Func<Task> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capture = new StringWriter();
        Console.SetOut(capture);
        Console.SetError(capture);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
        return capture.ToString();
    }
}
