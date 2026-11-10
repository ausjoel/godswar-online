using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetItemContentChecks
{
    public const string CheckName =
        "Immutable pet skill-slot and legacy talent-artifact content";

    public static Task RunAsync()
    {
        var seeds = PetItemContentBaseline.ItemTemplates;
        Check.Equal(
            ExpectedItems.Length,
            seeds.Count,
            "reviewed pet-item count");
        Check.True(
            seeds.Select(static value => value.Id)
                .SequenceEqual(ExpectedItems.Select(static value => value.Id)) &&
            seeds.Select(static value => value.Id).Distinct().Count() ==
                ExpectedItems.Length,
            "reviewed pet items have exact unique ordered IDs");

        foreach (var expected in ExpectedItems)
        {
            var seed = seeds.Single(value => value.Id == expected.Id);
            Check.True(
                seed.Kind == "consume item" &&
                seed.NameKey == expected.NameKey &&
                seed.DisplayName == expected.DisplayName &&
                seed.EquipmentSlot == 0 &&
                seed.ClassIds.Length == 0 &&
                seed.MinLevel is null &&
                seed.MaxLevel is null &&
                seed.Hand is null &&
                seed.SkillFlag is null &&
                seed.Texture == (expected.Texture ??
                    PetItemContentBaseline.Texture) &&
                seed.Icon == expected.Icon,
                $"pet item {expected.Id} keeps stock metadata");

            using var document = JsonDocument.Parse(seed.StatsJson);
            var stats = document.RootElement;
            Check.True(
                stats.GetProperty("ID").GetString() ==
                    expected.Id.ToString() &&
                stats.GetProperty("Type").GetString() == "consume item" &&
                stats.GetProperty("Texture").GetString() ==
                    (expected.Texture ?? PetItemContentBaseline.Texture) &&
                stats.GetProperty("Icon").GetString() == expected.Icon &&
                stats.GetProperty("Random").GetString() == "0" &&
                stats.GetProperty("Distribution").GetString() == "0,0" &&
                stats.GetProperty("Money").GetString() == "0" &&
                stats.GetProperty("Overlap").GetString() == expected.Overlap,
                $"pet item {expected.Id} keeps stock stats");
            AssertOptionalStat(stats, "Use", expected.Use, expected.Id);
            AssertOptionalStat(
                stats,
                "ItemType",
                expected.ItemType,
                expected.Id);
            AssertOptionalStat(
                stats,
                "Values",
                expected.Values,
                expected.Id);
            AssertOptionalStat(
                stats,
                "BindType",
                expected.BindType,
                expected.Id);
            AssertOptionalStat(
                stats,
                "Skill",
                expected.Skill,
                expected.Id);
            AssertOptionalStat(
                stats,
                "Mode",
                expected.Mode,
                expected.Id);
            AssertOptionalStat(
                stats,
                "Petlimit",
                expected.PetLimit,
                expected.Id);
            AssertOptionalStat(stats, "Food", expected.Food, expected.Id);
            AssertOptionalStat(stats, "Fill", expected.Fill, expected.Id);
            AssertOptionalStat(stats, "Favor", expected.Favor, expected.Id);
            AssertOptionalStat(
                stats,
                "PetSkill",
                expected.PetSkill,
                expected.Id);
            Check.Equal(
                8 +
                (expected.Use is null ? 0 : 1) +
                (expected.ItemType is null ? 0 : 1) +
                (expected.Values is null ? 0 : 1) +
                (expected.BindType is null ? 0 : 1) +
                (expected.Skill is null ? 0 : 1) +
                (expected.Mode is null ? 0 : 1) +
                (expected.PetLimit is null ? 0 : 1) +
                (expected.PetSkill is null ? 0 : 1) +
                (expected.Food is null ? 0 : 1) +
                (expected.Fill is null ? 0 : 1) +
                (expected.Favor is null ? 0 : 1),
                stats.EnumerateObject().Count(),
                $"pet item {expected.Id} has no invented stats");
        }

        var experienceItems = ExpectedItems
            .Where(static item =>
                PetExperienceItemPolicy.IsMorningDew(
                    checked((uint)item.Id)))
            .ToArray();
        Check.Equal(
            10,
            experienceItems.Length,
            "all five normal and restricted Morning Dew tiers are reviewed");
        foreach (var expected in experienceItems)
        {
            Check.True(
                PetExperienceItemPolicy.TryResolve(
                    TestItemContent.Catalog,
                    checked((uint)expected.Id),
                    out var resolved) &&
                resolved.Experience == long.Parse(expected.Values!) &&
                resolved.RequiresBoundPet ==
                    (expected.PetLimit == "1"),
                $"Morning Dew {expected.Id} resolves from pinned official content");
        }

        CheckSpecialPetShedDeveloperGrant();
        CheckPetConsumableDeveloperGrants();
        CheckMagicJadeItems(seeds);
        CheckPetCareItems();

        return Task.CompletedTask;
    }

    /// <summary>
    /// The pet-care consumables that the reviewed client-catalog family
    /// publishes must resolve, and the rules that depend only on the client's
    /// own numbers are asserted directly. Eleven further rows are transcribed
    /// from the same client table but are not in a published revision yet, so
    /// they must resolve as absent rather than throw.
    /// </summary>
    private static void CheckPetCareItems()
    {
        uint[] published =
        [
            4_060, 4_061, 4_062,
            10_002, 10_022, 10_042, 10_060,
            // Reviewed and republished so the mall and bound-gold
            // listings of them can actually complete a purchase.
            10_023, 10_043, 10_090
        ];
        foreach (var itemId in published)
        {
            Check.True(
                PetCareItemPolicy.TryResolvePublished(
                    TestItemContent.Catalog,
                    itemId,
                    out var resolved) &&
                resolved.ItemId == itemId,
                $"pet-care item {itemId} resolves from pinned official content");
        }

        Check.True(
            PetCareItemPolicy.TryResolvePublished(
                TestItemContent.Catalog,
                10_060,
                out var mellowWine) &&
            mellowWine.Kind == PetCareItemEffectKind.Amity &&
            mellowWine.Amity == 100,
            "the mellow wine grants 100 amity and no satiety");

        Check.True(
            PetCareItemPolicy.TryResolvePublished(
                TestItemContent.Catalog,
                4_062,
                out var holyWater) &&
            PetCareItemPolicy.ResolveEnergyGrant(holyWater, 100) == 95 &&
            PetCareItemPolicy.ResolveEnergyGrant(holyWater, 200) == 190,
            "energy waters grant a fraction of the pet's own maximum");

        // An unpublished reviewed row must be left alone, never resolved and
        // never an exception: the bag router has to fall through untouched.
        foreach (var itemId in PetCareItemPolicy.Items)
        {
            if (published.Contains(itemId))
            {
                continue;
            }
            Check.True(
                PetCareItemPolicy.IsReviewedItem(itemId) &&
                !PetCareItemPolicy.TryResolvePublished(
                    TestItemContent.Catalog,
                    itemId,
                    out _),
                $"unpublished pet-care item {itemId} resolves as absent");
        }

        // The mismatched-food-kind rule is fixed by PETDP_X0_19 and is
        // asserted on the published third tier, which shares the fourth
        // tier's Food/Fill/Favor shape.
        Check.True(
            PetCareItemPolicy.TryResolvePublished(
                TestItemContent.Catalog,
                10_002,
                out var mushroom) &&
            PetCareItemPolicy.ResolveSatiety(
                mushroom,
                PetCareItemPolicy.GrassFoodKind) == 40 &&
            PetCareItemPolicy.ResolveSatiety(
                mushroom,
                PetCareItemPolicy.MeatFoodKind) == 20 &&
            PetCareItemPolicy.ResolveAmity(
                mushroom,
                PetCareItemPolicy.GrassFoodKind) == 15 &&
            PetCareItemPolicy.ResolveAmity(
                mushroom,
                PetCareItemPolicy.MeatFoodKind) == 0,
            "the wrong food kind grants half satiety and no amity");
    }

    private static void CheckPetConsumableDeveloperGrants()
    {
        var grants = TestItemContent.Content.DeveloperItems;
        var expected = new[]
        {
            (Id: 10084u, Name: "Mysterious Tuck Net",
                Alias: "capturetool", Stack: (short)99),
            (Id: PetItemCatalog.MergedSpirit, Name: "Merged Spirit",
                Alias: "mergingspirit", Stack: (short)99),
            (Id: PetItemCatalog.RebirthSpirit, Name: "Rebirth Spirit",
                Alias: "rebirthspirit", Stack: (short)99),
            (Id: PetItemCatalog.ContractSpirit, Name: "Contract Spirit",
                Alias: "contractspirit", Stack: (short)99),
            (Id: PetItemCatalog.PixieTear, Name: "Pixie Tear",
                Alias: "pixietear", Stack: (short)99),
            (Id: PetItemCatalog.SpringWater, Name: "Spring Water",
                Alias: "springwater", Stack: (short)99),
            (Id: PetItemCatalog.EmptySealJade,
                Name: "Seal Jade (Empty)", Alias: "emptysealjade",
                Stack: (short)99),
            (Id: PetExperienceItemPolicy.LastMorningDew,
                Name: "Morning Dew 5", Alias: "mdew5", Stack: (short)99),
            (Id: PetItemCatalog.GenderReverser,
                Name: "Pet Gender Reverser", Alias: "genderreverser",
                Stack: (short)1)
        };

        foreach (var item in expected)
        {
            Check.True(
                grants.TryResolveDeveloper(item.Id, out var numeric) &&
                numeric.ItemId == item.Id &&
                numeric.DisplayName == item.Name &&
                numeric.StackCap == item.Stack &&
                numeric.GrantedBound == 0 &&
                grants.TryResolveDeveloper(item.Alias, out var alias) &&
                alias == numeric &&
                grants.TryResolveDeveloper(
                    $"pet{item.Id}",
                    out var nameKey) &&
                nameKey == numeric,
                $"pet developer grant {item.Id} is exact and unbound");
        }

        var operationId = Guid.NewGuid();
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item add mdew5 1 op={operationId:D}",
                out var request,
                out var error,
                TestItemContent.Content.DeveloperMounts,
                grants) &&
            string.IsNullOrEmpty(error) &&
            request is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: PetExperienceItemPolicy.LastMorningDew,
                Material.StackCap: 99,
                Material.GrantedBound: 0,
                Quantity: 1
            } &&
            request.ClientOperationId == operationId,
            "Morning Dew 5 is grantable through generic /item add");
    }

    private static void CheckSpecialPetShedDeveloperGrant()
    {
        var content = TestItemContent.Content;
        Check.True(
            content.DeveloperItems.TryResolveDeveloper(
                PetItemCatalog.SpecialPetShed,
                out var numeric) &&
            numeric.ItemId == PetItemCatalog.SpecialPetShed &&
            numeric.DisplayName == "Special Pet Shed" &&
            numeric.StackCap == 1 &&
            numeric.GrantedBound == 1,
            "Special Pet Shed resolves through the narrow developer allowlist");
        foreach (var alias in new[]
                 {
                     "petshed",
                     "specialpetshed",
                     "addpetnum",
                     "Special Pet Shed"
                 })
        {
            Check.True(
                content.DeveloperItems.TryResolveDeveloper(
                    alias,
                    out var resolved) &&
                resolved == numeric,
                $"Special Pet Shed alias '{alias}' resolves exactly");
        }

        var operationId = Guid.NewGuid();
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item add petshed 1 op={operationId:D}",
                out var request,
                out var error,
                content.DeveloperMounts,
                content.DeveloperItems) &&
            string.IsNullOrEmpty(error) &&
            request is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: PetItemCatalog.SpecialPetShed,
                Material.StackCap: 1,
                Material.GrantedBound: 1,
                Quantity: 1
            } &&
            request.ClientOperationId == operationId,
            "Special Pet Shed developer grant retains its operation identity");
    }

    private static void AssertOptionalStat(
        JsonElement stats,
        string name,
        string? expected,
        int itemId)
    {
        var present = stats.TryGetProperty(name, out var value);
        Check.True(
            expected is null
                ? !present
                : present && value.GetString() == expected,
            $"pet item {itemId} keeps optional {name}");
    }

    private static readonly ExpectedItem[] ExpectedItems =
        CreateExpectedItems();

    private static ExpectedItem[] CreateExpectedItems() =>
    [
        E(
            4109,
            "AddPetNum",
            "Special Pet Shed",
            "432,972",
            "1",
            use: "1",
            bindType: "1",
            skill: "4720",
            mode: "4"),
        // Stock pet-care consumables. Values are the client's own
        // ItemBaseAttribute rows: Food kind 1 grass / 2 meat / 3 meal,
        // Fill satiety, Favor amity; the wines carry Favor only and the
        // lifetime spring carries Values.
        E(10023, "Pet10023", "Roast Meat", "108,936", "99",
            use: "1", itemType: "7", skill: "4721", food: "2", fill: "100", favor: "40"),
        E(10043, "Pet10043", "Royal Feast", "684,972", "99",
            use: "1", itemType: "7", skill: "4721", food: "3", fill: "100", favor: "40"),
        E(10084, "Pet10084", "Mysterious Tuck Net", "900,936", "99",
            use: "1", itemType: "0", skill: "4734"),
        E(10090, "Pet10090", "Pet Spring Water", "576,936", "99",
            use: "1", itemType: "8", values: "100"),
        E(10099, "Pet10099", "Pet Enhance Spring", "648,936", "99", "1", "5"),
        E(10100, "Pet10100", "Golden Apple Juice", "504,936", "99", "1", "1"),
        E(10101, "Pet10101", "Strong Purge Potion", "612,936", "99"),
        E(10102, "Pet10102", "Weak Purge Potion", "468,936", "99", "1", "2"),
        E(10103, "Pet10103", "Merged Spirit", "756,972", "99", itemType: "15"),
        E(10104, "Pet10104", "Rebirth Spirit", "792,972", "99", itemType: "16"),
        E(10105, "Pet10105", "Contract Spirit", "828,972", "99", itemType: "17"),
        E(10106, "Pet10106", "Pixie Tear", "864,972", "99"),
        E(10107, "Pet10107", "Spring Water", "900,972", "99", use: "1", itemType: "12"),
        E(10108, "Pet10108", "Seal Jade (Empty)", "936,972", "99"),
        E(10109, "Pet10109", "Seal Jade(Packed)", "972,972", "1", use: "1", itemType: "13"),
        E(10110, "Pet10110", "Stick: Random Event", "720,936", "1"),
        E(10111, "Pet10111", "Stick: Quest Dispatch", "720,936", "1"),
        E(10112, "Pet10112", "Stick: Work", "720,936", "1"),
        E(10113, "Pet10113", "Stick: Healing", "720,936", "1"),
        E(10114, "Pet10114", "Stick: Merge", "720,936", "1"),
        E(10130, "Pet10130", "Morning Dew 1", "0,756", "99", "1", "18", "10000", skill: "4721"),
        E(10131, "Pet10131", "Morning Dew 2", "36,756", "99", "1", "18", "80000", skill: "4721"),
        E(10132, "Pet10132", "Morning Dew 3", "72,756", "99", "1", "18", "1000000", skill: "4721"),
        E(10133, "Pet10133", "Morning Dew 4", "108,756", "99", "1", "18", "2000000", skill: "4721"),
        E(10134, "Pet10134", "Morning Dew 5", "144,756", "99", "1", "18", "10000000", skill: "4721"),
        E(10140, "Pet10140", "Morning Dew 1 (Restricted)", "0,756", "99", "1", "18", "10000", skill: "4721", petLimit: "1"),
        E(10141, "Pet10141", "Morning Dew 2 (Restricted)", "36,756", "99", "1", "18", "80000", skill: "4721", petLimit: "1"),
        E(10142, "Pet10142", "Morning Dew 3 (Restricted)", "72,756", "99", "1", "18", "1000000", skill: "4721", petLimit: "1"),
        E(10143, "Pet10143", "Morning Dew 4 (Restricted)", "108,756", "99", "1", "18", "2000000", skill: "4721", petLimit: "1"),
        E(10144, "Pet10144", "Morning Dew 5 (Restricted)", "144,756", "99", "1", "18", "10000000", skill: "4721", petLimit: "1"),
        .. CreateExpectedPetSkillBookItems(),
        E(11003, "Pet11003", "Charm: Pet Call", "432,756", "1", use: "1", itemType: "20", skill: "4721"),
        E(11004, "Pet11004", "Charm: Merge", "864,936", "1", use: "1", itemType: "21", skill: "4721"),
        E(11015, "Pet11015", "Pet Gender Reverser", "72,900", "1",
            texture: "./Localization/en_us/UI/Texture/Icon.gwo"),
        .. CreateExpectedMagicJadeItems(),
        .. CreateExpectedVampiricBookItems()
    ];

    private static ExpectedItem E(
        int id,
        string nameKey,
        string displayName,
        string icon,
        string overlap,
        string? use = null,
        string? itemType = null,
        string? values = null,
        string? bindType = null,
        string? skill = null,
        string? mode = null,
        string? petLimit = null,
        string? petSkill = null,
        string? texture = null,
        string? food = null,
        string? fill = null,
        string? favor = null) =>
        new(
            id,
            nameKey,
            displayName,
            icon,
            overlap,
            use,
            itemType,
            values,
            bindType,
            skill,
            mode,
            petLimit,
            petSkill,
            texture,
            food,
            fill,
            favor);

    private sealed record ExpectedItem(
        int Id,
        string NameKey,
        string DisplayName,
        string Icon,
        string Overlap,
        string? Use,
        string? ItemType,
        string? Values,
        string? BindType,
        string? Skill,
        string? Mode,
        string? PetLimit,
        string? PetSkill,
        string? Texture,
        string? Food = null,
        string? Fill = null,
        string? Favor = null);
}
