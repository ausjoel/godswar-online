using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset>
        _medusaTerminationExitRequested = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaTerminationEgressInFlight = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaTerminationExitSettled = [];

    private void RequestMedusaTerminationExit(
        WorldInstanceId worldInstanceId) =>
        _medusaTerminationExitRequested.TryAdd(
            worldInstanceId,
            DateTimeOffset.UtcNow);

    /// <summary>
    /// The members a terminal 美杜莎之岛 run still has to carry home, once the
    /// shared thirty-second countdown has expired.
    /// </summary>
    private MedusaTerminationEgress? CaptureMedusaTerminationEgress(
        WorldInstanceRuntime runtime,
        DateTimeOffset now)
    {
        if (_medusaTerminationExitSettled.ContainsKey(runtime.InstanceId))
        {
            return null;
        }

        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                    out var snapshot)
                ? snapshot
                : null);
        if (ownership is null)
        {
            return null;
        }
        var run = ownership.Run;
        if (run.State is not (
                MedusaRunState.TimedOut or
                MedusaRunState.VoluntarilyExited) ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership.ContentMapId.Value, out var clientSceneId))
        {
            return null;
        }

        // The run is terminal, and its members keep the same leave countdown a
        // completed run shows. Nobody's click carries the party out any more: the
        // member who pressed leave left on his own, through the shared
        // clicker-only leave, and the rest are carried out when the countdown
        // expires.
        var endStartedAt = run.State == MedusaRunState.TimedOut
            ? run.Deadline
            : _medusaTerminationExitRequested.TryGetValue(
                runtime.InstanceId, out var requestedAt)
                ? requestedAt
                : run.Deadline;
        // The settlement must never wait for the thirty-second window to elapse:
        // the members of a manually ended run leave through the shared
        // clicker-only leave, and an instance nobody is left inside is retired
        // before that window expires - waiting for it is exactly what kept
        // 美杜莎之岛's reward from ever being paid. The window still governs the
        // exit countdown; it does not gate payment.

        var members = new List<MedusaTerminationEgressMember>();
        lock (_gate)
        {
            foreach (var context in _sessions.Values)
            {
                if (!context.WorldReady ||
                    context.Session.IsDisconnected ||
                    context.WorldInstanceId != runtime.InstanceId ||
                    context.Character.CurrentMap != context.MapId ||
                    context.MapId is not (200 or 204) ||
                    !context.Ownership.IsValid ||
                    !IsCurrentAccountSession(
                        context.AccountId,
                        context.Session,
                        context.Ownership))
                {
                    continue;
                }

                members.Add(new(
                    context.Session,
                    context.CharacterId,
                    context.CharacterName,
                    context.WorldInstanceId,
                    context.MapId,
                    context.Ownership,
                    context.Character.Camp));
            }
        }

        // A run that ended early still pays the documented score tier it reached,
        // so the reward settles from the run's own final numbers. The run's own
        // scene and score go with it, for the one shared end-of-run flow.
        return new(
            runtime.InstanceId,
            runtime.RealmId,
            run.Difficulty,
            endStartedAt.ToUniversalTime(),
            endStartedAt.ToUniversalTime() - run.StartedAt.ToUniversalTime(),
            run.TeamScore,
            run.AdmittedCharacterIds.ToArray(),
            members,
            BeginInstanceRunEnd(runtime.InstanceId, InstanceRunKind.Medusa,
                "Medusa", runtime.MapId, checked((ushort)clientSceneId),
                MedusaDailyEntryLimit(runtime.InstanceId), run.TeamScore,
                endStartedAt.ToUniversalTime()));
    }

    private async Task PublishMedusaTerminationEgressAsync(
        MedusaTerminationEgress egress,
        CancellationToken cancellationToken)
    {
        if (!_medusaTerminationEgressInFlight.TryAdd(
                egress.SourceWorldInstanceId,
                0))
        {
            return;
        }

        try
        {
            // Pay before carrying anyone home. A failed or pending settlement is
            // retried by later ticks and never holds the egress hostage: being
            // carried home must not depend on a durable write.
            _ = await SettleMedusaTerminationRewardAsync(egress, cancellationToken);

            // The settlement above is immediate on purpose: a run whose members all
            // leave inside the countdown must still be paid, and waiting for the
            // window is exactly what kept this dungeon's reward from ever being
            // written. Carrying the rest home is not immediate - they keep the same
            // thirty-second window a completed run gives them, and only the members
            // still inside when it expires are moved. Without this guard the
            // leader's own click dragged his whole party out of the instance.
            if (DateTimeOffset.UtcNow <
                egress.Ending.EndedAt + egress.Ending.Window)
            {
                return;
            }

            // The one automatic exit every dungeon shares. Only the members this
            // capture still found inside are carried home; whoever pressed leave
            // during the countdown already left on his own.
            var allTransferred = await EgressInstanceRunEndAsync(
                egress.Ending,
                [.. egress.Members.Select(static member => new InstanceRunEndMember(
                    member.Session, member.CharacterId, member.Ownership,
                    member.Camp))],
                TransitionMedusaMemberHomeAsync,
                cancellationToken);

            if (allTransferred)
            {
                _medusaTerminationExitSettled.TryAdd(
                    egress.SourceWorldInstanceId,
                    0);
                _medusaTerminationExitRequested.TryRemove(
                    egress.SourceWorldInstanceId,
                    out _);
                ForgetInstanceRunEnd(egress.SourceWorldInstanceId);
            }
        }
        finally
        {
            _medusaTerminationEgressInFlight.TryRemove(
                egress.SourceWorldInstanceId,
                out _);
        }
    }

    /// <summary>
    /// Pays the tier a run that ended early actually reached, through the same
    /// durable settlement and projection the completed path uses.
    /// </summary>
    /// <remarks>
    /// The settlement is keyed by world instance, so a run can never be paid
    /// twice even if it is seen by both the completion and the termination paths.
    /// The synthesized completion marker carries the run's real final score and
    /// elapsed time, and never a selected title, so an incomplete run resolves to
    /// its documented incomplete tier and pays no title.
    /// </remarks>
    private Task<bool> SettleMedusaTerminationRewardAsync(
        MedusaTerminationEgress egress,
        CancellationToken cancellationToken)
    {
        var leaderId = InstanceRunLeaderCharacterId(egress.SourceWorldInstanceId);
        var leaderCharacterId = leaderId != 0
            ? leaderId
            : egress.AdmittedCharacterIds.FirstOrDefault();
        var rewardEgress = new MedusaCompletionEgress(
            egress.SourceWorldInstanceId,
            egress.RealmId,
            egress.Difficulty,
            leaderCharacterId,
            new MedusaRunCompletionMarker(
                egress.EndedAt,
                egress.Elapsed,
                egress.FinalScore,
                SelectedTitle: null),
            egress.AdmittedCharacterIds,
            egress.Members
                .Select(static member => new MedusaCompletionEgressMember(
                    member.Session,
                    member.CharacterId,
                    member.CharacterName,
                    member.SourceWorldInstanceId,
                    member.SourceMapId,
                    member.Ownership,
                    member.Camp))
                .ToArray(),
            ExitPlayers: false,
            egress.Ending,
            // The bosses never fell: this run keeps the score tier it reached.
            Completed: false);
        return SettleMedusaCompletionRewardAsync(
            rewardEgress,
            cancellationToken);
    }

    private sealed record MedusaTerminationEgress(
        WorldInstanceId SourceWorldInstanceId,
        RealmId RealmId,
        MedusaEncounterDifficulty Difficulty,
        DateTimeOffset EndedAt,
        TimeSpan Elapsed,
        int FinalScore,
        IReadOnlyList<int> AdmittedCharacterIds,
        IReadOnlyList<MedusaTerminationEgressMember> Members,
        InstanceRunEnding Ending);

    private readonly record struct MedusaTerminationEgressMember(
        ClientSession Session,
        int CharacterId,
        string CharacterName,
        WorldInstanceId SourceWorldInstanceId,
        byte SourceMapId,
        PlayerOwnershipFence Ownership,
        byte Camp);
}
