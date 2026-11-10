using Npgsql;

namespace Godswar.Server.State;

internal sealed record ForgeCompoundResult(
    ForgeCompoundStatus Status,
    GameCharacter? Character,
    CompactItemEntry Result,
    string Reason);

internal sealed partial class PostgresGameStore
{
    /// <summary>
    /// FORGEGEMS: Gems tab. Consumes six staged gem units and adds one gem of
    /// the next level, all against the locked bag rows.
    /// </summary>
    public async Task<ForgeCompoundResult> CompoundGemsAsync(
        int accountId,
        int characterId,
        IReadOnlyList<ForgeCompoundSelection> units,
        CancellationToken cancellationToken = default)
    {
        return await RunForgeCompoundAsync(
            accountId,
            characterId,
            "Gem compound",
            "forge-gem-compound",
            (_, _, _, bag) => Task.FromResult(
                ForgeCompoundPlanner.PlanGemCompound(ItemContent.Templates, bag, units)),
            cancellationToken);
    }

    /// <summary>
    /// FORGEGEMS: Comp tab. Re-rolls the main gear (forge slot 0) from itself
    /// and three supporting pieces (forge slots 3-5), which are consumed.
    /// </summary>
    public async Task<ForgeCompoundResult> CompoundEquipmentAsync(
        int accountId,
        int characterId,
        ForgeCompoundSelection main,
        IReadOnlyList<ForgeCompoundSelection> supporting,
        CancellationToken cancellationToken = default)
    {
        return await RunForgeCompoundAsync(
            accountId,
            characterId,
            "Equipment compound",
            "forge-equipment-compound",
            async (connection, transaction, token, bag) =>
            {
                var pool = await ReadForgeCompoundAttributePoolAsync(
                    connection,
                    transaction,
                    main.ExpectedItem.Id,
                    token);
                return ForgeCompoundPlanner.PlanEquipmentCompound(
                    ItemContent.Templates,
                    bag,
                    main,
                    supporting,
                    pool,
                    System.Security.Cryptography.RandomNumberGenerator.GetInt32,
                    ForgeCompoundPlanner.KeepStoneAttributes);
            },
            cancellationToken);
    }

    private async Task<ForgeCompoundResult> RunForgeCompoundAsync(
        int accountId,
        int characterId,
        string operationName,
        string auditSource,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, CompactItemEntry[], Task<ForgeCompoundPlan>> plan,
        CancellationToken cancellationToken)
    {
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
                return new ForgeCompoundResult(
                    ForgeCompoundStatus.CharacterNotFound, null, CompactItemEntry.Empty,
                    "Character was not found.");
            }
        }

        var (_, kitBag) = await LoadAuthoritativeItemProjectionsForUpdateAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var bag = new CompactItemEntry[KitBagProjectionSlots];
        for (var slot = 0; slot < bag.Length; slot++)
        {
            bag[slot] = KitBagSlots.GetItem(kitBag, slot);
        }

        var planned = await plan(connection, transaction, cancellationToken, bag);
        if (!planned.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ForgeCompoundResult(
                planned.Status,
                await GetCharacterByIdAsync(characterId, cancellationToken),
                CompactItemEntry.Empty,
                planned.Reason);
        }

        // Consumption and updates first, then inserts into emptied slots.
        foreach (var mutation in planned.Mutations.OrderBy(static mutation => mutation.Before.IsEmpty ? 1 : 0))
        {
            await ApplyKitBagSlotMutationAsync(
                connection,
                transaction,
                characterId,
                mutation.Slot,
                mutation.Before,
                mutation.After,
                operationName,
                auditSource,
                cancellationToken);
        }

        var refreshed = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"{operationName}: character could not be reloaded inside its transaction.");
        await transaction.CommitAsync(cancellationToken);
        return new ForgeCompoundResult(
            ForgeCompoundStatus.Succeeded,
            refreshed,
            planned.Result,
            string.Empty);
    }

    private static async Task<IReadOnlyList<ForgeCompoundAttributeOption>> ReadForgeCompoundAttributePoolAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        uint itemId,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('public.item_allowed_attributes') IS NOT NULL;",
            connection,
            transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return [];
            }
        }

        var options = new List<ForgeCompoundAttributeOption>();
        await using var command = new NpgsqlCommand(
            """
            SELECT attribute_id, stat_type
            FROM public.item_allowed_attributes
            WHERE item_id = @itemId
              AND attribute_id BETWEEN 0 AND 32767
              AND stat_type IS NOT NULL
            ORDER BY position;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", checked((int)itemId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            options.Add(new(
                checked((short)reader.GetInt32(0)),
                reader.GetInt16(1)));
        }

        return options;
    }
}
