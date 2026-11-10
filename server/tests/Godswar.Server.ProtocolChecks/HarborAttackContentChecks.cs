using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// INSTANCES (Bay Under Attack build): end boss, generated team score, the
/// never-respawn rule for harbor maps only (shared monster-init path), the
/// run progress (kills counted once, Christina completes), the ordered and
/// retried ledger writer, the first-use (unregistered) ledger tables, and the
/// switch defaults.
/// </summary>
internal static class HarborAttackContentChecks
{
    public const string CheckName =
        "Bay Under Attack content: Christina ends the run, score, no respawn, run ledger";

    public static async Task RunAsync()
    {
        Check.True(
            HarborAttackContentPolicy.IsEndBoss("B_bossSA_mage_018") &&
            HarborAttackContentPolicy.IsEndBoss("B_bossSAQ_mage_018") &&
            !HarborAttackContentPolicy.IsEndBoss("B_bossSA_skeleton_001") &&
            !HarborAttackContentPolicy.IsEndBoss("B_bossGC_mage_017"),
            "Christina (both level bands) is the only end boss");
        Check.True(
            HarborAttackContentPolicy.ScoreFor("B_normalSA_robber_001") == 1 &&
            HarborAttackContentPolicy.ScoreFor("B_eliteSAQ_persiawarrior_005") == 10 &&
            HarborAttackContentPolicy.ScoreFor("B_bossSA_skeleton_001") == 50,
            "generated team score is 1 / 10 / 50 by rank");

        Check.True(
            GameSessionRegistry.MonsterRespawnPolicyForMap(208) == MonsterRespawnPolicy.Never &&
            GameSessionRegistry.MonsterRespawnPolicyForMap(209) == MonsterRespawnPolicy.Never,
            "harbor run monsters never respawn");
        foreach (var other in new[] { 0, 1, 2, 3, 8, 17, 29, 42, 200, 204, 205, 207, 210 })
        {
            Check.True(
                GameSessionRegistry.MonsterRespawnPolicyForMap(other) == MonsterRespawnPolicy.Timed,
                $"map {other} keeps the timed respawn it had");
        }

        CheckRunProgress();
        await CheckLedgerWriterAsync();

        // The ledger tables are created on first use, never as a registered
        // migration, so the previous binary still starts after a rollback.
        Check.True(
            !PostgresSchemaMigrationCatalog.All.Any(static m =>
                m.Id.Contains("harbor_attack_run", StringComparison.Ordinal) ||
                m.Sql.Contains("harbor_attack_runs", StringComparison.Ordinal)),
            "no registered migration creates the harbor run ledger");
        var ddl = PostgresHarborAttackRunLedger.SchemaSql;
        Check.True(
            ddl.Contains("CREATE TABLE IF NOT EXISTS public.harbor_attack_runs", StringComparison.Ordinal) &&
            ddl.Contains("CREATE TABLE IF NOT EXISTS public.harbor_attack_run_members", StringComparison.Ordinal) &&
            ddl.Contains("CREATE INDEX IF NOT EXISTS", StringComparison.Ordinal) &&
            !ddl.Contains("DROP ", StringComparison.OrdinalIgnoreCase) &&
            !ddl.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase) &&
            !ddl.Contains("REFERENCES", StringComparison.OrdinalIgnoreCase) &&
            // BAY2: plus the two completion-reward tables, same first-use DDL.
            ddl.Contains("CREATE TABLE IF NOT EXISTS public.harbor_attack_completion_rewards", StringComparison.Ordinal) &&
            ddl.Contains("CREATE TABLE IF NOT EXISTS public.harbor_attack_completion_reward_members", StringComparison.Ordinal) &&
            ddl.Split("CREATE TABLE ", StringSplitOptions.None).Length - 1 == 4,
            "the first-use ledger DDL is idempotent, additive and takes no foreign-key lock");

        var defaults = ServerRatesSnapshot.Default;
        Check.True(
            // BAY2 (the admin 2026-11-03 round 2): Bay Under Attack open, refund on,
            // the Training Ground still closed.
            defaults.Absolute(ServerRateKeys.HarborAttackEntry) == 1 &&
            defaults.Absolute(ServerRateKeys.TrainingGroundEntry) == 0 &&
            defaults.Absolute(ServerRateKeys.HarborAttackRestartRefund) == 1 &&
            defaults.Absolute(ServerRateKeys.HarborAttackExperiencePercent) == 100,
            "Bay Under Attack opens by default with the restart refund on; the Training Ground stays closed");
    }

    private static void CheckRunProgress()
    {
        var at = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var progress = new HarborAttackRunProgress();
        Check.Equal((int)HarborAttackKillOutcome.Scored,
            (int)progress.Record(46001, 1, "B_normalSA_robber_001", at), "a normal kill scores");
        Check.Equal((int)HarborAttackKillOutcome.Ignored,
            (int)progress.Record(46001, 1, "B_normalSA_robber_001", at), "a replayed kill is ignored");
        Check.Equal((int)HarborAttackKillOutcome.Scored,
            (int)progress.Record(46001, 2, "B_normalSA_robber_001", at), "a new spawn generation scores");
        Check.Equal((int)HarborAttackKillOutcome.Scored,
            (int)progress.Record(46020, 1, "B_eliteSA_persiawarrior_001", at), "an elite kill scores");
        Check.Equal(12, progress.Score, "score 1 + 1 + 10");
        Check.True(progress.CompletedAt is null, "the run is not complete before Christina");
        Check.Equal((int)HarborAttackKillOutcome.Completed,
            (int)progress.Record(46054, 1, "B_bossSA_mage_018", at.AddMinutes(9)), "Christina completes the run");
        Check.True(progress.CompletedAt == at.AddMinutes(9), "completion time is her kill");
        Check.Equal(62, progress.Score, "Christina scores 50");
        Check.Equal((int)HarborAttackKillOutcome.Ignored,
            (int)progress.Record(46055, 1, "B_eliteSA_persiawarrior_002", at.AddMinutes(10)),
            "nothing scores after completion");
        Check.Equal((int)HarborAttackKillOutcome.Ignored,
            (int)progress.Record(46054, 1, "B_bossSA_mage_018", at.AddMinutes(10)),
            "a second Christina report (tick fallback after the recorder) is ignored");
        Check.Equal(62, progress.Score, "score unchanged after completion");
    }

    private static async Task CheckLedgerWriterAsync()
    {
        var ledger = new FakeLedger();
        var writer = new HarborAttackRunLedgerWriter(ledger, static _ => TimeSpan.Zero);
        var run = Guid.NewGuid();
        var late = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        // The open is slow and the member and close are queued behind it: they
        // must still land after it, in order.
        var openGate = new TaskCompletionSource();
        ledger.OpenGate = openGate;
        writer.Open(run, 1, 208, started, [11, 12, 12, 13]);
        writer.AddMember(run, late, 14);
        Check.True(writer.Close(run, "abandoned", started.AddSeconds(5)), "first close is accepted");
        Check.True(!writer.Close(run, "completed", started.AddSeconds(6)), "a second close is refused");
        Check.True(writer.IsClosed(run), "run is marked closed");
        await Task.Delay(50);
        Check.Equal(0, ledger.Calls.Count, "nothing lands while the open is still running");
        openGate.SetResult();
        await writer.WhenIdleAsync(run);
        Check.Equal(
            $"open:{run}:208:11,12,13|member:{run}:{late}:14|close:{run}:abandoned",
            string.Join("|", ledger.Calls),
            "open, then the late member, then one close, in order (characters de-duplicated)");

        // A failing write is retried, and a later write still waits for it.
        ledger.Calls.Clear();
        ledger.FailuresLeft = HarborAttackRunLedgerWriter.Attempts - 1;
        var second = Guid.NewGuid();
        writer.Open(second, 1, 209, started, [21]);
        writer.Close(second, "completed", started.AddMinutes(9));
        await writer.WhenIdleAsync(second);
        Check.Equal(
            $"open:{second}:209:21|close:{second}:completed",
            string.Join("|", ledger.Calls),
            "an open that failed twice is retried and lands before its close");

        // After the last attempt the write is dropped (logged), the queue moves on.
        ledger.Calls.Clear();
        ledger.FailuresLeft = HarborAttackRunLedgerWriter.Attempts;
        var third = Guid.NewGuid();
        writer.Open(third, 1, 208, started, [31]);
        writer.Close(third, "terminated", started.AddMinutes(3));
        await writer.WhenIdleAsync(third);
        Check.Equal(
            $"close:{third}:terminated",
            string.Join("|", ledger.Calls),
            "an open that failed every attempt does not block the close");
    }

    private sealed class FakeLedger : IHarborAttackRunLedger
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource? OpenGate { get; set; }
        public int FailuresLeft { get; set; }

        public async Task OpenAsync(Guid reservationId, short realmId, byte mapId,
            DateTimeOffset startedAtUtc, IReadOnlyCollection<int> characterIds,
            CancellationToken cancellationToken = default)
        {
            if (OpenGate is { } gate)
            {
                OpenGate = null;
                await gate.Task;
            }
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new InvalidOperationException("simulated database failure");
            }
            lock (Calls)
            {
                Calls.Add($"open:{reservationId}:{mapId}:{string.Join(",", characterIds)}");
            }
        }

        public Task AddMemberAsync(Guid runReservationId, Guid memberReservationId, int characterId,
            CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add($"member:{runReservationId}:{memberReservationId}:{characterId}");
            }
            return Task.CompletedTask;
        }

        public Task CloseAsync(Guid reservationId, string reason, DateTimeOffset closedAtUtc,
            CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add($"close:{reservationId}:{reason}");
            }
            return Task.CompletedTask;
        }

        public Task<HarborAttackRefundResult> RefundInterruptedAsync(short realmId,
            DateTimeOffset processStartedAtUtc, bool refundEntries,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(default(HarborAttackRefundResult));
    }
}
