namespace Godswar.Server.ProtocolChecks;

internal static class LegacyInstanceCheckCatalog
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        (
            LegacyInstanceDailyEntryChecks.CheckName,
            LegacyInstanceDailyEntryChecks.RunAsync),
        (
            PostgresLegacyInstanceDailyEntryChecks.CheckName,
            PostgresLegacyInstanceDailyEntryChecks.RunAsync),
        (
            PostgresLegacyInstanceOpalPaymentIntegrationChecks.CheckName,
            PostgresLegacyInstanceOpalPaymentIntegrationChecks.RunAsync),
        (
            InstanceRosterPanelChecks.CheckName,
            InstanceRosterPanelChecks.RunAsync),
        // Restored with the in-game confirmed bytes (0 offline, 1 online, 2
        // waiting) once the state-byte experiment concluded: every other value
        // makes the client skip the row, so the experiment's candidates are gone.
        (
            InstanceCallerHandlerChecks.InstanceRosterStatesCheckName,
            InstanceCallerHandlerChecks.RunInstanceRosterStatesAsync),
        // A repeated member confirmation used to mint one daily-entry
        // reservation per frame; this checks the dedupe that stops it.
        (
            InstanceCallerHandlerChecks.DuplicateEntryCheckName,
            InstanceCallerHandlerChecks.RunDuplicateEntryAsync),
        // 美杜莎之岛's own invite-by-name, which the shared 10224 handler had
        // stopped answering.
        (
            InstanceCallerHandlerChecks.ManualMedusaInvitationCheckName,
            InstanceCallerHandlerChecks.RunManualMedusaInvitationAsync),
        // Joining a party must not offer 美杜莎 entry any more.
        (
            InstanceCallerHandlerChecks.MedusaPartyJoinCheckName,
            InstanceCallerHandlerChecks.RunMedusaPartyJoinAsync),
        // Item sweep 2026-11-04: big gift packs disconnected the player (projection > 128-packet egress queue).
        (
            InstanceCallerHandlerChecks.WonderlandProjectionChunksCheckName,
            InstanceCallerHandlerChecks.RunWonderlandProjectionChunksAsync)
    ];
}
