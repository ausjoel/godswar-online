using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisSpawnBalanceChecks
{
    public const string CheckName = "Atlantis bounded solo and party spawn balance";

    public static Task RunAsync()
    {
        (int CharacterId, int Level)[] solo = [(1, 90)];
        Check.Equal(new AtlantisSpawnStats(90, 15_000),
            AtlantisSpawnBalancePolicy.Resolve(solo, 1, AtlantisMonsterRank.Normal),
            "level-90 solo normal baseline remains explicit");
        Check.Equal(new AtlantisSpawnStats(90, 75_000),
            AtlantisSpawnBalancePolicy.Resolve(solo, 1, AtlantisMonsterRank.Elite),
            "elite HP uses its own authored baseline");
        Check.Equal(new AtlantisSpawnStats(90, 500_000),
            AtlantisSpawnBalancePolicy.Resolve(solo, 1, AtlantisMonsterRank.Boss),
            "boss HP uses its own authored baseline");

        var fullParty = Enumerable.Range(1, 5).Select(id => (id, 90)).ToArray();
        Check.Equal(new AtlantisSpawnStats(90, 54_000),
            AtlantisSpawnBalancePolicy.Resolve(fullParty, 5, AtlantisMonsterRank.Normal),
            "five-player final-stage normal has triple party HP and 20 percent stage increase");
        Check.Equal(new AtlantisSpawnStats(90, 1_800_000),
            AtlantisSpawnBalancePolicy.Resolve(fullParty, 5, AtlantisMonsterRank.Boss),
            "party size affects boss HP without increasing its level or point value");
        var strongest = new (int, int)[] { (1, 90), (2, 140) };
        var mixed = AtlantisSpawnBalancePolicy.Resolve(strongest, 1, AtlantisMonsterRank.Boss);
        Check.Equal(140u, mixed.Level, "mixed party pins the highest admitted level");
        Check.Equal(mixed,
            AtlantisSpawnBalancePolicy.Resolve(strongest.Reverse().ToArray(), 1, AtlantisMonsterRank.Boss),
            "party ordering cannot change difficulty");

        foreach (var level in new[] { 141, 160, 200 })
        {
            Check.Equal((uint)level,
                AtlantisSpawnBalancePolicy.Resolve([(1, level)], 1, AtlantisMonsterRank.Normal).Level,
                "players above the former ceiling retain their level for spawn balance");
        }
        Check.Equal(new AtlantisSpawnStats(160, 47_407),
            AtlantisSpawnBalancePolicy.Resolve([(1, 160)], 1, AtlantisMonsterRank.Normal),
            "level-160 solo HP continues the existing scaling formula");
        Check.Equal(new AtlantisSpawnStats(160, 5_688_888),
            AtlantisSpawnBalancePolicy.Resolve(
                Enumerable.Range(1, 5).Select(id => (id, 160)).ToArray(), 5, AtlantisMonsterRank.Boss),
            "level-160 parties can scale the final boss without an upper entry restriction");
        Check.Equal(new AtlantisSpawnStats(int.MaxValue, uint.MaxValue),
            AtlantisSpawnBalancePolicy.Resolve(
                Enumerable.Range(1, 5).Select(id => (id, int.MaxValue)).ToArray(), 5, AtlantisMonsterRank.Boss),
            "unbounded admission cannot overflow HP arithmetic or its packet field");

        foreach (var invalidParty in new (int, int)[][]
        {
            [], [(0, 90)], [(1, 89)], [(1, 90), (1, 100)],
            Enumerable.Range(1, 6).Select(id => (id, 90)).ToArray()
        })
        {
            ThrowsArgument(() => AtlantisSpawnBalancePolicy.Resolve(
                invalidParty, 1, AtlantisMonsterRank.Normal));
        }
        ThrowsArgument(() => AtlantisSpawnBalancePolicy.Resolve(solo, 0, AtlantisMonsterRank.Normal));
        ThrowsArgument(() => AtlantisSpawnBalancePolicy.Resolve(solo, 6, AtlantisMonsterRank.Normal));
        ThrowsArgument(() => AtlantisSpawnBalancePolicy.Resolve(solo, 1, (AtlantisMonsterRank)99));
        return Task.CompletedTask;
    }

    private static void ThrowsArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid Atlantis balance inputs must be rejected.");
    }
}
