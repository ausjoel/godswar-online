using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// One terminal 美杜莎之岛 run, ready for the shared end-of-run flow.
    /// </summary>
    private sealed record MedusaRunEnd(
        InstanceRunEnding Ending,
        IReadOnlyList<InstanceRunEndMember> Members);

    /// <summary>
    /// Moves 美杜莎's leader to the earliest still-present member when the
    /// registered leader has left the run, through the one leader-transfer
    /// implementation every dungeon shares.
    /// </summary>
    /// <remarks>
    /// 美杜莎之岛 was the one dungeon without this: its end control was bound to
    /// the panel registration's session, so a leader who dropped and relogged
    /// could not end his own run and a run whose leader left had no ending
    /// authority at all.
    /// </remarks>
    private void MaintainMedusaInstanceLeader(WorldInstanceRuntime runtime)
    {
        var state = InvokeWorldOwner(runtime, static map => (
            Ownership: map.TryGetMedusaOwnershipSnapshot(out var snapshot)
                ? snapshot
                : null,
            Players: map.Snapshot()));
        var ownership = state.Ownership;
        if (ownership is null)
        {
            return;
        }

        MaintainInstanceRunLeader(
            runtime.InstanceId,
            "Medusa",
            [.. ownership.Run.AdmittedCharacterIds],
            [.. state.Players
                .Where(static context =>
                    context.WorldReady && !context.Session.IsDisconnected)
                .Select(static context => context.CharacterId)
                .Distinct()
                .Order()]);
    }

    /// <summary>
    /// The terminal state of a 美杜莎之岛 run, as the shared end-of-run flow
    /// publishes it: its own client scene, its own team score, and the run's own
    /// terminal moment.
    /// </summary>
    private MedusaRunEnd? CaptureMedusaRunEnd(WorldInstanceRuntime runtime)
    {
        var state = InvokeWorldOwner(runtime, static map => (
            Ownership: map.TryGetMedusaOwnershipSnapshot(out var snapshot)
                ? snapshot
                : null,
            Players: map.Snapshot()));
        var ownership = state.Ownership;
        if (ownership is null)
        {
            return null;
        }
        var run = ownership.Run;
        if (run.State == MedusaRunState.Active ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership.ContentMapId.Value, out var clientSceneId))
        {
            return null;
        }

        var ending = BeginInstanceRunEnd(runtime.InstanceId, InstanceRunKind.Medusa,
            "Medusa", runtime.MapId, checked((ushort)clientSceneId),
            MedusaDailyEntryLimit(runtime.InstanceId),
            run.TeamScore, run.CompletionMarker?.CompletedAt ?? run.Deadline);
        return new(ending,
            [.. state.Players
                .Where(static context =>
                    context.WorldReady && !context.Session.IsDisconnected)
                .Select(ToInstanceRunEndMember)]);
    }

    /// <summary>
    /// The daily entry limit the run's panel was opened with, read from the
    /// registrations that carry it.
    /// </summary>
    private ushort MedusaDailyEntryLimit(WorldInstanceId instanceId)
    {
        lock (_gate)
        {
            if (_medusaLeaderUi.TryGetValue(instanceId, out var leader))
            {
                return leader.DailyEntryLimit;
            }
            foreach (var entry in _medusaMemberUi)
            {
                if (entry.Key.Instance == instanceId)
                {
                    return entry.Value.DailyEntryLimit;
                }
            }
            return 0;
        }
    }
}
