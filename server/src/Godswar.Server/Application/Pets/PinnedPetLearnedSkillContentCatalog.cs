using System.Collections.Frozen;

namespace Godswar.Server.Application.Pets;

internal sealed class PinnedPetLearnedSkillContentCatalog :
    IPetLearnedSkillContentCatalog
{
    // Counts include the family-428 Vampiric extension published alongside the
    // installed curve data, so they must track PetLearnedSkillContentBaseline.
    public const int ExpectedFamilyCount = 68;
    public const int ExpectedCurveCount = 390;
    public const int ExpectedStepCount = 1661;
    private const int VampiricFamilyType = 428;

    private readonly FrozenDictionary<
        (int FamilyType, short Priority),
        PetLearnedSkillCurveContentDefinition> _byKey;
    private readonly FrozenDictionary<
        int,
        PetLearnedSkillCurveContentDefinition> _byRuntimeSkillId;

    private PinnedPetLearnedSkillContentCatalog(
        PetLearnedSkillContentRevision revision,
        PetLearnedSkillCurveContentDefinition[] curves)
    {
        Revision = revision;
        Curves = Array.AsReadOnly(curves);
        _byKey = curves.ToFrozenDictionary(
            static value => (value.FamilyType, value.Priority));
        _byRuntimeSkillId = curves
            .SelectMany(curve => curve.Steps.Select(step => (curve, step)))
            .ToFrozenDictionary(
                static value => value.step.RuntimeSkillId,
                static value => value.curve);
    }

    public PetLearnedSkillContentRevision Revision { get; }

    public IReadOnlyList<PetLearnedSkillCurveContentDefinition> Curves
        { get; }

    public bool TryGetCurve(
        int familyType,
        int priority,
        out PetLearnedSkillCurveContentDefinition definition)
    {
        if (priority is >= short.MinValue and <= short.MaxValue &&
            _byKey.TryGetValue(
                (familyType, checked((short)priority)),
                out definition!))
        {
            return true;
        }
        definition = null!;
        return false;
    }

    public bool TryGetCurveByRuntimeSkillId(
        int runtimeSkillId,
        out PetLearnedSkillCurveContentDefinition definition) =>
        _byRuntimeSkillId.TryGetValue(runtimeSkillId, out definition!);

    public static PinnedPetLearnedSkillContentCatalog Create(
        string source,
        string sourceSha256,
        IReadOnlyList<PetLearnedSkillCurveContentDefinition> curves,
        string? expectedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ValidateDigest(sourceSha256, nameof(sourceSha256));
        ArgumentNullException.ThrowIfNull(curves);
        var snapshot = curves
            .Select(static curve => curve with
            {
                Steps = Array.AsReadOnly(curve.Steps
                    .OrderBy(static step => step.StepOrder).ToArray())
            })
            .OrderBy(static value => value.FamilyType)
            .ThenBy(static value => value.Priority)
            .ToArray();
        Validate(snapshot);
        var revision = PetLearnedSkillContentHasher.Compute(
            sourceSha256,
            snapshot);
        if (expectedRevision is not null &&
            !revision.Equals(expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Published learned pet-skill content does not match its " +
                $"revision (expected {expectedRevision}, computed {revision}).");
        }
        return new(
            new PetLearnedSkillContentRevision(
                revision,
                snapshot.Length,
                snapshot.Sum(static value => value.Steps.Count),
                source,
                sourceSha256),
            snapshot);
    }

    private static void Validate(
        PetLearnedSkillCurveContentDefinition[] curves)
    {
        // The installed-only projection predates the Vampiric extension, so the
        // pinned counts follow whichever baseline produced this snapshot.
        var hasVampiric = curves.Any(static curve =>
            curve.FamilyType == VampiricFamilyType);
        var expectedFamilies = hasVampiric ? ExpectedFamilyCount : 67;
        var expectedCurves = hasVampiric ? ExpectedCurveCount : 384;
        var expectedSteps = hasVampiric ? ExpectedStepCount : 1655;
        if (curves.Length != expectedCurves ||
            curves.Select(static value => value.FamilyType)
                .Distinct().Count() != expectedFamilies ||
            curves.Select(static value => (value.FamilyType, value.Priority))
                .Distinct().Count() != curves.Length ||
            curves.Sum(static value => value.Steps.Count) != expectedSteps ||
            (hasVampiric && curves.Count(static curve =>
                curve.FamilyType == VampiricFamilyType) != 6))
        {
            throw new InvalidDataException(
                "Learned pet-skill curves are incomplete or ambiguous.");
        }

        foreach (var family in curves.GroupBy(static value => value.FamilyType))
        {
            var priorities = family.Select(static value => (int)value.Priority)
                .Order().ToArray();
            if (!priorities.SequenceEqual(
                    Enumerable.Range(1, priorities.Length)))
            {
                throw new InvalidDataException(
                    $"Pet-skill family {family.Key} has a tier gap.");
            }
        }

        var runtimeIds = new HashSet<int>();
        foreach (var curve in curves)
        {
            if (curve.FamilyType < 0 || curve.Priority < 1 ||
                curve.Genre < 0 || curve.Effect < 0 ||
                curve.OpaqueAdd < 0 || curve.OpaqueFlag < 0 ||
                curve.FirstRuntimeSkillId <= 0 ||
                !curve.LearnTraitRequirement.IsValid ||
                (curve.FamilyType == VampiricFamilyType
                    ? curve.Steps.Count != 1 || curve.Effect != 34 ||
                      curve.Genre != 34 || curve.Steps[0].AbsoluteValue > 1m
                    : curve.Steps.Count is < 3 or > 5))
            {
                throw new InvalidDataException(
                    "A learned pet-skill curve is invalid.");
            }
            for (var index = 0; index < curve.Steps.Count; index++)
            {
                var step = curve.Steps[index];
                if (step.StepOrder != index ||
                    step.RuntimeSkillId != curve.FirstRuntimeSkillId + index ||
                    step.MinimumPetRank is < 0 or > 655 ||
                    (index == 0 && step.MinimumPetRank != 0) ||
                    (index > 0 && step.MinimumPetRank <=
                        curve.Steps[index - 1].MinimumPetRank) ||
                    step.AbsoluteValue <= 0m ||
                    !runtimeIds.Add(step.RuntimeSkillId))
                {
                    throw new InvalidDataException(
                        "A learned pet-skill rank step is invalid.");
                }
            }
        }
    }

    private static void ValidateDigest(string value, string name)
    {
        if (value.Length != 64 || value.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "Content digests must be uppercase SHA-256 values.",
                name);
        }
    }
}
