using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal sealed record WonderlandBlackmarketRequest(Guid OperationId, CommandSubject Subject,
    PlayerOwnershipFence Ownership, RealmId Realm, WorldInstanceId Instance, int TargetIsland, int Function,
    Guid AdmissionReservationId = default)
{
    public int Cost => Function switch { 59 => 5000, 62 => 6000, 63 => 8000, _ => 0 };
    public bool IsValid => OperationId != Guid.Empty && Subject.AccountId > 0 && Subject.CharacterId > 0 &&
        Ownership.IsValid && Realm.Value > 0 && Instance.Value != Guid.Empty && TargetIsland is >= 1 and <= 8 && Cost > 0 &&
        (TargetIsland != 1 || AdmissionReservationId != Guid.Empty);
}

internal enum WonderlandBlackmarketStatus { Committed, InsufficientSilver, NotEligible, OwnershipLost, Unavailable }
internal sealed record WonderlandBlackmarketReceipt(WonderlandBlackmarketStatus Status, int Silver, long WalletRevision)
{
    public bool Succeeded => Status == WonderlandBlackmarketStatus.Committed;
}

internal interface IWonderlandBlackmarketStore
{
    Task<WonderlandBlackmarketReceipt> ChargeAsync(WonderlandBlackmarketRequest request, CancellationToken token);
    Task<WonderlandBlackmarketReceipt> RefundAsync(WonderlandBlackmarketRequest request, CancellationToken token);
}
