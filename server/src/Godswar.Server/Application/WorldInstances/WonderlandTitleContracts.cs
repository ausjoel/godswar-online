using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct WonderlandTitleMember(int AccountId, int CharacterId, PlayerOwnershipFence Ownership);
internal readonly record struct WonderlandTitleAward(int IslandNumber, uint TitleId, string DisplayName);

internal static class WonderlandTitlePolicy
{
    public const string Revision = "wonderland-titles-v1";
    public static WonderlandTitleAward Resolve(int islandNumber) => islandNumber switch
    {
        1 => new(1, 5155, "Gatebreaker"),
        2 => new(2, 5114, "Demonbreaker"),
        3 => new(3, 5156, "Flamebreaker"),
        4 => new(4, 5115, "Stonebreaker"),
        5 => new(5, 5157, "Marshal's Bane"),
        6 => new(6, 5116, "Dragonbane"),
        7 => new(7, 5117, "Hydra's Bane"),
        8 => new(8, 5118, "Wonderland Sovereign"),
        _ => throw new ArgumentOutOfRangeException(nameof(islandNumber))
    };
}

/// <summary>
/// Immutable evidence produced by the exact instance owner when an island
/// clears. The award is the instance's own result and keeps the registered
/// party's classification; entitlement is granted to every character still
/// inside the run at that moment, including one who confirmed the party window
/// after the run was sealed. Later disconnect or transport does not revoke an
/// earned entitlement.
/// </summary>
internal sealed class WonderlandTitleRequest
{
    public WonderlandTitleRequest(WorldInstanceId worldInstanceId, RealmId realmId,
        Guid admissionReservationId, DateTimeOffset startedAtUtc, DateTimeOffset clearedAtUtc,
        int islandNumber, IReadOnlyCollection<WonderlandTitleMember> admittedMembers,
        IReadOnlyCollection<WonderlandTitleMember> frozenMembers,
        IReadOnlyCollection<Guid>? admissionReservationIds = null,
        IReadOnlyDictionary<int, Guid>? lateAdmissionReservations = null)
    {
        ArgumentNullException.ThrowIfNull(admittedMembers);
        ArgumentNullException.ThrowIfNull(frozenMembers);
        var late = (lateAdmissionReservations ?? new Dictionary<int, Guid>())
            .OrderBy(pair => pair.Key)
            .ToArray();
        var reservations = (admissionReservationIds ?? [admissionReservationId])
            .Concat(late.Select(pair => pair.Value))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();
        var admitted = admittedMembers.OrderBy(member => member.CharacterId).ToArray();
        var frozen = frozenMembers.OrderBy(member => member.CharacterId).ToArray();
        // S5 (5 Nov party run): the registered party is the run's identity. A member admitted after the
        // seal holds a reservation of its own and is never part of that identity, so the run binding stays
        // stable while the roster grows.
        var party = admitted.Where(member => late.All(pair => pair.Key != member.CharacterId)).ToArray();
        if (!worldInstanceId.IsValid || !realmId.IsValid || realmId.Value > short.MaxValue ||
            admissionReservationId == Guid.Empty || reservations.Length == 0 ||
            !reservations.Contains(admissionReservationId) ||
            party.Length == 0 ||
            late.Any(pair => pair.Value == Guid.Empty || pair.Value == admissionReservationId ||
                admitted.All(member => member.CharacterId != pair.Key)) ||
            startedAtUtc == default ||
            startedAtUtc.Offset != TimeSpan.Zero || clearedAtUtc.Offset != TimeSpan.Zero ||
            clearedAtUtc < startedAtUtc || clearedAtUtc - startedAtUtc >= TimeSpan.FromMinutes(40) ||
            !ValidRoster(admitted) || !ValidRoster(frozen) || frozen.Any(member => !admitted.Any(original =>
                original.CharacterId == member.CharacterId && original.AccountId == member.AccountId)))
            throw new ArgumentException("Invalid authoritative Wonderland title evidence.");
        WorldInstanceId = worldInstanceId;
        RealmId = realmId;
        AdmissionReservationId = admissionReservationId;
        AdmissionReservationIds = Array.AsReadOnly(reservations);
        LateAdmissionReservations = late.ToDictionary(pair => pair.Key, pair => pair.Value).AsReadOnly();
        PartyMembers = Array.AsReadOnly(party);
        PartyCharacterIds = Array.AsReadOnly(party.Select(member => member.CharacterId).ToArray());
        StartedAtUtc = startedAtUtc;
        ClearedAtUtc = clearedAtUtc;
        Award = WonderlandTitlePolicy.Resolve(islandNumber);
        AdmittedMembers = Array.AsReadOnly(admitted);
        FrozenMembers = Array.AsReadOnly(frozen);
        AdmittedCharacterIds = Array.AsReadOnly(admitted.Select(member => member.CharacterId).ToArray());
        CharacterIds = Array.AsReadOnly(frozen.Select(member => member.CharacterId).ToArray());
        RunHash = HashRun(worldInstanceId, realmId, admissionReservationId, startedAtUtc, party);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(WonderlandTitlePolicy.Revision);
            writer.Write(worldInstanceId.Value.ToByteArray());
            writer.Write(realmId.Value);
            writer.Write(admissionReservationId.ToByteArray());
            writer.Write(startedAtUtc.UtcTicks);
            WriteRoster(writer, admitted);
            writer.Write(clearedAtUtc.UtcTicks);
            writer.Write(islandNumber);
            writer.Write(Award.TitleId);
            WriteRoster(writer, frozen);
        }
        RequestHash = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public WorldInstanceId WorldInstanceId { get; }
    public RealmId RealmId { get; }
    public Guid AdmissionReservationId { get; }

    /// <summary>
    /// Every admission reservation that put a character into this run: the
    /// registered party's own and, for a member who confirmed the party window
    /// after the run was sealed, that member's own. The ledger fence reads them
    /// as one set.
    /// </summary>
    public IReadOnlyList<Guid> AdmissionReservationIds { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset ClearedAtUtc { get; }
    public WonderlandTitleAward Award { get; }
    public int IslandNumber => Award.IslandNumber;
    public IReadOnlyList<WonderlandTitleMember> AdmittedMembers { get; }
    public IReadOnlyList<WonderlandTitleMember> FrozenMembers { get; }
    public IReadOnlyList<int> AdmittedCharacterIds { get; }
    public IReadOnlyList<int> CharacterIds { get; }

    /// <summary>
    /// Members admitted after the registered party was sealed, each with the admission reservation it
    /// claimed for itself. Every other admitted member entered on <see cref="AdmissionReservationId"/>.
    /// </summary>
    public IReadOnlyDictionary<int, Guid> LateAdmissionReservations { get; }

    /// <summary>Admitted members who entered on the registered party's reservation.</summary>
    public IReadOnlyList<WonderlandTitleMember> PartyMembers { get; }
    public IReadOnlyList<int> PartyCharacterIds { get; }

    /// <summary>The reservation that admitted <paramref name="characterId"/> into this run.</summary>
    public Guid ReservationFor(int characterId) =>
        LateAdmissionReservations.TryGetValue(characterId, out var reservation) ? reservation : AdmissionReservationId;
    public string RunHash { get; }
    public string RequestHash { get; }

    /// <summary>
    /// The run's identity: instance, realm, party reservation, start and the registered party's admitted
    /// roster. Byte-identical to the previous formula whenever nobody joined late, so runs bound by older
    /// builds keep their hash.
    /// </summary>
    private static string HashRun(WorldInstanceId worldInstanceId, RealmId realmId, Guid admissionReservationId,
        DateTimeOffset startedAtUtc, WonderlandTitleMember[] party)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(WonderlandTitlePolicy.Revision);
            writer.Write(worldInstanceId.Value.ToByteArray());
            writer.Write(realmId.Value);
            writer.Write(admissionReservationId.ToByteArray());
            writer.Write(startedAtUtc.UtcTicks);
            WriteRoster(writer, party);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static bool ValidRoster(WonderlandTitleMember[] members) => members.Length is >= 1 and <= 5 &&
        members.All(member => member.AccountId > 0 && member.CharacterId > 0 && member.Ownership.IsValid) &&
        members.Select(member => member.CharacterId).Distinct().Count() == members.Length &&
        members.Select(member => member.AccountId).Distinct().Count() == members.Length;

    private static void WriteRoster(BinaryWriter writer, WonderlandTitleMember[] members)
    {
        writer.Write(members.Length);
        foreach (var member in members)
        {
            writer.Write(member.AccountId);
            writer.Write(member.CharacterId);
            writer.Write(member.Ownership.OwnerId.ToByteArray());
            writer.Write(member.Ownership.Generation);
        }
    }
}

internal enum WonderlandTitleStatus : byte
{
    Applied = 1, Duplicate = 2, RequestConflict = 3, AdmissionConflict = 4, CharacterUnavailable = 5
}

internal readonly record struct WonderlandTitleReceiptMember(int AccountId, int CharacterId,
    int HonorPoints, uint SelectedTitleId, long RewardRevision, bool NewlyOwned);

internal sealed record WonderlandTitleReceipt(WonderlandTitleStatus Status, WorldInstanceId WorldInstanceId,
    WonderlandTitleAward Award, IReadOnlyList<WonderlandTitleReceiptMember> Members)
{
    public bool Succeeded => Status is WonderlandTitleStatus.Applied or WonderlandTitleStatus.Duplicate;
}

internal interface IWonderlandTitleStore
{
    Task<WonderlandTitleReceipt> SettleAsync(WonderlandTitleRequest request, CancellationToken cancellationToken = default);
}
