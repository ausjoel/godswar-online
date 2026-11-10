using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class BloodfangPetContentChecks
{
    internal const string CheckName = "Bloodfang species content and V10 compatibility";
    internal const string V10Revision =
        "F5CC3B3EFAA33AB275AC35F8A3CF9FAB4DE26D7DAD3E13B90BAB88CC0B09F9FD";

    public static Task RunAsync()
    {
        var current = PetContentBaseline.Create();
        Check.True(current.Revision.Source == "reviewed-pet-baseline-v11" &&
            current.Species.Count == 46 && current.NativeProfiles.Count == 552 &&
            current.MergeRankSpeciesFactors.Count == 46,
            "Bloodfang release publishes 46 species and twelve profiles per species");
        Check.True(current.TryGetSpeciesByEggItemId(10194, out var bloodfang) &&
            bloodfang.SpeciesId == 46 && bloodfang.DisplayName == "Bloodfang" &&
            bloodfang.FoodKind == 2 && bloodfang.StarterSkillId == 6400 &&
            bloodfang.StarterSkillName == "Vampiric I" &&
            bloodfang.EggDeclaredSpeciesId == 46 && bloodfang.MagicJadeItemId == 11096 &&
            bloodfang.LifetimeValues.SequenceEqual(new[] { 1200 }),
            "Bloodfang egg resolves its own species and innate Vampiric I");
        Check.True(current.TryGetSpeciesByMagicJadeItemId(11096, out var appearance) &&
            appearance == bloodfang && !current.TryGetSpeciesByMagicJadeItemId(11095, out _) &&
            PetItemCatalog.FindRange(11096)?.Purpose == PetItemPurpose.SpeciesChange &&
            PetItemCatalog.TryGetCore(11095, out var ambrosia) &&
            ambrosia.Purpose == PetItemPurpose.Rebirth,
            "Bloodfang jade uses the explicit gap after Ambrosia of Rebirth");

        var profiles = PetNativeAptitudeProfileCatalog.All
            .Where(static value => value.SpeciesType == 46).ToArray();
        Check.True(profiles.Length == 11 && profiles.All(static value =>
            value.StarterSkillId == 6400 && value.NativeSkillCount == 3 &&
            value.Lifetime == 1200 && value.StartingTraits == PetSavvy.Zero &&
            value.GeniusTraits == PetSavvy.Zero && value.NativeQuality == 0 &&
            value.NativeSamsara == 0 && value.NativeGenius == 0 && value.NativeProcreate == 0),
            "all Bloodfang native aptitude rows preserve the authored creation profile");
        Check.True(current.TryGetNativeProfile(46, (short)PetAptitude.Calm, out var calm) &&
            calm.StarterSkillId == 6400 && calm.NativeSkillCount == 3 && calm.Lifetime == 1200,
            "Bloodfang Calm compatibility profile carries its own starter skill");

        var historical = CreateV10(current);
        Check.True(historical.Revision.Sha256 == V10Revision &&
            historical.Species.Count == 45 && historical.NativeProfiles.Count == 540 &&
            current.Revision.Sha256 != historical.Revision.Sha256,
            "all original species and profiles retain the exact sealed V10 content hash");
        Check.Throws<InvalidOperationException>(() => CopyWithSpecies(current,
            current.Species.Select(static value => value.SpeciesId == 46
                ? value with { MagicJadeItemId = 11095 } : value).ToArray()),
            "a Bloodfang mapping colliding with Ambrosia is rejected");
        Check.Throws<InvalidOperationException>(() => CopyWithSpecies(current,
            current.Species.Select(static value => value.SpeciesId == 46
                ? value with { EggItemId = 10150 } : value).ToArray()),
            "a Bloodfang egg colliding with an existing species is rejected");

        var migration = PostgresSchemaMigrationCatalog.All.Single(static value =>
            value.Id == "20260916_155_bloodfang_pet_species");
        Check.True(migration.Sql.Contains("ON CONFLICT (species_id) DO NOTHING", StringComparison.Ordinal) &&
            migration.Sql.Contains("RAISE EXCEPTION 'Bloodfang species 46 conflicts", StringComparison.Ordinal) &&
            migration.Sql.Contains("species_id = 46 AND magic_jade_item_id = 11096", StringComparison.Ordinal) &&
            migration.Sql.Contains("species_id BETWEEN 1 AND 45", StringComparison.Ordinal) &&
            !migration.Sql.Contains("UPDATE public.pet_content", StringComparison.Ordinal),
            "forward migration adds one guarded identity and preserves sealed historical definitions");
        return Task.CompletedTask;
    }

    internal static PinnedPetContentCatalog CreateV10(PinnedPetContentCatalog current) =>
        PinnedPetContentCatalog.Create("reviewed-pet-baseline-v10", current.Settings,
            current.Species.Where(static value => value.SpeciesId <= 45).ToArray(),
            current.Aptitudes,
            current.NativeProfiles.Where(static value => value.SpeciesId <= 45).ToArray(),
            current.ExperienceSteps, current.RebirthSteps, current.MergeSavvySteps,
            current.MergeSavvyLookup, current.HatchRankSteps, current.MergeRankLookup,
            current.MergeRankSpeciesFactors.Where(static value => value.SpeciesId <= 45).ToArray(),
            current.MergeRankSpiritSteps);

    private static PinnedPetContentCatalog CopyWithSpecies(PinnedPetContentCatalog current,
        PetSpeciesContentDefinition[] species) =>
        PinnedPetContentCatalog.Create(current.Revision.Source, current.Settings, species,
            current.Aptitudes, current.NativeProfiles, current.ExperienceSteps,
            current.RebirthSteps, current.MergeSavvySteps, current.MergeSavvyLookup,
            current.HatchRankSteps, current.MergeRankLookup, current.MergeRankSpeciesFactors,
            current.MergeRankSpiritSteps);
}
