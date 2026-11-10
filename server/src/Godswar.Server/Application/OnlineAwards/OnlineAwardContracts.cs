using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.OnlineAwards;

internal readonly record struct OnlineAwardRewardEntry(
    short Order,
    int ItemId,
    short Quantity,
    short ItemQuality,
    short Bound,
    short StackCap);

internal sealed record OnlineAwardBalanceSnapshot(
    long Revision,
    string Sha256,
    IReadOnlyList<OnlineAwardRewardEntry> Rewards)
{
    public const int MaximumRewardRows = 16;
    public const int MaximumTotalQuantity = 127;

    public void Validate()
    {
        if (Revision <= 0 ||
            Rewards is null ||
            Rewards.Count is < 1 or > MaximumRewardRows ||
            !string.Equals(
                Sha256,
                ComputeSha256(Rewards),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Online Award balance snapshot is invalid.");
        }

        var total = 0;
        var requiredSlots = 0;
        var identities = new HashSet<(int, short, short)>();
        for (var index = 0; index < Rewards.Count; index++)
        {
            var reward = Rewards[index];
            total = checked(total + reward.Quantity);
            requiredSlots = checked(requiredSlots +
                (reward.Quantity + reward.StackCap - 1) /
                    reward.StackCap);
            if (reward.Order != index ||
                reward.ItemId <= 0 ||
                reward.Quantity <= 0 ||
                reward.ItemQuality is < 1 or > 16 ||
                reward.Bound is < 0 or > 1 ||
                reward.StackCap is < 1 or > 999 ||
                reward.Quantity > MaximumTotalQuantity ||
                !identities.Add((
                    reward.ItemId,
                    reward.ItemQuality,
                    reward.Bound)))
            {
                throw new InvalidDataException(
                    "An Online Award reward row is invalid.");
            }
        }

        if (total > MaximumTotalQuantity || requiredSlots > 96)
        {
            throw new InvalidDataException(
                "The Online Award quantity exceeds its bounded maximum.");
        }
    }

    public static string ComputeSha256(
        IEnumerable<OnlineAwardRewardEntry> rewards)
    {
        ArgumentNullException.ThrowIfNull(rewards);
        var builder = new StringBuilder("online-award-balance-v1\n");
        foreach (var reward in rewards.OrderBy(static value => value.Order))
        {
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "reward:{0},{1},{2},{3},{4},{5}\n",
                reward.Order,
                reward.ItemId,
                reward.Quantity,
                reward.ItemQuality,
                reward.Bound,
                reward.StackCap);
        }
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public string CoordinationRevision()
    {
        Validate();
        return Sha256;
    }
}

internal readonly record struct OnlineAwardOperationIdentity(
    CommandIdentityStrength Strength,
    Guid OperationId,
    Guid RawLocalConnectionId)
{
    public static OnlineAwardOperationIdentity SecureClient(Guid value) =>
        new(CommandIdentityStrength.ClientOperationId, value, Guid.Empty);

    public static OnlineAwardOperationIdentity RawLocalServer(
        Guid value,
        Guid connectionId) =>
        new(CommandIdentityStrength.ServerOperationId, value, connectionId);

    public bool IsSecureClient =>
        Strength == CommandIdentityStrength.ClientOperationId &&
        OperationId != Guid.Empty && RawLocalConnectionId == Guid.Empty;

    public bool IsRawLocalServer =>
        Strength == CommandIdentityStrength.ServerOperationId &&
        OperationId != Guid.Empty && RawLocalConnectionId != Guid.Empty;
}

internal readonly record struct OnlineAwardCommand(
    OnlineAwardOperationIdentity Identity,
    int RealmId,
    int NpcId,
    int DialogIndex,
    int ClaimDayNumber);

internal enum OnlineAwardExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    InvalidIntent = 3,
    RequestHashConflict = 4,
    PreconditionFailed = 5,
    BagFull = 6,
    AlreadyClaimed = 7,
    ProviderUnavailable = 8
}

internal readonly record struct OnlineAwardItemDelta(
    int ItemId,
    short ItemQuality,
    short Bound,
    short Quantity);

internal sealed record OnlineAwardExecutionReceipt(
    int CharacterId,
    int RealmId,
    DateOnly ClaimDay,
    int NativeResultSubId,
    long BalanceRevision,
    string BalanceSha256,
    string ItemContentRevision,
    IReadOnlyList<OnlineAwardItemDelta> ItemDeltas,
    long InventoryRevision,
    long OnlineAwardRevision,
    string AuditId,
    Guid EventId);

internal sealed record OnlineAwardExecutionResult(
    OnlineAwardExecutionDisposition Disposition,
    OnlineAwardExecutionReceipt? Receipt)
{
    public bool IsDurable =>
        Disposition is OnlineAwardExecutionDisposition.Committed or
            OnlineAwardExecutionDisposition.Duplicate;

    public static OnlineAwardExecutionResult Terminal(
        OnlineAwardExecutionDisposition disposition,
        OnlineAwardExecutionReceipt? receipt = null) =>
        new(disposition, receipt);
}

internal interface IOnlineAwardCommandExecutor
{
    Task<OnlineAwardExecutionResult> ExecuteAsync(
        CommandEnvelope<OnlineAwardCommand> envelope,
        CancellationToken cancellationToken = default);
}

internal sealed record OnlineAwardBalanceUpdate(
    long ExpectedRevision,
    string UpdatedBy,
    IReadOnlyList<OnlineAwardRewardEntry> Rewards);

internal enum OnlineAwardBalanceUpdateStatus : byte
{
    Updated = 1,
    Unchanged = 2,
    RevisionConflict = 3,
    Invalid = 4
}

internal sealed record OnlineAwardBalanceUpdateResult(
    OnlineAwardBalanceUpdateStatus Status,
    OnlineAwardBalanceSnapshot? Snapshot);

internal interface IOnlineAwardBalanceSettingsStore
{
    Task<OnlineAwardBalanceUpdateResult> TryPublishSuccessorAsync(
        OnlineAwardBalanceUpdate update,
        CancellationToken cancellationToken = default);
}
