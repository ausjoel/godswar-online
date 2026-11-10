using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandEntryCheckName =
        "Wonderland handler seals actual admissions, gates both island bosses, and revives at the first island";

    public static async Task RunWonderlandEntryAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 3,
            failedFollowers: new HashSet<int> { 2 });
        var party = fixture.Party;
        var leader = party.Leader;
        var registry = leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var admitted = new[] { leader.Character.Id, party.Followers[0].Character.Id }.Order().ToArray();
        Check.True(runtime.Map.Population == 2 && party.Followers[1].Character.CurrentMap != 207 &&
            fixture.Daily.Claims.Single().InstanceKind == InstanceCallerEntryKind.Wonderland &&
            fixture.Daily.Claims.Single().CharacterIds.Count == 3 &&
            fixture.Daily.Admissions.SelectMany(value => value.CharacterIds).Distinct().Order().SequenceEqual(admitted),
            "partial entry claims the original three, but records only the two successful transfers");
        var reservation = fixture.Daily.Claims.Single().ReservationId;
        registry.RecordWonderlandAdmissions(reservation, [party.Followers[1].Character.Id]);
        var failed = party.Followers[1];
        var failedSource = registry.GetMapSessions(failed.Character.CurrentMap)
            .Single(context => ReferenceEquals(context.Session, failed.Session));
        var failedPosition = (failed.Character.PositionX, failed.Character.PositionZ);
        var failedPackets = failed.Transport.ReadLegacyPackets().Count;
        var entrance = WonderlandTerrainPolicy.GetIsland(1).Entrance;
        var lateEntry = new AuthoritativeInstanceTransitionCommand(failed.Character.Id,
            failedSource.WorldInstanceId, failedSource.MapId, failedSource.Ownership,
            runtime.InstanceId, 207, entrance.X, entrance.Z);
        Check.True(!await InvokeAuthoritativeTransitionAsync(failed.Handler, lateEntry, CancellationToken.None) &&
            registry.TryGetSessionWorldInstanceId(failed.Session, out var stillSource) &&
            stillSource == failedSource.WorldInstanceId && failed.Character.CurrentMap == failedSource.MapId &&
            (failed.Character.PositionX, failed.Character.PositionZ) == failedPosition && runtime.Map.Population == 2 &&
            failed.Transport.ReadLegacyPackets().Skip(failedPackets)
                .All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "a failed original entrant cannot bypass the sealed actual roster with a valid late transfer command");
        runtime.Map.TryGetWonderlandSnapshot(out var sealedRun);
        Check.True(sealedRun.Participants.Select(member => member.CharacterId).Order().SequenceEqual(admitted),
            "live encounter balance is sealed to the actual two admitted participants");

        var beforeLocked = leader.ReadPackets().Count;
        await ClickWonderlandPortalAsync(leader, 1);
        Check.True(GetSourceInstanceId(leader) == runtime.InstanceId &&
            leader.ReadPackets().Skip(beforeLocked).All(packet => ReadOpcode(packet) != Opcodes.SceneChange) &&
            !registry.TryResolveWonderlandTravel(leader.Session, 1, out _, out _),
            "the real island teleporter cannot skip Alpha Demon before its committed death");
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var firstTitle = WonderlandTitlePolicy.Resolve(1).TitleId;
        var firstAward = fixture.Titles.Requests.Single();
        Check.True(firstAward.IslandNumber == 1 && firstAward.Award.TitleId == firstTitle &&
            firstAward.AdmissionReservationId == reservation && firstAward.WorldInstanceId == runtime.InstanceId &&
            firstAward.AdmittedCharacterIds.SequenceEqual(admitted) && firstAward.CharacterIds.SequenceEqual(admitted) &&
            leader.Character.OwnedTitleIds.Contains(firstTitle) &&
            party.Followers[0].Character.OwnedTitleIds.Contains(firstTitle) &&
            !party.Followers[1].Character.OwnedTitleIds.Contains(firstTitle) &&
            leader.Character.SelectedTitleId == 0 && party.Followers[0].Character.SelectedTitleId == 5009,
            "Alpha's clear grants the first island title only to actual admitted finishers without equipping it");
        var beforeTravel = leader.ReadPackets().Count;
        await ClickWonderlandPortalAsync(leader, 1);
        var secondEntrance = WonderlandTerrainPolicy.GetIsland(2).Entrance;
        Check.True(GetSourceInstanceId(leader) == runtime.InstanceId && leader.Character.CurrentMap == 207 &&
            leader.Character.PositionX == secondEntrance.X && leader.Character.PositionZ == secondEntrance.Z &&
            leader.ReadPackets().Skip(beforeTravel).Count(packet => packet.SequenceEqual(
                PacketBuilder.SceneChange(0x1448, secondEntrance.X, 0, secondEntrance.Z, 207))) == 1,
            "unlocking island one changes the client scene while preserving exact dungeon identity");
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);

        Check.True(registry.TryGetPlayerLifeRevision(leader.Session, out var beforeLife),
            "revive fixture owns its original player life");
        leader.Character.CurrentHp = 0;
        leader.Character.CurrentMp = 0;
        leader.Character.PositionX = WonderlandTerrainPolicy.GetIsland(2).Center.X;
        leader.Character.PositionZ = WonderlandTerrainPolicy.GetIsland(2).Center.Z;
        registry.UpdateCharacter(leader.Session, leader.Character, advanceWorldRevision: false);
        var beforeRevive = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x9999));
        Check.Equal(0, leader.Character.CurrentHp, "spoofed native revive cannot restore another player object");
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448));
        Check.True(!leader.Session.IsDisconnected && GetSourceInstanceId(leader) == runtime.InstanceId &&
            leader.Character.CurrentHp == Math.Max(1, leader.Character.MaxHp / 10) &&
            leader.Character.CurrentMp == Math.Max(0, leader.Character.MaxMp / 10) &&
            registry.TryGetPlayerLifeRevision(leader.Session, out var afterLife) && afterLife > beforeLife &&
            leader.Character.PositionX == entrance.X && leader.Character.PositionZ == entrance.Z &&
            leader.ReadPackets().Skip(beforeRevive).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
            "native free revive restores ten percent at the first island entrance in the same instance");
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);

        runtime.Map.TryGetWonderlandSnapshot(out var second);
        var derskey = second.ActiveSpawns.Single(value => value.Role == WonderlandMonsterRole.Derskey);
        var monkey = second.ActiveSpawns.Single(value => value.Role == WonderlandMonsterRole.Monkeyface);
        var death = KillWonderlandHandlerMonster(fixture, runtime, derskey.ObjectId);
        registry.RecordWonderlandMonsterKillCommitted(runtime, death, WonderlandNow(runtime));
        runtime.Map.TryGetWonderlandSnapshot(out var half);
        Check.True(half.CompletedIslands == 1 && half.RequiredMonstersRemaining == 1 &&
            !registry.HasPendingWonderlandTitles(runtime.InstanceId) && fixture.Titles.Requests.Count == 1 &&
            fixture.Titles.Requests.All(request => request.IslandNumber == 1),
            "Derskey death and its replay neither replace Monkeyface nor award an island-two title");
        var beforeSecondLocked = leader.ReadPackets().Count;
        await ClickWonderlandPortalAsync(leader, 2);
        Check.True(leader.ReadPackets().Skip(beforeSecondLocked)
                .All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "the second island teleporter stays closed while Monkeyface is alive");
        KillWonderlandHandlerMonster(fixture, runtime, monkey.ObjectId);
        Check.True(registry.HasPendingWonderlandTitles(runtime.InstanceId),
            "the second distinct required boss freezes a pending title entitlement");
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        var award = fixture.Titles.Requests.Single(request => request.IslandNumber == 2);
        Check.True(award.IslandNumber == 2 && award.AdmissionReservationId == reservation &&
            award.WorldInstanceId == runtime.InstanceId && award.AdmittedCharacterIds.SequenceEqual(admitted) &&
            award.CharacterIds.SequenceEqual(admitted) && fixture.Titles.Requests.Count == 2,
            "sealed title evidence excludes the failed transfer even after a late admission-marker replay");
        var firstTwoTitles = Enumerable.Range(1, 2).Select(island => WonderlandTitlePolicy.Resolve(island).TitleId).ToArray();
        Check.True(firstTwoTitles.All(leader.Character.OwnedTitleIds.Contains) &&
            firstTwoTitles.All(party.Followers[0].Character.OwnedTitleIds.Contains) &&
            firstTwoTitles.All(title => !party.Followers[1].Character.OwnedTitleIds.Contains(title)) &&
            leader.Character.SelectedTitleId == 0 && party.Followers[0].Character.SelectedTitleId == 5009 &&
            party.Characters.All(character => character.MedusaHonorPoints == 1234),
            "earned titles reach only actual finishers and preserve both unequipped and existing selections without wallet bonuses");
        Check.True(registry.TryResolveWonderlandTravel(leader.Session, 2, out var targetInstance, out _) &&
            targetInstance == runtime.InstanceId, "both required deaths unlock island three in the same dungeon");
    }

    private static GamePacket CreateWonderlandRevivePacket(uint playerObjectId,
        int reviveType = ReviveRequest.FreeReviveType, ushort opcode = Opcodes.NativeRevive)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), playerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), reviveType);
        return new GamePacket(bytes);
    }
}
