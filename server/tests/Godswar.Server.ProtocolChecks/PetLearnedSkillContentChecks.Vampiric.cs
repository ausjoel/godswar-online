using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetLearnedSkillContentChecks
{
    private static void CheckVampiricContent(IPetLearnedSkillContentCatalog content)
    {
        var installed = PetLearnedSkillContentBaseline.CreateInstalled();
        Check.True(installed.Curves.Count == 384 && installed.Revision.StepCount == 1655 &&
            installed.Revision.Sha256 == PetLearnedSkillContentBaseline.InstalledRevision,
            "the reviewed installed predecessor remains independently reconstructable");
        Check.Equal(installed.Revision.Sha256, PetLearnedSkillContentHasher.Compute(
                installed.Revision.SourceSha256,
                content.Curves.Where(static curve => curve.FamilyType != 428).ToArray()),
            "every original curve and rank step is unchanged by the six-curve extension");
        var fractions = new[] { .06m, .08m, .11m, .14m, .17m, .20m };
        var accuracy = new[] { 0m, 64m, 192m, 235m, 270m, 305m };
        for (var index = 0; index < fractions.Length; index++)
        {
            var tier = index + 1;
            Check.True(content.TryGetCurve(428, tier, out var curve) &&
                curve.FirstRuntimeSkillId == 6400 + index && curve.Genre == 34 &&
                curve.Effect == 34 && curve.Steps.Count == 1 &&
                curve.LearnTraitRequirement == new PetSkillTraitRequirement(0, 0, accuracy[index], 0, 0, 0),
                $"Vampiric tier {tier} has its own runtime identity and Accuracy learning threshold");
            foreach (var rank in new[] { 0m, 30m, 90m, 655.35m })
            {
                Check.True(PetLearnedSkillResolver.TryResolveEffect(content, 428, tier, rank,
                        out var effect) && effect.AbsoluteValue == fractions[index] &&
                    effect.MinimumPetRank == 0 && effect.RuntimeSkillId == 6400 + index,
                    $"Vampiric tier {tier} stays at its fixed fraction at pet rank {rank}");
            }
            Check.True(PetLearnedSkillResolver.CanLearn(content, 428, tier, tier - 1,
                new PetSavvy(0, 0, accuracy[index], 0, 0, 0), out _),
                $"Vampiric tier {tier} accepts its exact trait threshold and preceding tier");
            if (tier > 1)
            {
                Check.True(!PetLearnedSkillResolver.CanLearn(content, 428, tier, tier - 1,
                        new PetSavvy(0, 0, accuracy[index] - .01m, 0, 0, 0), out var rejection) &&
                    rejection == PetSkillLearnRejection.TraitRequirementNotMet,
                    $"Vampiric tier {tier} rejects insufficient Accuracy");
            }
        }
        Check.True(!PetLearnedSkillResolver.CanLearn(content, 428, 6, 0,
                new PetSavvy(0, 0, 305, 0, 0, 0), out var skipped) &&
            skipped == PetSkillLearnRejection.PriorTierRequired,
            "Vampiric cannot skip preceding learned tiers");
        var invalid = content.Curves.Select(curve => curve.FamilyType == 428 && curve.Priority == 6
            ? curve with { Steps = [curve.Steps[0] with { AbsoluteValue = 1.01m }] }
            : curve).ToArray();
        Check.Throws<InvalidDataException>(() => PinnedPetLearnedSkillContentCatalog.Create(
            content.Revision.Source, content.Revision.SourceSha256, invalid),
            "a Vampiric fraction above 100 percent is rejected at the content boundary");
    }
}
