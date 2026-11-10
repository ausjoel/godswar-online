using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    /// <summary>
    /// Applies one reviewed timed experience grant. Both potion families share
    /// this write: the caller resolves the family, its channel, and its tier
    /// rule, and this method commits the resolved grant and consumes one stack
    /// unit.
    /// </summary>
    /// <remarks>
    /// The granted duration is online time, so the row's
    /// <c>remaining_online_ticks</c> is authoritative and the ordinary online
    /// progression settlement spends it.
    /// </remarks>
    private async Task<PetTransition> ApplyExperienceBoostGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        uint expectedItemId,
        int kind,
        string sourcePrefix,
        ExperienceBoostPotionGrant grant,
        string writeFailedMessage,
        string mutationReason,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.Stack < 1 || item.PropId != expectedItemId)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }

        // A stronger boost of this kind is running: keep the potion. The row write refuses a lower tier.
        if (!grant.ReplacesActive)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }

        await WriteExperienceBoostGrantAsync(
            connection,
            transaction,
            characterId,
            kind,
            sourcePrefix,
            grant,
            writeFailedMessage,
            cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            characterId,
            bagSlot,
            item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);

        return new(
            PetDurableReceiptStatus.PetExperienceBoostActivated,
            KitBagSlot: bagSlot,
            InventoryMutations:
            [
                new InventoryMutation(
                    item.ItemId,
                    consumed.MutationKind,
                    item.BeforeState,
                    consumed.AfterState,
                    mutationReason,
                    inventoryRevision)
            ]);
    }

    private async Task<ActiveExperienceBoostGrant> ReadActiveGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int kind,
        string corruptActiveMessage,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                bonus_basis_points,
                COALESCE(remaining_online_ticks, 0)
            FROM public.character_experience_modifiers
            WHERE character_id = @characterId
              AND kind = @kind
              AND remaining_online_ticks > 0;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("kind", kind);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return default;
        }

        var active = new ActiveExperienceBoostGrant(
            reader.GetInt32(0),
            reader.GetInt64(1));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(corruptActiveMessage);
        }
        return active;
    }

    private async Task<long> WriteExperienceBoostGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int kind,
        string sourcePrefix,
        ExperienceBoostPotionGrant grant,
        string writeFailedMessage,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_experience_modifiers (
                character_id,
                status_id,
                kind,
                bonus_basis_points,
                priority,
                source,
                activated_at,
                expires_at,
                remaining_online_ticks
            )
            VALUES (
                @characterId,
                @statusId,
                @kind,
                @bonusBasisPoints,
                @priority,
                @source,
                transaction_timestamp(),
                transaction_timestamp() + (
                    @onlineTicks::bigint / 10000000
                ) * INTERVAL '1 second',
                @onlineTicks
            )
            ON CONFLICT (character_id, kind) DO UPDATE
            SET status_id = EXCLUDED.status_id,
                bonus_basis_points = EXCLUDED.bonus_basis_points,
                priority = EXCLUDED.priority,
                source = EXCLUDED.source,
                activated_at = EXCLUDED.activated_at,
                expires_at = EXCLUDED.expires_at,
                remaining_online_ticks = EXCLUDED.remaining_online_ticks
            WHERE EXCLUDED.bonus_basis_points >=
                public.character_experience_modifiers.bonus_basis_points
            RETURNING remaining_online_ticks;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("statusId", grant.StatusId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue(
            "bonusBasisPoints",
            grant.BonusBasisPoints);
        command.Parameters.AddWithValue("priority", grant.Priority);
        command.Parameters.AddWithValue(
            "source",
            sourcePrefix + grant.SkillId);
        command.Parameters.AddWithValue("onlineTicks", grant.OnlineTicks);
        var persisted = await command.ExecuteScalarAsync(cancellationToken);
        return persisted is long ticks && ticks == grant.OnlineTicks
            ? ticks
            : throw new InvalidDataException(writeFailedMessage);
    }

    private readonly record struct ActiveExperienceBoostGrant(
        int BonusBasisPoints,
        long OnlineTicks);
}
