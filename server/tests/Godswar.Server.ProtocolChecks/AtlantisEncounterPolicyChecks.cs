using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisEncounterPolicyChecks
{
    public const string CheckName = "Atlantis forty-minute and team-point encounter policy";

    public static Task RunAsync()
    {
        Check.Equal(TimeSpan.FromMinutes(40), AtlantisEncounterPolicy.TimeLimit,
            "Atlantis duration is forty minutes");
        Check.Equal(850, AtlantisEncounterPolicy.CompletionTeamPoints,
            "Atlantis completion requires 850 team points");
        foreach (var (rank, expected) in new[]
            { (AtlantisMonsterRank.Normal, 1), (AtlantisMonsterRank.Elite, 10), (AtlantisMonsterRank.Boss, 50) })
        {
            Check.True(AtlantisEncounterPolicy.TryGetKillPoints(rank, out var actual),
                $"{rank} has an explicit score");
            Check.Equal(expected, actual, $"{rank} kill points");
        }
        foreach (var rank in new[] { (AtlantisMonsterRank)0, (AtlantisMonsterRank)255 })
        {
            Check.True(!AtlantisEncounterPolicy.TryGetKillPoints(rank, out var points) && points == 0,
                "unknown rank never receives fallback points");
        }
        Check.True(AtlantisEncounterPolicy.TryParseMonsterRank("ELITE", out var elite) &&
            elite == AtlantisMonsterRank.Elite, "published rank casing does not change its identity");
        foreach (var rank in new string?[] { null, "", "pet", "miniboss", "1", " boss " })
        {
            Check.True(!AtlantisEncounterPolicy.TryParseMonsterRank(rank, out _),
                "unrecognized rank text is not inferred from another enemy category");
        }

        var descriptor = AtlantisRunRuntimeChecks.Descriptor();
        Check.True(AtlantisEncounterPolicy.IsAtlantisInstance(descriptor),
            "map 205 dungeon is Atlantis");
        foreach (var other in new[]
            { AtlantisRunRuntimeChecks.Descriptor(204), AtlantisRunRuntimeChecks.Descriptor(205, InstanceKind.OpenWorld) })
        {
            Check.True(!AtlantisEncounterPolicy.IsAtlantisInstance(other),
                "map or dungeon-kind mismatch is outside Atlantis");
            Check.Throws<ArgumentException>(() => new AtlantisRunRuntime(other, descriptor.CreatedAt),
                "runtime construction refuses a different content owner");
        }
        Check.Throws<ArgumentOutOfRangeException>(
            () => new AtlantisRunRuntime(descriptor, descriptor.CreatedAt.AddTicks(-1)),
            "runtime cannot start before the instance exists");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new AtlantisRunRuntime(descriptor, DateTimeOffset.MaxValue),
            "deadline overflow is rejected");
        return Task.CompletedTask;
    }
}
