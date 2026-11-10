namespace Godswar.Server.Application.WorldInstances;

internal static class WonderlandBossLootPolicy
{
    public const float InteractionRadius = 12f;
    public static readonly TimeSpan CorpseLifetime = TimeSpan.FromSeconds(20);

    public static IReadOnlyList<WonderlandBossLootDefinition> ForIsland(int island, byte partyCamp)
    {
        if (island is < 1 or > 8 || partyCamp is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(island));
        return WonderlandMonsterPlan.Create(island, 1, partyCamp)
            .Where(spawn => spawn.IsBoss && !spawn.IsAllied)
            .Select(spawn => new WonderlandBossLootDefinition(spawn.ObjectId, island, SackFor(spawn.Role)))
            .ToArray();
    }

    public static bool TryResolve(uint objectId, byte partyCamp, out WonderlandBossLootDefinition definition)
    {
        definition = default;
        if (partyCamp is not (0 or 1) || objectId < WonderlandMonsterPlan.FirstObjectId + 100 ||
            objectId >= WonderlandMonsterPlan.FirstObjectId + 900) return false;
        var island = checked((int)((objectId - WonderlandMonsterPlan.FirstObjectId) / 100));
        foreach (var candidate in ForIsland(island, partyCamp))
            if (candidate.MonsterObjectId == objectId)
            {
                definition = candidate;
                return true;
            }
        return false;
    }

    public static bool IsClaimWindowOpen(WonderlandSnapshot run, DateTimeOffset now) =>
        now >= run.StartedAt && (run.State == WonderlandRunState.Active && now < run.Deadline ||
            WonderlandCompletionPolicy.IsTreasureWindowOpen(run, now));

    public static bool IsCorpseClaimWindowOpen(WonderlandSnapshot run, DateTimeOffset diedAt, DateTimeOffset now) =>
        now >= diedAt && now < diedAt + CorpseLifetime && IsClaimWindowOpen(run, now);

    public static DateTimeOffset CorpseExpiresAt(WonderlandSnapshot run, DateTimeOffset diedAt)
    {
        var nativeExpiry = diedAt + CorpseLifetime;
        var runExpiry = run.State == WonderlandRunState.Completed && run.TerminalAt is { } completed
            ? completed + WonderlandCompletionPolicy.TreasureWindow
            : run.TerminalAt ?? run.Deadline;
        return nativeExpiry < runExpiry ? nativeExpiry : runExpiry;
    }

    private static uint SackFor(WonderlandMonsterRole role) => role switch
    {
        WonderlandMonsterRole.AlphaDemon => 4450,
        WonderlandMonsterRole.Derskey or WonderlandMonsterRole.Monkeyface => 4451,
        WonderlandMonsterRole.FlameRooster => 4452,
        WonderlandMonsterRole.RockSpirit => 4453,
        WonderlandMonsterRole.SpartanMarshal => 4454,
        WonderlandMonsterRole.AthenianMarshal => 4455,
        WonderlandMonsterRole.PlatinumDragon => 4456,
        WonderlandMonsterRole.MultiHead => 4457,
        WonderlandMonsterRole.Minotaur => 4458,
        WonderlandMonsterRole.Deer => 4459,
        WonderlandMonsterRole.DragonKing => 4460,
        WonderlandMonsterRole.ScorpionKing => 4461,
        _ => throw new InvalidOperationException("Wonderland boss has no original sack mapping.")
    };
}

internal readonly record struct WonderlandBossLootDefinition(uint MonsterObjectId, int Island, uint SackItemId);
