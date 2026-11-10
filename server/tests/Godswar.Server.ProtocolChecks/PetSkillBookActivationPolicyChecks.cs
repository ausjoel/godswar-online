using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSkillBookActivationPolicyChecks
{
    public const string CheckName =
        "Authoritative reviewed pet skill-book policy";

    private static readonly ReviewedBook[] Expected =
    [
        .. Family(10_464, 408, [3_900, 3_904, 3_908, 3_912, 3_916, 3_920]),
        .. Family(10_510, 412, [4_500, 4_503, 4_507, 4_511, 4_515, 4_519]),
        .. Family(10_530, 413, [4_600, 4_604, 4_608, 4_612, 4_616, 4_620]),
        .. Family(10_590, 419, [5_200, 5_204, 5_208, 5_212, 5_216, 5_220]),
        .. Family(10_700, 423, [5_600, 5_604, 5_608, 5_612, 5_616, 5_620])
    ];

    public static Task RunAsync()
    {
        var items = TestItemContent.Catalog;
        var skills = PetLearnedSkillContentBaseline.Create();
        Check.Equal(30, Expected.Length, "reviewed skill-book fixture count");
        Check.Equal(
            390,
            PetSkillBookActivationPolicy.ReviewedBookCount,
            "every stock skill book is reviewed");
        foreach (var expected in Expected)
        {
            Check.True(
                PetSkillBookActivationPolicy.IsReviewedItem(expected.ItemId) &&
                PetSkillBookActivationPolicy.TryResolve(
                    items,
                    skills,
                    expected.ItemId,
                    out var actual) &&
                actual.ItemId == expected.ItemId &&
                actual.FamilyType == expected.FamilyType &&
                actual.Priority == expected.Priority &&
                actual.RuntimeSkillId == expected.RuntimeSkillId &&
                actual.TraitRequirement == ExpectedTrait(
                    expected.FamilyType,
                    expected.Priority),
                $"reviewed skill book {expected.ItemId} is pinned exactly");
        }

        CheckReviewedFamilyTotals(items, skills);
        CheckSpeciesRestrictions(items, skills);
        CheckUnreviewedItemsFailClosed(items, skills);
        CheckTamperedMetadataFailsClosed(items, skills);
        CheckReceiptRoundTrip(items, skills);
        CheckSoulContractTraitThreshold(skills);

        Check.True(
            PetSkillFamilyCatalog.TryGetByInitialRuntimeSkillId(
                3_900,
                out var wildBump) &&
            wildBump.HasSkillBooks &&
            PetSkillFamilyCatalog.BookBackedFamilyCount == 58,
            "Wild Bump is included in the reviewed book-backed catalog");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The two client-transcribed items that the generated catalogue stops
    /// short of must resolve through the same activation path.
    /// </summary>
    private static void CheckReviewedFamilyTotals(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog skills)
    {
        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_745,
                out var spikyArmor) &&
            spikyArmor.FamilyType == 427 &&
            spikyArmor.Priority == 6 &&
            spikyArmor.RuntimeSkillId == 6_020,
            "Spiky Armor VI resolves as the sixth tier of family 427");
        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                16_400,
                out var vampiric) &&
            vampiric.FamilyType == 428 &&
            vampiric.Priority == 1 &&
            vampiric.RuntimeSkillId == 6_400 &&
            vampiric.RestrictedSpeciesId == 0,
            "Vampiric I resolves as a shared family-428 book");
    }

    private static void CheckSpeciesRestrictions(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog skills)
    {
        Check.Equal(
            0,
            PetSkillBookActivationPolicy.ResolveRestrictedSpeciesId(0),
            "Vital Boost is shared by every species");
        Check.Equal(
            8,
            PetSkillBookActivationPolicy.ResolveRestrictedSpeciesId(62),
            "Dark Vengeance belongs to Ghost");
        Check.Equal(
            5,
            PetSkillBookActivationPolicy.ResolveRestrictedSpeciesId(81),
            "Concentration belongs to Easter Bunny");
        Check.Equal(
            40,
            PetSkillBookActivationPolicy.ResolveRestrictedSpeciesId(423),
            "Resolute Physique belongs to Kratortle");
        Check.Equal(
            44,
            PetSkillBookActivationPolicy.ResolveRestrictedSpeciesId(427),
            "Spiky Armor belongs to Hedgehog");
        Check.Equal(
            0,
            PetSkillBookActivationPolicy.ResolveRestrictedSpeciesId(428),
            "Vampiric is shared");

        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_464,
                out var wildBump),
            "the innate-family fixture resolves");
        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_510,
                out var wildStrength),
            "the foreign-family fixture resolves");
        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_745,
                out var spikyArmor),
            "the Hedgehog-family fixture resolves");
        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                16_405,
                out var vampiricSix),
            "the shared-family fixture resolves");
        // A Kritox pet keeps its innate Wild Bump and may learn the shared
        // Vampiric family, but never another species' exclusive family.
        Check.True(
            PetSkillBookActivationPolicy.CanSpeciesLearn(
                25,
                wildBump.FamilyType,
                wildBump) &&
            PetSkillBookActivationPolicy.CanSpeciesLearn(
                25,
                wildBump.FamilyType,
                vampiricSix) &&
            !PetSkillBookActivationPolicy.CanSpeciesLearn(
                25,
                wildBump.FamilyType,
                wildStrength),
            "a Kritox pet may learn its own and shared families only");
        // A species change keeps every learned family and opens only the
        // books of the species the pet now is.
        Check.True(
            PetSkillBookActivationPolicy.CanSpeciesLearn(
                5,
                62,
                vampiricSix) &&
            !PetSkillBookActivationPolicy.CanSpeciesLearn(
                5,
                62,
                spikyArmor),
            "after a species change only the new species' books open");
        // Cupid is born with the Hedgehog family, so it must be able to
        // advance that innate skill even though the client text names
        // Hedgehog as the owning species.
        Check.True(
            PetSkillBookActivationPolicy.CanSpeciesLearn(
                45,
                427,
                spikyArmor),
            "an innate family stays learnable for its own species");
    }

    private static void CheckUnreviewedItemsFailClosed(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog skills)
    {
        Check.True(
            !PetSkillBookActivationPolicy.IsReviewedItem(10_199) &&
            !PetSkillBookActivationPolicy.IsReviewedItem(10_746) &&
            !PetSkillBookActivationPolicy.IsReviewedItem(16_406) &&
            !PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_199,
                out _),
            "non-book neighbors fail closed");
    }

    private static void CheckSoulContractTraitThreshold(
        IPetLearnedSkillContentCatalog skills)
    {
        var raw = new PetSavvy(
            Agility: 0m,
            Strength: 56m,
            Accuracy: 0m,
            Technique: 0m,
            Wisdom: 0m,
            Luck: 0m);
        var effective = PetSoulContractPolicy.ResolveDisplayedTotal(
            raw,
            stage: 6);
        Check.True(
            !PetLearnedSkillResolver.CanLearn(
                skills,
                familyType: 408,
                targetPriority: 2,
                currentlyLearnedPriority: 1,
                raw,
                out var rawRejection) &&
            rawRejection ==
                PetSkillLearnRejection.TraitRequirementNotMet &&
            PetLearnedSkillResolver.CanLearn(
                skills,
                familyType: 408,
                targetPriority: 2,
                currentlyLearnedPriority: 1,
                effective,
                out var effectiveRejection) &&
            effectiveRejection == PetSkillLearnRejection.None &&
            effective.Strength == 64m,
            "Soul Contract displayed Savvy satisfies skill-book Trait thresholds");
    }

    private static void CheckTamperedMetadataFailsClosed(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog skills)
    {
        var tamperedDefinitions = items.All.Select(definition =>
            definition.Id == 10_465
                ? definition with
                {
                    StatsJson = definition.StatsJson.Replace(
                        "3904",
                        "3905",
                        StringComparison.Ordinal)
                }
                : definition).ToArray();
        var tampered = PinnedItemTemplateCatalog.Create(
            "tampered-pet-skill-book-check",
            tamperedDefinitions);
        Check.True(
            !PetSkillBookActivationPolicy.TryResolve(
                tampered,
                skills,
                10_465,
                out _),
            "published item metadata cannot redirect an allow-listed book");
    }

    private static void CheckReceiptRoundTrip(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog skills)
    {
        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_465,
                out var book),
            "receipt fixture resolves the reviewed book");
        var evidence = new PetSkillLearnEvidence(
            PetId: 7,
            ItemInstanceId: 11,
            ItemTemplateId: book.ItemId,
            SpeciesId: 25,
            FamilyType: book.FamilyType,
            PreviousPriority: 1,
            LearnedPriority: book.Priority,
            PreviousRuntimeSkillId: 3_900,
            LearnedRuntimeSkillId: book.RuntimeSkillId,
            SkillSlot: 0,
            TraitRequirement: book.TraitRequirement,
            TraitsAtLearnTime: new PetContentStatVector(
                0m,
                64m,
                0m,
                0m,
                0m,
                0m),
            ItemContentRevision: items.Revision.Sha256,
            LearnedSkillContentRevision: skills.Revision.Sha256);
        var receipt = new PetDurableReceipt(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.PetSkillLearned,
            AccountId: 1,
            CharacterId: 2,
            KitBagSlot: 25,
            EquipmentSlot: -1,
            PetId: 7,
            PetLevel: 1,
            PetExperience: 0,
            PetRevision: 3,
            IsCarried: true,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 4,
            AuditReference: "pet-skill-book-policy-check",
            OutboxEventId: Guid.NewGuid(),
            SkillLearn: evidence);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.Decode(payload);
        Check.True(
            PetDurablePersistenceCodec.ReadContractVersion(payload) ==
                PetDurablePersistenceCodec.BagItemActivationContractVersion &&
            decoded == receipt,
            "bag activation v3 durably round-trips exact skill-learn evidence");
    }

    private static IEnumerable<ReviewedBook> Family(
        uint firstItemId,
        int familyType,
        IReadOnlyList<int> runtimeSkillIds) =>
        runtimeSkillIds.Select((runtimeSkillId, index) => new ReviewedBook(
            checked(firstItemId + (uint)index),
            familyType,
            checked((short)(index + 1)),
            runtimeSkillId));

    private static PetSkillTraitRequirement ExpectedTrait(
        int familyType,
        short priority)
    {
        var threshold = priority switch
        {
            1 => 0m,
            2 => 64m,
            3 => 192m,
            4 => 235m,
            5 => 270m,
            6 => 305m,
            _ => throw new ArgumentOutOfRangeException(nameof(priority))
        };
        return familyType switch
        {
            408 or 419 => new(0m, threshold, 0m, 0m, 0m, 0m),
            412 or 413 => new(0m, 0m, threshold, 0m, 0m, 0m),
            423 => new(0m, 0m, 0m, 0m, threshold, 0m),
            _ => throw new ArgumentOutOfRangeException(nameof(familyType))
        };
    }

    private sealed record ReviewedBook(
        uint ItemId,
        int FamilyType,
        short Priority,
        int RuntimeSkillId);
}
