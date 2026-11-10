using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    /// <summary>
    /// Transforms one bag equipment item into a random RltID sibling and spends
    /// one Level 3 Amethyst, both checked against the locked inventory rows.
    /// </summary>
    public async Task<AmethystTransformResult> TransformEquipmentWithAmethystAsync(
        int accountId,
        int characterId,
        int gearSlot,
        CompactItemEntry expectedGear,
        int amethystSlot,
        CompactItemEntry expectedAmethyst,
        CancellationToken cancellationToken = default,
        uint preferredTargetId = 0)
    {
        if (gearSlot is < 0 or >= KitBagProjectionSlots ||
            amethystSlot is < 0 or >= KitBagProjectionSlots ||
            gearSlot == amethystSlot)
        {
            return new AmethystTransformResult(
                AmethystTransformStatus.StaleSelection, null,
                CompactItemEntry.Empty, CompactItemEntry.Empty);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT id
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null || scalar is DBNull)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AmethystTransformResult(
                    AmethystTransformStatus.CharacterNotFound, null,
                    CompactItemEntry.Empty, CompactItemEntry.Empty);
            }
        }

        var (_, kitBag) = await LoadAuthoritativeItemProjectionsForUpdateAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var gear = KitBagSlots.GetItem(kitBag, gearSlot);
        var amethyst = KitBagSlots.GetItem(kitBag, amethystSlot);
        if (!SameStagedItem(gear, expectedGear) ||
            amethyst.IsEmpty ||
            amethyst.Id != expectedAmethyst.Id)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AmethystTransformResult(
                AmethystTransformStatus.StaleSelection,
                await GetCharacterByIdAsync(characterId, cancellationToken),
                gear, gear);
        }

        var targets = EquipmentTransformCatalog.GetPublishedTargets(
            gear.Id,
            ItemContent.Templates);
        if (targets.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AmethystTransformResult(
                AmethystTransformStatus.NotTransformable,
                await GetCharacterByIdAsync(characterId, cancellationToken),
                gear, gear);
        }

        var plan = EquipmentTransformCatalog.Plan(
            gear,
            amethyst,
            targets,
            // FORGEGEMS: the player's preview pick when a patched client
            // sends it (stock clients send 0, which keeps the random pick).
            EquipmentTransformPick.ChooseIndex(
                targets,
                preferredTargetId,
                System.Security.Cryptography.RandomNumberGenerator.GetInt32(targets.Count)));
        if (plan is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AmethystTransformResult(
                AmethystTransformStatus.NoAmethyst,
                await GetCharacterByIdAsync(characterId, cancellationToken),
                gear, gear);
        }

        await ApplyKitBagSlotMutationAsync(
            connection, transaction, characterId, gearSlot, gear, plan.GearAfter,
            "Amethyst transform", "amethyst-transform", cancellationToken);
        await ApplyKitBagSlotMutationAsync(
            connection, transaction, characterId, amethystSlot, amethyst, plan.AmethystAfter,
            "Amethyst transform", "amethyst-transform-consume", cancellationToken);

        var refreshed = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Transformed character could not be reloaded inside its transaction.");
        await transaction.CommitAsync(cancellationToken);
        return new AmethystTransformResult(
            AmethystTransformStatus.Transformed,
            refreshed,
            gear,
            plan.GearAfter);
    }

    private static bool SameStagedItem(CompactItemEntry actual, CompactItemEntry expected) =>
        !actual.IsEmpty &&
        actual.Id == expected.Id &&
        actual.Quality == expected.Quality &&
        actual.Grade == expected.Grade &&
        actual.Bound == expected.Bound &&
        actual.Stack == expected.Stack;
}
