using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerExperiencePillPolicyChecks
{
    public const string CheckName = "EXP Pill grants its full million EXP with native sealing and level limits";

    public static Task RunAsync()
    {
        Check.Equal(4174U, PlayerExperienceItemPolicy.ItemId, "existing native EXP Pill identity is retained");
        Check.Equal(1_000_000, PlayerExperienceItemPolicy.ExperiencePerPill, "one pill grants the approved fixed amount");
        var threshold = PlayerExperienceCatalog.GetNextLevelExperience(80);
        Check.True(PlayerExperienceItemPolicy.TryApply(80, threshold - 1, false, out var ordinary),
            "unsealed pill accepts a full million EXP across a level threshold");
        Check.Equal(81, ordinary.Level, "ordinary pill follows the native next-level table");
        Check.Equal(999_999L, ordinary.Experience, "ordinary pill retains all EXP after the level threshold");
        Check.True(PlayerExperienceItemPolicy.TryApply(80, threshold - 1, true, out var sealedResult),
            "sealed player stores the complete pill value");
        Check.Equal(80, sealedResult.Level, "sealed level never advances");
        Check.Equal(threshold - 1L + 1_000_000, sealedResult.Experience, "sealed EXP is not reduced by thresholds");
        Check.Equal(0, sealedResult.LevelUps.Count, "sealed use has no level-up projection");
        Check.True(PlayerExperienceItemPolicy.TryApply(200, uint.MaxValue - 1_000_000L, true, out var saturated),
            "sealed level-200 exact final credit is allowed");
        Check.Equal((long)uint.MaxValue, saturated.Experience, "exact credit reaches the full native UInt32 ceiling");
        foreach (var state in new[] { (200, 0L, false), (80, uint.MaxValue - 999_999L, true),
            (200, (long)uint.MaxValue, true), (0, 0L, true), (201, 0L, false), (80, -1L, false),
            (80, (long)uint.MaxValue + 1, true) })
            Check.True(!PlayerExperienceItemPolicy.TryApply(state.Item1, state.Item2, state.Item3, out _),
                $"invalid or partially creditable EXP state {state} rejects without consumption");
        Check.True(PlayerExperienceItemPolicy.TryApply(1, 0, false, out var multiLevel) && multiLevel.LevelUps.Count > 1,
            "low-level use applies every crossed level threshold");
        return Task.CompletedTask;
    }
}
