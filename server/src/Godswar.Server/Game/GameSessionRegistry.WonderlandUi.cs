using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<ClientSession, WonderlandUiStamp> _wonderlandUi = [];

    private async Task PublishWonderlandRunUiAsync(WorldInstanceRuntime runtime, WonderlandSnapshot run,
        WonderlandAdmission admission, IReadOnlyList<GameSessionContext> members,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var completed = run.State == WonderlandRunState.Completed;
        // An ended or timed-out run shows the same native leave countdown a
        // completed run does, and members are carried out when it expires.
        var ending = !completed && WonderlandCompletionPolicy.IsEndWindowOpen(run, now);
        if (completed || ending)
        {
            // The one end-of-run flow every dungeon shares: the same four
            // native frames, the same thirty-second countdown, the same
            // clicker-only leave and the same automatic exit. 飘渺 hands it its
            // own scene, its cleared islands and its own leave rule.
            await PublishInstanceRunEndAsync(
                BeginInstanceRunEnd(runtime.InstanceId, InstanceRunKind.Wonderland,
                    "Wonderland", WonderlandMapId, WonderlandClientSceneId,
                    admission.DailyLimit, run.CompletedIslands,
                    run.TerminalAt!.Value,
                    // A completed run keeps 飘渺's own five-minute treasure
                    // window; an ended or timed-out run uses the shared thirty
                    // seconds, exactly as the other three dungeons do.
                    window: completed
                        ? WonderlandCompletionPolicy.TreasureWindow
                        : WonderlandCompletionPolicy.EndWindow,
                    canLeave: () => !HasPendingWonderlandTitles(runtime.InstanceId)),
                [.. members.Select(ToInstanceRunEndMember)],
                now,
                settle: null,
                cancellationToken);
            return;
        }
        var remaining = checked((int)Math.Clamp(Math.Ceiling(
            (run.Deadline - run.LastObservedAt).TotalSeconds), 0, 2400));
        var roster = SnapshotInstanceRoster(
            runtime.InstanceId, InstanceRunKind.Wonderland, now);
        var signature = InstanceRosterSignature(roster);
        var stamp = new WonderlandUiStamp(runtime.InstanceId, run.State, run.CurrentIsland,
            run.CompletedIslands, remaining, signature);
        foreach (var member in members)
        {
            Task? write = null;
            lock (_gate)
            {
                if (!IsCurrentWonderlandMember(member, runtime.InstanceId)) continue;
                // A prior member's transport write may have yielded while the
                // leader cancelled or combat advanced the authoritative run.
                if (!TryGetWonderlandEncounterSnapshot(runtime.InstanceId, out var currentRun) ||
                    currentRun.State != run.State || currentRun.CurrentIsland != run.CurrentIsland ||
                    currentRun.CompletedIslands != run.CompletedIslands) continue;
                _wonderlandUi.TryGetValue(member.Session, out var previous);
                if (previous == stamp || previous?.InstanceId == stamp.InstanceId &&
                    (previous.CompletedIslands > stamp.CompletedIslands ||
                     previous.State == WonderlandRunState.Completed && completed ||
                     ending && previous.State == stamp.State)) continue;
                var packets = new List<ReadOnlyMemory<byte>>();
                if (previous?.InstanceId != stamp.InstanceId)
                    packets.Add(PacketBuilder.RepetitionSync(WonderlandClientSceneId, 0, 0, 5, admission.DailyLimit));
                if (previous?.InstanceId != stamp.InstanceId || previous.Roster != signature)
                    packets.Add(PacketBuilder.RepetitionInstanceMembers(roster));
                packets.Add(PacketBuilder.RepetitionFightInfo(remaining, run.CompletedIslands));
                // A terminal state never reaches this active-run publisher: the
                // panel-completion state and the leave countdown belong to the
                // one shared end-of-run flow.
                if (member.Session.TryAdmitExactBatch(packets, out var admitted)) _wonderlandUi[member.Session] = stamp;
                write = admitted;
            }
            if (write is not null)
            {
                try
                {
                    await write;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                { member.Session.Disconnect(); }
            }
        }
    }

    private sealed record WonderlandUiStamp(WorldInstanceId InstanceId, WonderlandRunState State,
        int Island, int CompletedIslands, int RemainingSeconds, string Roster);
}

