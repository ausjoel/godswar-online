using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetInnateTalentPolicyChecks
{
    public static Task RunAsync()
    {
        // The ladder is written down once, in PetInnateTalentPolicy. These are
        // the tier boundaries that rule promises, restated as literal values so
        // a silent change to the rule fails here instead of in a hatch.
        Check.Equal(
            (byte)0,
            PetInnateTalentPolicy.Resolve(PetAptitude.Calm),
            "tier 6 carries no innate talent");
        Check.Equal(
            (byte)31,
            PetInnateTalentPolicy.Resolve(PetAptitude.Grumpy),
            "tier 7 is the first tier that carries every innate talent");
        Check.Equal(
            (byte)26,
            PetInnateTalentPolicy.Resolve(PetAptitude.Brave),
            "tier 8 carries quest dispatch, healing and merge");
        Check.Equal(
            (byte)0,
            PetInnateTalentPolicy.Resolve(PetAptitude.Zealous),
            "tier 9 is deliberately not a granted tier and carries no talent");
        Check.Equal(
            (byte)26,
            PetInnateTalentPolicy.Resolve(PetAptitude.Smart),
            "tier 10 carries quest dispatch, healing and merge again");
        Check.Equal(
            (byte)26,
            PetInnateTalentPolicy.Resolve(PetAptitude.Almighty),
            "tier 13 still carries quest dispatch, healing and merge");
        Check.Equal(
            (byte)31,
            PetInnateTalentPolicy.Resolve(PetAptitude.Godly),
            "tier 14 carries every innate talent");

        foreach (var aptitude in PetAptitudeCatalog.All)
        {
            var expected = PetInnateTalentPolicy.Resolve(aptitude.Aptitude);
            foreach (var talent in PetTalentCatalog.All)
            {
                Check.Equal(
                    (expected & talent.MaskBit) != 0,
                    PetInnateTalentPolicy.HasTalent(
                        aptitude.Aptitude,
                        talent.Talent),
                    $"{aptitude.DisplayName}/{talent.DisplayName} membership");
            }
        }

        var baseline = PetContentBaseline.Create();
        foreach (var aptitude in baseline.Aptitudes)
        {
            Check.Equal(
                checked((short)PetInnateTalentPolicy.Resolve(
                    (PetAptitude)aptitude.Aptitude)),
                aptitude.InnateTalentMask,
                $"published aptitude {aptitude.Aptitude} pins its talent mask");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => PetInnateTalentPolicy.Resolve((PetAptitude)0),
            "unknown aptitude cannot receive an invented talent mask");
        Check.True(
            !PetInnateTalentPolicy.HasTalent(
                PetAptitude.Smart,
                (PetTalentKind)99),
            "unknown talents fail closed");
        return Task.CompletedTask;
    }
}
