using Godswar.Server.State;
using System.Data;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed partial class PostgresFighterLevelSealStore
{
    public async Task<FighterLevelSealChangeResult>
        ChangeFighterLevelSealAsync(
        int accountId,
        int characterId,
        RealmId realmId,
        PlayerOwnershipFence ownership,
        Guid operationId,
        bool desiredSealed,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        ownership.Validate();
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A level-seal operation ID is required.",
                nameof(operationId));
        }

        var subject = new CommandSubject(accountId, characterId);
        var ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var ownershipResult = await ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken);
        if (ownershipResult.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                FighterLevelSealChangeStatus.CharacterNotFound,
                LevelSealed: false,
                BindingGold: 0,
                SealRevision: 0);
        }
        ownershipResult.RequireCurrent();

        var before = await LockFighterLevelSealCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            realmId,
            cancellationToken);
        if (!before.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                FighterLevelSealChangeStatus.CharacterNotFound,
                LevelSealed: false,
                BindingGold: 0,
                SealRevision: 0);
        }

        var request = CreateFighterLevelSealRequest(
            realmId,
            desiredSealed);
        var stored = await ReadFighterLevelSealInboxAsync(
            connection,
            transaction,
            accountId,
            characterId,
            operationId,
            cancellationToken);
        if (stored.HasValue)
        {
            var replay = await ReplayFighterLevelSealAsync(
                connection,
                transaction,
                characterId,
                realmId,
                request.Hash,
                stored.Value,
                before.Value,
                cancellationToken);
            (await ownershipGuard.ValidateCurrentAsync(
                subject,
                ownership,
                cancellationToken)).RequireCurrent();
            return replay;
        }

        var status = ResolveFighterLevelSealStatus(
            before.Value,
            desiredSealed);
        var changed = status is
            FighterLevelSealChangeStatus.Sealed or
            FighterLevelSealChangeStatus.Unsealed;
        var unsealCost = status == FighterLevelSealChangeStatus.Unsealed
            ? FighterLevelSealRules.UnsealBoundGoldCost
            : 0;
        if (unsealCost > 0 &&
            !await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                FighterLevelSealChangeStatus.CharacterNotFound,
                before.Value.LevelSealed,
                before.Value.BindingGold,
                before.Value.SealRevision);
        }

        var bindingGoldAfter = checked(
            before.Value.BindingGold - unsealCost);
        var walletRevisionAfter = unsealCost == 0
            ? before.Value.WalletRevision
            : checked(before.Value.WalletRevision + 1);
        var sealRevisionAfter = changed
            ? checked(before.Value.SealRevision + 1)
            : before.Value.SealRevision;
        var inboxId = await InsertFighterLevelSealEvidenceAsync(
            connection,
            transaction,
            accountId,
            characterId,
            realmId,
            operationId,
            request,
            status,
            before.Value,
            desiredSealed,
            bindingGoldAfter,
            walletRevisionAfter,
            sealRevisionAfter,
            cancellationToken);
        if (changed)
        {
            await UpdateFighterLevelSealCharacterAsync(
                connection,
                transaction,
                accountId,
                characterId,
                realmId,
                desiredSealed,
                before.Value,
                bindingGoldAfter,
                walletRevisionAfter,
                sealRevisionAfter,
                cancellationToken);
        }
        if (unsealCost > 0)
        {
            await InsertFighterLevelSealCurrencyLedgerAsync(
                connection,
                transaction,
                inboxId,
                accountId,
                characterId,
                before.Value.BindingGold,
                bindingGoldAfter,
                walletRevisionAfter,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        (await ownershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();

        return new(
            status,
            changed ? desiredSealed : before.Value.LevelSealed,
            bindingGoldAfter,
            sealRevisionAfter);
    }

    private static FighterLevelSealChangeStatus
        ResolveFighterLevelSealStatus(
        FighterLevelSealLockedCharacter before,
        bool desiredSealed)
    {
        if (before.LevelSealed == desiredSealed)
        {
            return desiredSealed
                ? FighterLevelSealChangeStatus.AlreadySealed
                : FighterLevelSealChangeStatus.AlreadyUnsealed;
        }
        if (!desiredSealed &&
            before.BindingGold <
                FighterLevelSealRules.UnsealBoundGoldCost)
        {
            return FighterLevelSealChangeStatus.InsufficientBoundGold;
        }
        return desiredSealed
            ? FighterLevelSealChangeStatus.Sealed
            : FighterLevelSealChangeStatus.Unsealed;
    }

    private static async Task<FighterLevelSealLockedCharacter?>
        LockFighterLevelSealCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        RealmId realmId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT fighter_job_lv, fighter_level_sealed, "BindingGold",
                   wallet_revision, progression_reward_revision,
                   fighter_level_seal_revision
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var value = new FighterLevelSealLockedCharacter(
            reader.GetInt32(0),
            reader.GetBoolean(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
        if (value.Level is < 1 or > PlayerExperienceCatalog.MaximumLevel ||
            value.BindingGold < 0 ||
            value.WalletRevision < 0 ||
            value.ProgressionRevision < 0 ||
            value.SealRevision < 0)
        {
            throw new InvalidDataException(
                "The locked fighter level-seal state is invalid.");
        }
        return value;
    }

    private readonly record struct FighterLevelSealLockedCharacter(
        int Level,
        bool LevelSealed,
        int BindingGold,
        long WalletRevision,
        long ProgressionRevision,
        long SealRevision);
}
