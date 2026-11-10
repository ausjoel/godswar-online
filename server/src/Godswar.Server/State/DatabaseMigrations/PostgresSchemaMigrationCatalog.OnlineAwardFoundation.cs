namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    internal static PostgresSchemaMigration
        CreateOnlineAwardFoundation() => new(
        "20260821_105_online_award_foundation",
        "Create revisioned Online Award balance and durable daily claims",
        OnlineAwardSchemaSql + OnlineAwardGuardSql + OnlineAwardSeedSql);
}
