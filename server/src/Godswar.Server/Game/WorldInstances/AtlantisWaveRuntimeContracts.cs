using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal enum AtlantisWaveState : byte
{
    PendingPublication = 1,
    Active = 2,
    Completed = 3,
    TimedOut = 4,
    Cancelled = 5
}

internal readonly record struct AtlantisWaveToken(WorldInstanceId WorldInstanceId, int WaveIndex);

internal sealed record AtlantisWavePublication(AtlantisWaveToken Token, AtlantisWaveDefinition Definition);

internal readonly record struct AtlantisWaveMonsterIdentity(
    uint ObjectId, uint SpawnGeneration, AtlantisMonsterRank Rank);

internal sealed record AtlantisWaveSnapshot(
    WorldInstanceId WorldInstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    DateTimeOffset LastObservedAt,
    AtlantisWaveState State,
    int WaveIndex,
    int CompletedWaveCount,
    int CommittedKillCount,
    int RemainingMonsterCount,
    ImmutableArray<AtlantisWaveMonsterIdentity> BoundMonsters,
    DateTimeOffset? TerminalAt);

internal enum AtlantisWaveBindOutcome : byte
{
    Bound = 1,
    AlreadyBound = 2,
    WrongInstance = 3,
    WrongWave = 4,
    BindingConflict = 5,
    InvalidMonsterBindings = 6,
    IdentityReused = 7,
    TimestampMovedBackward = 8,
    RunNotActive = 9,
    TimedOut = 10
}

internal readonly record struct AtlantisWaveBindResult(
    AtlantisWaveBindOutcome Outcome, AtlantisWaveSnapshot Snapshot)
{
    public bool Succeeded => Outcome is AtlantisWaveBindOutcome.Bound or AtlantisWaveBindOutcome.AlreadyBound;
}

internal enum AtlantisWaveKillOutcome : byte
{
    Applied = 1,
    WaveCleared = 2,
    RunCompleted = 3,
    DuplicateKill = 4,
    WrongInstance = 5,
    UnknownMonster = 6,
    NoActiveWave = 7,
    TimestampMovedBackward = 8,
    RunNotActive = 9,
    TimedOut = 10
}

internal readonly record struct AtlantisWaveKillResult(
    AtlantisWaveKillOutcome Outcome, AtlantisWaveSnapshot Snapshot)
{
    public bool Accepted => Outcome is AtlantisWaveKillOutcome.Applied or
        AtlantisWaveKillOutcome.WaveCleared or AtlantisWaveKillOutcome.RunCompleted;
}
