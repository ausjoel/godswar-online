using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private readonly object _atlantisEncounterGate = new();
    private AtlantisRunRuntime? _atlantisRun;

    // Entry explicitly starts the clock before its first transfer. Repeated
    // admission cannot restart the clock, clear points, or revive a terminal run.
    internal bool TryStartAtlantisEncounter(DateTimeOffset startedAt, out AtlantisRunSnapshot snapshot)
    {
        lock (_atlantisEncounterGate)
        {
            if (_atlantisRun is not null)
            {
                snapshot = _atlantisRun.Snapshot();
                return true;
            }

            lock (_descriptorGate)
            {
                var descriptor = _descriptor;
                if (!AtlantisEncounterPolicy.IsAtlantisInstance(descriptor) ||
                    descriptor.LifecycleState is not
                        (WorldInstanceLifecycleState.Creating or WorldInstanceLifecycleState.Active))
                {
                    snapshot = null!;
                    return false;
                }
                _atlantisRun = new AtlantisRunRuntime(descriptor, startedAt);
                snapshot = _atlantisRun.Snapshot();
                return true;
            }
        }
    }

    internal bool TryGetAtlantisRunSnapshot(out AtlantisRunSnapshot snapshot)
    {
        lock (_atlantisEncounterGate)
        {
            snapshot = _atlantisRun?.Snapshot()!;
            return snapshot is not null;
        }
    }

    internal bool TryAdvanceAtlantisEncounter(DateTimeOffset now, out AtlantisRunSnapshot snapshot)
    {
        lock (_atlantisEncounterGate)
        {
            snapshot = _atlantisRun?.Advance(now)!;
            _atlantisWaves?.Advance(now);
            if (snapshot is not null && snapshot.State != AtlantisRunState.Active)
            {
                // A run that ran out of time still owes its members the tier its
                // score reached, so the evidence freezes here as well.
                FreezeAtlantisRewardOnTerminal(snapshot);
                StopAtlantisMonsterCombat();
            }
            return snapshot is not null;
        }
    }

    /// <summary>
    /// Freezes the reward evidence the first time the run terminalizes, whatever
    /// terminal state it reached.
    /// </summary>
    private void FreezeAtlantisRewardOnTerminal(AtlantisRunSnapshot snapshot)
    {
        if (snapshot.State == AtlantisRunState.Active)
        {
            return;
        }
        FreezeAtlantisRewardMembers(snapshot);
    }

    internal AtlantisKillResult RecordCommittedAtlantisMonsterKill(
        WorldInstanceId expectedInstanceId,
        uint objectId,
        uint spawnGeneration,
        string? rank,
        DateTimeOffset committedAt)
    {
        lock (_atlantisEncounterGate)
        {
            if (_atlantisRun is null)
            {
                return new(AtlantisKillOutcome.RunNotStarted, 0, null);
            }
            _ = AtlantisEncounterPolicy.TryParseMonsterRank(rank, out var parsedRank);
            if (_atlantisWaves is not null &&
                (!_atlantisWaves.TryGetActiveMonster(expectedInstanceId, objectId, spawnGeneration,
                    out var waveMonster) || waveMonster.Rank != parsedRank))
            {
                return new(AtlantisKillOutcome.InvalidMonsterIdentity, 0, _atlantisRun.Snapshot());
            }
            var result = _atlantisRun.RecordCommittedMonsterKill(
                expectedInstanceId, objectId, spawnGeneration, parsedRank, committedAt);
            if (result.PointsAwarded > 0 && _atlantisWaves is not null)
            {
                var waveKill = _atlantisWaves.RecordCommittedKill(
                    expectedInstanceId, objectId, spawnGeneration, committedAt);
                if (!waveKill.Accepted)
                {
                    throw new InvalidOperationException("Atlantis score and wave kill authority diverged.");
                }
            }
            if (result.Snapshot?.State != AtlantisRunState.Active)
            {
                FreezeAtlantisRewardOnTerminal(result.Snapshot!);
                _atlantisWaves?.Advance(committedAt);
                StopAtlantisMonsterCombat();
            }
            return result;
        }
    }

    internal bool TryCancelAtlantisEncounter(DateTimeOffset now, out AtlantisRunSnapshot snapshot)
    {
        lock (_atlantisEncounterGate)
        {
            snapshot = _atlantisRun?.Cancel(now)!;
            if (snapshot is null)
            {
                return false;
            }
            // The leader ended the run early: freeze what the party had earned.
            FreezeAtlantisRewardOnTerminal(snapshot);
            _atlantisWaves?.Cancel(now);
            StopAtlantisMonsterCombat();
            return true;
        }
    }
}
