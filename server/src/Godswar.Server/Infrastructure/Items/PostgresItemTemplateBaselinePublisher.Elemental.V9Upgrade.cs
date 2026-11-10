using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private const string OfficialPetItemsV2Revision =
        "28B1C5C6C2F292755B564CAC9D7C651CA821391C6D4E8C03EAE0D01535D60BB4";
    private const string OfficialPetItemsV2Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v2";
    private const string OfficialPetItemsV3Revision =
        "BCF91FCD7A9E3C5EA93B774143B5D2F9B714B147E40EBF0B85C639CF0DD63057";
    private const string OfficialPetItemsV3Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v3";
    private const string OfficialWarehouseV1Revision =
        "2E324B0C9892CD0801CD0A93EEC3C0EC4E459317A23D8FF0FB090D5A901BD113";
    private const string OfficialWarehouseV1Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v4+nameplates-v1+warehouse-v1";
    private const string OfficialPetItemsV5Revision =
        "9A6D6087087937D57DAED7DD93871F02CAED74124166A5CC1EB69D86DBACD121";
    private const string OfficialPetItemsV5Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v5+nameplates-v1+warehouse-v1";

    private sealed record V9PublicationSnapshot(
        IReadOnlyList<ItemTemplateDefinition> Definitions,
        ItemPolicySnapshot Policies,
        HolySuitPolicySnapshot HolySuit);

    private static async Task<V9PublicationSnapshot> PrepareV9PublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState? existing,
        CancellationToken cancellationToken)
    {
        V8PublicationSnapshot prior;
        if (existing is { ManifestVersion: 9 })
        {
            await VerifyPublishedV9ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            ValidateSupportedPetItemsV9Predecessor(existing);
            prior = await ReadV8ShapeAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        }
        else if (existing is { ManifestVersion: 8 })
        {
            await VerifyPublishedV8ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            await ValidateOfficialV8ElementalReleaseAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            prior = await ReadV8ShapeAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        }
        else
        {
            prior = await PrepareV8PublicationAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
        }

        var definitions = await ReplaceReviewedElementalStoneItemsAsync(
            connection,
            transaction,
            prior.Definitions,
            cancellationToken);
        definitions = await AppendMissingReviewedSocketSpellsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReconcileReviewedHolyStoneMaterialsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReconcileReviewedPetItemsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReconcileReviewedNameplatesAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReconcileReviewedWarehouseItemsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReconcileReviewedOpalAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReconcileReviewedClientCatalogItemsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReplaceReviewedMountDefinitionsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        return new V9PublicationSnapshot(
            definitions,
            prior.Policies with
            {
                // The fourteen former family-specific stones stay as item
                // definition tombstones on upgrade, but no longer carry a
                // material policy in the authoritative v9 manifest.
                EnhancementMaterials = ReplaceElementalMaterialPolicies(
                    prior.Policies.EnhancementMaterials)
            },
            prior.HolySuit);
    }

    private static void ValidateSupportedPetItemsV9Predecessor(
        PublishedItemRevisionState release)
    {
        var isV2 = release.Revision.Equals(
                OfficialPetItemsV2Revision,
                StringComparison.Ordinal) &&
            release.Source.Equals(
                OfficialPetItemsV2Source,
                StringComparison.Ordinal);
        var isV3 = release.Revision.Equals(
                OfficialPetItemsV3Revision,
                StringComparison.Ordinal) &&
            release.Source.Equals(
                OfficialPetItemsV3Source,
                StringComparison.Ordinal);
        var isV4 = release.Revision.Equals(
                "1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF",
                StringComparison.Ordinal) &&
            release.Source.Equals(
                "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
                "zephyr-v1+mount-speed-v3+pets-v4",
                StringComparison.Ordinal);
        var isNameplatesV1 = release.Revision.Equals(
                "AC11E2A725B0450B93D9C71F021F2D95B19EB4204ACC8E36B54CFEF1F8B9A063",
                StringComparison.Ordinal) &&
            release.Source.Equals(
                "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
                "zephyr-v1+mount-speed-v3+pets-v4+nameplates-v1",
                StringComparison.Ordinal);
        var isWarehouseV1 = release.Revision.Equals(
                OfficialWarehouseV1Revision,
                StringComparison.Ordinal) &&
            release.Source.Equals(
                OfficialWarehouseV1Source,
                StringComparison.Ordinal);
        var isPetItemsV5 = release.Revision.Equals(
                OfficialPetItemsV5Revision,
                StringComparison.Ordinal) &&
            release.Source.Equals(
                OfficialPetItemsV5Source,
                StringComparison.Ordinal);
        // This publisher's own earlier lineages are always safe predecessors:
        // they already carry every reviewed family that the reconciliation
        // steps below re-apply, so re-applying them is a no-op. Each new
        // reviewed family appends one provenance suffix, so every lineage this
        // publisher ever sealed is a strict prefix of the current label.
        var isCurrentLineage =
            release.Source.Equals(
                PublicationSource,
                StringComparison.Ordinal) ||
            PublicationSource.StartsWith(
                release.Source + "+",
                StringComparison.Ordinal);
        if (!isV2 && !isV3 && !isV4 && !isNameplatesV1 &&
            !isWarehouseV1 && !isPetItemsV5 && !isCurrentLineage)
        {
            throw new InvalidOperationException(
                $"Manifest-v9 item revision {release.Revision} is not the " +
                "exact reviewed pets-v2/v3/v4, Nameplates-v1, or " +
                "Warehouse-v1/pets-v5 predecessor; pet items were not " +
                "reconciled.");
        }
    }

    private static IReadOnlyList<GearEnhancementMaterialDefinition>
        ReplaceElementalMaterialPolicies(
            IReadOnlyList<GearEnhancementMaterialDefinition> prior) =>
        prior.Where(static value => !IsElementalStoneRange(value.ItemId))
            .Concat(ReviewedEnhancementMaterials.Where(
                static value => IsElementalStoneRange(value.ItemId)))
            .OrderBy(static value => value.ItemId)
            .ToArray();

    private static bool IsElementalStoneRange(uint itemId) =>
        itemId is >= 16300 and <= 16320;

    private static async Task<V8PublicationSnapshot> ReadV8ShapeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken) =>
        new(
            await ReadPublishedDefinitionsAsync(
                connection, transaction, revision, cancellationToken),
            await ReadPublishedPoliciesAsync(
                connection, transaction, revision, cancellationToken),
            await ReadPublishedHolySuitPoliciesAsync(
                connection, transaction, revision, cancellationToken));

    private static async Task<bool> VerifyPublishedV9ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState release,
        CancellationToken cancellationToken)
    {
        if (release.ManifestVersion != 9)
        {
            throw new InvalidOperationException(
                $"Expected item manifest v9, found {release.ManifestVersion}.");
        }

        var definitions = await ReadPublishedDefinitionsAsync(
            connection, transaction, release.Revision, cancellationToken);
        var policies = await ReadPublishedPoliciesAsync(
            connection, transaction, release.Revision, cancellationToken);
        var holySuit = await ReadPublishedHolySuitPoliciesAsync(
            connection, transaction, release.Revision, cancellationToken);
        if (definitions.Count != release.EntryCount ||
            policies.Attributes.Count != release.AttributeCount ||
            policies.EquipmentRanks.Count != release.EquipmentRankCount ||
            policies.HolySuitEffects.Count != release.HolySuitEffectCount ||
            policies.MaterialPolicyCount != release.MaterialPolicyCount ||
            policies.MaterialRecipeCount != release.MaterialRecipeCount ||
            holySuit.Tiers.Count != release.HolySuitTierCount ||
            holySuit.Upgrades.Count != release.HolySuitUpgradeCount ||
            holySuit.Consumables.Count != release.HolySuitConsumableCount ||
            release.HolySuitPolicyCount != 1)
        {
            throw new InvalidOperationException(
                $"Published item-content revision {release.Revision} has " +
                $"counts that disagree with its own header: definitions " +
                $"{definitions.Count}/{release.EntryCount}, attributes " +
                $"{policies.Attributes.Count}/{release.AttributeCount}, " +
                $"equipment ranks " +
                $"{policies.EquipmentRanks.Count}/{release.EquipmentRankCount}.");
        }
        {
            var recomputed = ItemTemplateContentRevisionHasher.ComputeV6(
                definitions,
                policies.Attributes,
                policies.EquipmentRanks,
                policies.HolySuitEffects,
                policies.ForgingMaterials,
                policies.EnhancementMaterials,
                policies.AttributeDusts,
                policies.Recipes,
                holySuit.Tiers,
                holySuit.Upgrades,
                holySuit.Consumables,
                holySuit.OperationPolicy);
            if (!recomputed.Equals(release.Revision, StringComparison.Ordinal))
            {
                // The seal is intact - the released rows still match their own
                // header counts - but the manifest hash no longer matches what
                // the current baselines produce. That happens when a reviewed
                // baseline gains an item, which is exactly the case the
                // publisher must answer by publishing a NEW revision instead of
                // validating the old one. Republishing is the normal path; the
                // frozen revision is left untouched, so the previous release
                // stays available.
                Console.WriteLine(
                    "[item-content] sealed revision no longer matches the " +
                    $"reviewed baselines (stored={release.Revision} " +
                    $"recomputed={recomputed} entries={definitions.Count}); " +
                    "republishing a new revision from the current baselines.");
                return false;
            }
        }

        return true;
    }
}
