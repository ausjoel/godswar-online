using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandTitleStore(NpgsqlDataSource dataSource) : IWonderlandTitleStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<WonderlandTitleReceipt> SettleAsync(WonderlandTitleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var existing = await ReadExistingAsync(connection, transaction, request, cancellationToken);
        if (existing is not null) return existing;
        // Match daily admission and other reward stores' lock order.
        var characters = await LockCharactersAsync(connection, transaction, request, cancellationToken);
        existing = await ReadExistingAsync(connection, transaction, request, cancellationToken);
        if (existing is not null) return existing;
        if (characters.Count != request.AdmittedMembers.Count || characters.Where((character, index) =>
            character.CharacterId != request.AdmittedMembers[index].CharacterId ||
            character.AccountId != request.AdmittedMembers[index].AccountId).Any())
            return Failed(request, WonderlandTitleStatus.CharacterUnavailable);
        var eligible = characters.Where(character => request.CharacterIds.Contains(character.CharacterId)).ToArray();
        if (eligible.Any(character => !character.IsActive || !character.AlreadyOwned && character.Revision == long.MaxValue))
            return Failed(request, WonderlandTitleStatus.CharacterUnavailable);
        if (!await AdmissionMatchesAsync(connection, transaction, request, cancellationToken))
            return Failed(request, WonderlandTitleStatus.AdmissionConflict);
        if (!await BindRunAsync(connection, transaction, request, cancellationToken))
            return Failed(request, WonderlandTitleStatus.RequestConflict);
        existing = await ReadExistingAsync(connection, transaction, request, cancellationToken);
        if (existing is not null) return existing;
        await InsertMilestoneAsync(connection, transaction, request, cancellationToken);
        var members = new List<WonderlandTitleReceiptMember>();
        foreach (var character in eligible)
        {
            var member = new WonderlandTitleReceiptMember(character.AccountId, character.CharacterId,
                character.Honor, character.SelectedTitle, character.AlreadyOwned ? character.Revision : checked(character.Revision + 1),
                !character.AlreadyOwned);
            await WriteMemberAsync(connection, transaction, request, member, cancellationToken);
            members.Add(member);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(WonderlandTitleStatus.Applied, request.WorldInstanceId, request.Award, members.AsReadOnly());
    }

    private static WonderlandTitleReceipt Failed(WonderlandTitleRequest request, WonderlandTitleStatus status) =>
        new(status, request.WorldInstanceId, request.Award, []);

    private sealed record LockedCharacter(int AccountId, int CharacterId, int Honor, uint SelectedTitle,
        long Revision, bool IsActive, bool AlreadyOwned);

    private static void AddIdentity(NpgsqlCommand command, WonderlandTitleRequest request)
    {
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("island", checked((short)request.IslandNumber));
    }
}
