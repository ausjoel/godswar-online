namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The one dispatcher both native repetition controls share - the panel's
    /// Terminate action and the twelve-byte end request - for 飘渺幻境,
    /// 亚特兰蒂斯, 港湾遇袭 and 美杜莎之岛.
    /// </summary>
    /// <remarks>
    /// The terminal flow itself is not decided here: the first step is the shared
    /// end-of-run leave, which is the only path that carries a member out during
    /// any run's countdown and carries only the member who pressed it. Each
    /// dungeon then contributes just its own rule for ending its own active run,
    /// and the current server-owned instance, never the supplied repetition ID,
    /// decides which one receives the action.
    /// </remarks>
    private async Task HandleInstanceRunTerminationAsync(int? repetitionId,
        int repetitionIndex, CancellationToken cancellationToken)
    {
        if (_registry.TryResolveInstanceRunEndLeave(_session, repetitionId,
                repetitionIndex, DateTimeOffset.UtcNow, out var leave))
        {
            var transferred = await TryBeginAuthoritativeInstanceTransitionAsync(
                leave, cancellationToken);
            Console.WriteLine(
                "[instance] end-window leave character=" +
                $"{_character?.Name ?? "<none>"} " +
                $"instance={leave.ExpectedSourceWorldInstanceId} " +
                $"transferred={transferred}");
            return;
        }

        if (TryHandleAtlantisTermination(repetitionId, repetitionIndex) ||
            TryHandleWonderlandTermination(repetitionId, repetitionIndex) ||
            TryHandleHarborAttackTermination(repetitionId, repetitionIndex) ||
            TryHandleTrainingGroundTermination(repetitionId, repetitionIndex)) // TRAINING
        {
            return;
        }

        if (repetitionId is { } id
            ? _registry.TryEndMedusaRunFromLeader(_session, id, repetitionIndex,
                DateTimeOffset.UtcNow)
            : _registry.TryTerminateMedusaRunFromLeader(_session,
                DateTimeOffset.UtcNow))
        {
            return;
        }

        Console.WriteLine(
            "[instance] rejected non-authoritative end control character=" +
            $"{_character?.Name ?? "<none>"}");
    }
}
