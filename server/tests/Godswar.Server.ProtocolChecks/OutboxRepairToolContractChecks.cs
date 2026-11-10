using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class OutboxRepairToolContractChecks
{
    public const string CheckName =
        "Guarded historical outbox repair tool contract";

    public static async Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var repair = await File.ReadAllTextAsync(Path.Combine(
            root,
            "tools",
            "RepairLocalDevelopmentOutboxPoison.ps1"));
        var proof = await File.ReadAllTextAsync(Path.Combine(
            root,
            "tools",
            "TestLocalDevelopmentOutboxStreamRepair.ps1"));
        var sparse = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260830_122_outbox_ordered_sparse");
        var claimPlan = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260830_123_outbox_claim_candidate_index");

        Check.True(
            repair.Contains(sparse.Checksum, StringComparison.Ordinal) &&
            repair.Contains(claimPlan.Checksum, StringComparison.Ordinal),
            "repair readiness pins the current sparse and claim-plan migrations");
        Check.True(
            repair.Contains(
                "DISABLE TRIGGER trg_outbox_events_guard",
                StringComparison.Ordinal) &&
            repair.Contains(
                "SET CONSTRAINTS ALL IMMEDIATE",
                StringComparison.Ordinal) &&
            repair.Contains(
                "ENABLE TRIGGER trg_outbox_events_guard",
                StringComparison.Ordinal) &&
            !repair.Contains(
                "set_config('session_replication_role'",
                StringComparison.Ordinal),
            "repair bypasses only the named immutable event guard");
        Check.True(
            repair.Contains(
                "trg_command_audit_immutable",
                StringComparison.Ordinal) &&
            repair.Contains(
                "ck_outbox_events_sparse_consumer_policy",
                StringComparison.Ordinal) &&
            repair.Contains(
                "ix_outbox_events_ordered_stream_version",
                StringComparison.Ordinal),
            "repair verifies immutable evidence and durable sparse schema guards");

        var auditLock = repair.IndexOf(
            "LOCK TABLE public.command_audit",
            StringComparison.Ordinal);
        var eventLock = repair.IndexOf(
            "LOCK TABLE public.outbox_events",
            StringComparison.Ordinal);
        var positionLock = repair.IndexOf(
            "LOCK TABLE public.outbox_consumer_positions",
            StringComparison.Ordinal);
        Check.True(
            auditLock >= 0 &&
            auditLock < eventLock &&
            eventLock < positionLock,
            "repair uses the normal audit, event, position lock order");
        Check.True(
            proof.Contains(
                "$env:GODSWAR_OUTBOX_REPAIR_TEST_PHASE = 'prepare'",
                StringComparison.Ordinal) &&
            proof.Contains(
                "$env:GODSWAR_OUTBOX_REPAIR_TEST_PHASE = 'verify'",
                StringComparison.Ordinal) &&
            proof.Contains(
                "LiveDatabaseUnchanged = $true",
                StringComparison.Ordinal),
            "disposable proof separates baseline migration from repaired drain");
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "GodswarServer.sln")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
