using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The instance roster's new contract: it is the run's own member list, it marks
/// an offline member instead of dropping him, its single state byte separates
/// the run's leader from the other online members, and a login puts a member who
/// dropped back into a run that is still going with somebody else inside.
/// </summary>
internal static class InstanceRosterPanelChecks
{
    public const string CheckName =
        "Instance roster keeps dropped members and reconnects them";

    private const byte WonderlandMap = 207;
    private const int LeaderCharacterId = 6_101;
    private const int MemberCharacterId = 6_102;
    private const int MemberBytes = 44;

    // The instance caller's own Wonderland arrival: island 1's entrance.
    private const float WonderlandArrivalX = 169f;
    private const float WonderlandArrivalZ = -216f;

    public static async Task RunAsync()
    {
        CheckRosterStateByteMapping();
        await CheckWonderlandRosterAndReconnectAsync();
    }

    /// <summary>
    /// The roster's single state byte at <c>+40</c> is one enumeration, not two
    /// flags: the client renders it as text such as 「在线(队长)」 or 「等待中」.
    /// In game it is confirmed that one is the run's leader and two is a member
    /// who is only waiting; zero (offline) and three (plain online) are still
    /// inferred from the client's four-state window. This asserts the server's own
    /// contract so a change stays visible, and the run-level check below asserts
    /// the online states end to end.
    /// </summary>
    private static void CheckRosterStateByteMapping()
    {
        var allFourStates = PacketBuilder.RepetitionInstanceMembers(
        [
            new(301, "Leader", 120, RepetitionMemberState.OnlineLeader, 1),
            new(302, "Member", 120, RepetitionMemberState.Online, 1),
            new(303, "Waiting", 120, RepetitionMemberState.Waiting, 1),
            new(304, "Gone", 120, RepetitionMemberState.Offline, 1)
        ]);
        Check.True(
            RosterState(allFourStates, 0) == 1 &&
            RosterState(allFourStates, 1) == (byte)RepetitionMemberState.Online &&
            RosterState(allFourStates, 2) == 2 &&
            RosterState(allFourStates, 3) == 0,
            "the roster state byte is 0 offline, 1 for the run leader, 2 while " +
            "waiting, and the online candidate for an online member");
    }

    private static async Task CheckWonderlandRosterAndReconnectAsync()
    {
        await using var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Legacy,
            gameplayCatalogs: GameplayRuntimeCatalogs.Create(
                WonderlandMapChecks.Content()));
        var created = await registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest,
            new WorldMapId(WonderlandMap),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            CancellationToken.None);
        var runtime = created.Runtime ?? throw new InvalidOperationException(
            "The Wonderland roster fixture created no runtime.");
        var startedAt = runtime.Descriptor.CreatedAt;

        var leaderTransport = new FactionCrierCaptureTransport();
        var memberTransport = new FactionCrierCaptureTransport();
        await using var leaderSession = new ClientSession(leaderTransport);
        await using var memberSession = new ClientSession(memberTransport);
        var leader = CreateCharacter(LeaderCharacterId, "RosterLeader");
        var member = CreateCharacter(MemberCharacterId, "RosterMember");
        var leaderFence = GameHandlerOwnershipTestFences.Bind(
            registry, leaderSession, leader.AccountId, leader);
        var memberFence = GameHandlerOwnershipTestFences.Bind(
            registry, memberSession, member.AccountId, member);
        registry.JoinWorldInstance(leaderSession, leader.AccountId, leader,
            WorldObjectIds.ForPlayer(leader.Id), runtime.InstanceId,
            joinedAt: startedAt);
        registry.JoinWorldInstance(memberSession, member.AccountId, member,
            WorldObjectIds.ForPlayer(member.Id), runtime.InstanceId,
            joinedAt: startedAt);

        var reservation = Guid.NewGuid();
        Check.True(
            registry.TryStartWonderlandEncounter(runtime.InstanceId,
                dailyLimit: 3,
                [
                    new(leaderSession, leader.AccountId, leader.Id,
                        leader.Name, leader.Level, leader.Profession,
                        RealmId.Tempest, runtime.InstanceId, WonderlandMap,
                        leaderFence),
                    new(memberSession, member.AccountId, member.Id,
                        member.Name, member.Level, member.Profession,
                        RealmId.Tempest, runtime.InstanceId, WonderlandMap,
                        memberFence)
                ],
                startedAt,
                reservation),
            "the roster fixture admits a real two-player Wonderland run");
        registry.RecordWonderlandAdmissions(reservation,
            [leader.Id, member.Id]);
        registry.CompleteWonderlandAdmissions(reservation);

        await registry.AdvanceMonsterWorldOnceAsync(startedAt.AddSeconds(1),
            CancellationToken.None);
        var firstRoster = LastRosterPacket(memberTransport);
        Check.True(
            firstRoster is not null &&
            RosterCount(firstRoster) == 2 &&
            RosterCharacterId(firstRoster, 0) == LeaderCharacterId &&
            RosterCharacterId(firstRoster, 1) == MemberCharacterId &&
            RosterState(firstRoster, 0) == 1 &&
            RosterState(firstRoster, 1) ==
                (byte)RepetitionMemberState.Online,
            "the roster marks the run's leader 1 and the other online member " +
            "with the online candidate");

        // The member drops. He must stay on the list the leader still sees, with
        // his own recorded name, level and profession, marked offline.
        registry.Remove(memberSession);
        await registry.AdvanceMonsterWorldOnceAsync(startedAt.AddSeconds(2),
            CancellationToken.None);
        var afterDrop = LastRosterPacket(leaderTransport);
        Check.True(
            afterDrop is not null &&
            RosterCount(afterDrop) == 2 &&
            RosterCharacterId(afterDrop, 1) == MemberCharacterId &&
            ReadName(afterDrop, 1) == member.Name &&
            RosterLevel(afterDrop, 1) == member.Level &&
            afterDrop[8 + MemberBytes + 41] == member.Profession &&
            RosterState(afterDrop, 0) == 1 &&
            RosterState(afterDrop, 1) == 0,
            "a dropped member stays on the published roster as offline");

        Check.True(
            registry.TryResolveInstanceReconnect(
                member.Id,
                member.AccountId,
                RealmId.Tempest,
                WonderlandMap,
                out var target) &&
            target.InstanceId == runtime.InstanceId &&
            Math.Abs(target.EntranceX - WonderlandArrivalX) < 0.001f &&
            Math.Abs(target.EntranceZ - WonderlandArrivalZ) < 0.001f,
            "a dropped member is offered his running instance at the instance " +
            "caller's own arrival");

        var rejoinedObjectId = registry.JoinPlayerReconnectedInstance(
            memberSession,
            member.AccountId,
            member,
            target.InstanceId,
            worldReady: false);
        Check.True(
            rejoinedObjectId > 0 &&
            registry.IsSessionInWorldInstance(memberSession,
                runtime.InstanceId) &&
            registry.GetWorldInstancePopulation(runtime.InstanceId) == 2,
            "the reconnected member rejoins the exact instance before his " +
            "world handshake");

        registry.Remove(leaderSession);
        Check.True(
            !registry.TryResolveInstanceReconnect(
                member.Id,
                member.AccountId,
                RealmId.Tempest,
                WonderlandMap,
                out _),
            "an instance holding nobody else is never reconnected into");

        // A terminal run is never reconnected into either, even with the
        // character still on its admission record.
        registry.JoinWorldInstance(leaderSession, leader.AccountId, leader,
            WorldObjectIds.ForPlayer(leader.Id), runtime.InstanceId,
            joinedAt: startedAt);
        runtime.Map.CancelWonderland(DateTimeOffset.UtcNow);
        Check.True(
            !registry.TryResolveInstanceReconnect(
                leader.Id,
                leader.AccountId,
                RealmId.Tempest,
                WonderlandMap,
                out _),
            "a run that is no longer running is never reconnected into");
    }

    private static byte[]? LastRosterPacket(
        FactionCrierCaptureTransport transport) =>
        transport.ReadLegacyPackets()
            .Where(static packet => packet.Length >= 8 &&
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
                    Opcodes.RepetitionInstanceMembers)
            .LastOrDefault();

    private static int RosterCount(byte[] packet) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));

    private static int RosterCharacterId(byte[] packet, int index) =>
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.AsSpan(8 + index * MemberBytes));

    private static int RosterLevel(byte[] packet, int index) =>
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.AsSpan(8 + index * MemberBytes + 36));

    private static byte RosterState(byte[] packet, int index) =>
        packet[8 + index * MemberBytes + 40];

    private static string ReadName(byte[] packet, int index) =>
        PacketText.ReadFixedAscii(packet, 8 + index * MemberBytes + 4, 32);

    private static GameCharacter CreateCharacter(int characterId, string name) =>
        new()
        {
            Id = characterId,
            AccountId = checked(20_000 + characterId),
            Name = name,
            RealmId = RealmId.Tempest,
            CreatedUtc = DateTime.UtcNow,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = WonderlandMap,
            PositionX = WonderlandArrivalX,
            PositionZ = WonderlandArrivalZ,
            Level = 130,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CurrentMp = 10_000,
            MaxMp = 10_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
}
