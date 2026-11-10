using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class TalentExperienceProgressionChecks
{
    public const string CheckName = "Talent reward saturation preserves valid progression and credited value";

    public static Task RunAsync()
    {
        var normal = TalentExperienceCatalog.Apply(95, 7, 10);
        Check.True(normal == new TalentExperienceProgression(5, 8, 10, 1),
            "ordinary Talent EXP crosses its hundred-point threshold unchanged");
        var partial = TalentExperienceCatalog.Apply(95, int.MaxValue - 1, 250);
        Check.True(partial == new TalentExperienceProgression(99, int.MaxValue, 104, 1),
            "the final Talent Point and remainder credit only representable EXP");
        var cappedPoint = TalentExperienceCatalog.Apply(47, int.MaxValue, 100);
        Check.True(cappedPoint == new TalentExperienceProgression(99, int.MaxValue, 52, 0),
            "a full point balance can finish the valid remaining EXP bar");
        var capped = TalentExperienceCatalog.Apply(99, int.MaxValue, int.MaxValue);
        Check.True(capped == new TalentExperienceProgression(99, int.MaxValue, 0, 0),
            "a saturated balance accepts reward settlement without overflow or phantom credit");
        var large = TalentExperienceCatalog.Apply(99, 0, int.MaxValue);
        Check.True(large == new TalentExperienceProgression(46, 21_474_837, int.MaxValue, 21_474_837),
            "intermediate EXP arithmetic handles the full nonnegative integer award range");
        Check.Throws<ArgumentOutOfRangeException>(() => TalentExperienceCatalog.Apply(100, 0, 1),
            "an invalid persisted EXP sentinel is not silently converted into Talent Points");
        Check.Throws<ArgumentOutOfRangeException>(() => TalentExperienceCatalog.Apply(-1, 0, 1),
            "negative persisted EXP remains invalid");
        Check.Throws<ArgumentOutOfRangeException>(() => TalentExperienceCatalog.Apply(0, -1, 1),
            "negative persisted points remain invalid");
        Check.Throws<ArgumentOutOfRangeException>(() => TalentExperienceCatalog.Apply(0, 0, -1),
            "negative reward credit remains invalid");
        return Task.CompletedTask;
    }
}
