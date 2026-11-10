using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal enum AtlantisRunState : byte
{
    Active = 1,
    Completed = 2,
    TimedOut = 3,
    Cancelled = 4
}

internal enum AtlantisKillOutcome : byte
{
    Applied = 1,
    Completed = 2,
    DuplicateKill = 3,
    UnknownRank = 4,
    InvalidMonsterIdentity = 5,
    WrongInstance = 6,
    TimestampMovedBackward = 7,
    RunNotStarted = 8,
    RunNotActive = 9,
    TimedOut = 10
}

internal sealed record AtlantisRunSnapshot(
    WorldInstanceId WorldInstanceId,
    MapId ContentMapId,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    DateTimeOffset LastObservedAt,
    AtlantisRunState State,
    int TeamPoints,
    int ScoredKillCount,
    DateTimeOffset? TerminalAt);

internal readonly record struct AtlantisKillResult(
    AtlantisKillOutcome Outcome,
    int PointsAwarded,
    AtlantisRunSnapshot? Snapshot);
