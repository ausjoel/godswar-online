using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresAtlantisCompletionRewardStore(NpgsqlDataSource dataSource)
    : IAtlantisCompletionRewardStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<AtlantisCompletionRewardReceipt> SettleAsync(AtlantisCompletionRewardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var existing = await ReadExistingAsync(connection, transaction, request, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        // The daily admission/payment stores acquire character rows before
        // admission rows. Keep that order, with stable character ordering.
        var characters = await LockCharactersAsync(connection, transaction, request, cancellationToken);
        // A concurrent identical settlement can finish while character locks
        // are awaited. Replay it before checking the now-incremented balance.
        existing = await ReadExistingAsync(connection, transaction, request, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        // Every registered member must still be present with the same identity.
        // The loaded set may legitimately be larger: a member who joined the run
        // by invitation is a recipient without holding a seat in the party the run
        // was registered for, and the old exact-count check threw him away - which
        // is why a party run paid nobody but its leader.
        if (request.AdmittedMembers.Any(admitted => !characters.Any(character =>
            character.CharacterId == admitted.CharacterId &&
            character.AccountId == admitted.AccountId)))
        {
            return Failed(request, AtlantisCompletionRewardStatus.CharacterUnavailable);
        }
        var finishers = characters.Where(character => request.CharacterIds.Contains(character.CharacterId)).ToArray();
        if (finishers.Any(character => !character.IsActive || character.Camp is < 0 or > 1 ||
            character.Honor > int.MaxValue - request.Award.HardPoints || character.RewardRevision == long.MaxValue))
        {
            return Failed(request, AtlantisCompletionRewardStatus.CharacterUnavailable);
        }
        if (!await AdmissionMatchesAsync(connection, transaction, request, cancellationToken))
        {
            return Failed(request, AtlantisCompletionRewardStatus.AdmissionConflict);
        }

        if (!await TryInsertSettlementAsync(connection, transaction, request, cancellationToken))
        {
            var raced = await ReadExistingAsync(connection, transaction, request, cancellationToken) ??
                Failed(request, AtlantisCompletionRewardStatus.RequestConflict);
            await transaction.CommitAsync(cancellationToken);
            return raced;
        }

        var members = new List<AtlantisCompletionRewardMember>(finishers.Length);
        foreach (var character in finishers)
        {
            var reward = new AtlantisCompletionRewardMember(character.AccountId, character.CharacterId, checked((byte)character.Camp),
                character.Honor, checked(character.Honor + request.Award.HardPoints),
                checked(character.RewardRevision + 1), request.Award.TitleId);
            await WriteMemberAsync(connection, transaction, request, reward, cancellationToken);
            members.Add(reward);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(AtlantisCompletionRewardStatus.Applied, request.WorldInstanceId, request.Award, members.AsReadOnly());
    }

    private static AtlantisCompletionRewardReceipt Failed(AtlantisCompletionRewardRequest request,
        AtlantisCompletionRewardStatus status) => new(status, request.WorldInstanceId, request.Award, []);

    private sealed record LockedCharacter(int AccountId, int CharacterId, short Camp,
        int Honor, long RewardRevision, bool IsActive);
}
