namespace Godswar.Server.Game;

internal enum SceneTransitionOutcome
{
    // No destination remains authoritative. This also covers a provisional
    // destination checkpoint whose source compensation completed successfully.
    RejectedWithoutRelocation,
    CommittedAwaitingReadiness,
    CommittedRequiresReconnect
}
