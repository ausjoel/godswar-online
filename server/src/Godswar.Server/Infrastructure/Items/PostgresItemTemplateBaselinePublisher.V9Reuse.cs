using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task<bool> TryReuseCurrentV9Async(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PublishedItemRevisionState existing, CancellationToken cancellationToken)
    {
        await VerifyPublishedV9ReleaseAsync(
            connection,
            transaction,
            existing,
            cancellationToken);
        var publishedHolySuit =
            await ReadPublishedHolySuitPoliciesAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasClassSuitItems =
            await PublishedClassSuitItemsAreCompleteAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasElementalContent =
            await PublishedElementalContentIsCompleteAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasSocketSpells =
            await PublishedSocketSpellItemsAreCompleteAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasHolyStoneMaterials =
            await PublishedHolyStoneMaterialsAreCompleteAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasPetItems =
            await PublishedPetItemsAreCompleteAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasNameplates =
            await PublishedNameplatesAreCompleteAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasWarehouseItems =
            await PublishedWarehouseItemsAreCompleteAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasOpal = await PublishedOpalIsCompleteAsync(
            connection,
            transaction,
            existing.Revision,
            cancellationToken);
        var hasMountSpeedProfile =
            await PublishedMountSpeedProfileIsCurrentAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        var hasWonderlandSacks = await PublishedWonderlandSacksAreCompleteAsync(
            connection, transaction, existing.Revision, cancellationToken);
        var hasExperiencePill = await PublishedExperiencePillIsCompleteAsync(
            connection, transaction, existing.Revision, cancellationToken);
        if (hasClassSuitItems &&
            hasWonderlandSacks &&
            hasExperiencePill &&
            hasElementalContent &&
            hasSocketSpells &&
            hasHolyStoneMaterials &&
            hasPetItems &&
            hasNameplates &&
            hasWarehouseItems &&
            hasOpal &&
            hasMountSpeedProfile &&
            IsCurrentHolySuitPolicy(publishedHolySuit) &&
            await PublishedHolySuitItemsAreCurrentAsync(connection, transaction, existing.Revision, cancellationToken))
        {
            await EnsureHolySuitMutableTemplateCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            await EnsureClassSuitMutableTemplateCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            await EnsureElementalMutableTemplateCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                upgradeFromV8Revision: null,
                cancellationToken);
            await EnsureHolyStoneMutableTemplateCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            await EnsureSocketSpellMutableCompatibilityAsync(connection, transaction, existing.Revision, cancellationToken);
            await EnsureWonderlandSackMutableCompatibilityAsync(connection, transaction, existing.Revision, cancellationToken);
            await EnsureExperiencePillMutableCompatibilityAsync(connection, transaction, existing.Revision, cancellationToken);
            await EnsurePetItemMutableTemplateCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            await EnsureNameplateMutableCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            await EnsureWarehouseMutableCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            await EnsureOpalMutableCompatibilityAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            return true;
        }
        return false;
    }
}
