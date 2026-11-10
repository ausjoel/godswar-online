using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandReviveCheckName =
        "Wonderland native death and revival preserve the run and reject invalid revival requests";

    public static async Task RunWonderlandReviveAsync()
    {
        var legacy = Convert.FromHexString("1C0022274F0200000000164300000000000012C30000000001000000");
        Check.True(PacketBuilder.PlayerDeath(0x24F, 150, 0, -146, 0).SequenceEqual(legacy),
            "the scoped Wonderland correction preserves the exact historical city death packet");
        Check.True(ReviveRequest.TryParse(Convert.FromHexString("0C0023274814000002000000"), out var old) &&
            old.PlayerObjectId == 0x1448 && old.ReviveType == 2 &&
            ReviveRequest.TryParse(Convert.FromHexString("0C002C274814000002000000"), out var native) && native == old,
            "historical and installed-client native free revival retain the same twelve-byte contract");

        await CheckWonderlandTerminalCityReviveAsync();

        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var leader = fixture.Party.Leader;
        var observer = fixture.Party.Followers.Single();
        var registry = leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var initialDailyClaims = fixture.Daily.Claims.Count;
        runtime.Map.TryGetWonderlandSnapshot(out var before);
        var monsterIds = runtime.Map.SnapshotMonsters().Select(monster => monster.ObjectId).Order().ToArray();
        var entrance = WonderlandTerrainPolicy.GetIsland(1).Entrance;
        leader.Character.CurrentHp = 0;
        leader.Character.CurrentMp = 0;
        leader.Character.PositionX = 159;
        leader.Character.PositionZ = -163;
        registry.UpdateCharacter(leader.Session, leader.Character, advanceWorldRevision: false);
        var beforeLife = registry.GetPlayerLifeRevision(leader.Session);
        var packetCount = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x9999));
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448, reviveType: 0));
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448, reviveType: 1));
        var malformed = CreateWonderlandRevivePacket(0x1448).Buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(malformed, 8);
        await InvokeAsync(leader.Handler, new GamePacket(malformed));
        var trailing = CreateWonderlandRevivePacket(0x1448).Buffer.Concat(new byte[4]).ToArray();
        await InvokeAsync(leader.Handler, new GamePacket(trailing));
        Check.True(leader.Character.CurrentHp == 0 && registry.GetPlayerLifeRevision(leader.Session) == beforeLife &&
            leader.ReadPackets().Skip(packetCount).All(packet =>
                ReadOpcode(packet) is not (Opcodes.SceneChange or 10097)),
            "wrong object, paid types, short declared frame, and undeclared trailing bytes cannot revive or relocate");

        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime).AddSeconds(35), CancellationToken.None);
        Check.True(leader.Character.CurrentMap == 207 && GetSourceInstanceId(leader) == runtime.InstanceId &&
            runtime.Map.TryGetWonderlandSnapshot(out var dead) && dead.State == WonderlandRunState.Active,
            "a dead admitted player remains in Wonderland beyond the previously observed city-transfer interval");
        var observerPacketCount = observer.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448));
        Check.True(!leader.Session.IsDisconnected && GetSourceInstanceId(leader) == runtime.InstanceId &&
            leader.Character.CurrentMap == 207 && leader.Character.PositionX == entrance.X &&
            leader.Character.PositionZ == entrance.Z && entrance.X == 169 && entrance.Z == -216 &&
            leader.Character.CurrentHp == Math.Max(1, leader.Character.MaxHp / 10) &&
            leader.Character.CurrentMp == Math.Max(0, leader.Character.MaxMp / 10) &&
            registry.GetPlayerLifeRevision(leader.Session) == beforeLife + 1 &&
            leader.ReadPackets().Skip(packetCount).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
            "native free revival restores ten percent once at the exact island-one entrance without leaving the run");
        var revivalPackets = leader.ReadPackets().Skip(packetCount).ToArray();
        var sceneIndex = Array.FindIndex(revivalPackets, packet => ReadOpcode(packet) == Opcodes.SceneChange);
        var vitalsIndex = Array.FindIndex(revivalPackets, packet => ReadOpcode(packet) == 10097);
        Check.True(vitalsIndex >= 0 && vitalsIndex < sceneIndex &&
            revivalPackets[vitalsIndex].SequenceEqual(PacketBuilder.PlayerVitalsUpdate(0x1448,
                leader.Character.CurrentHp, leader.Character.CurrentMp)),
            "native HP/MP is restored before SceneChange selects stand, so zero HP cannot force the death animation");
        var worldObjectId = registry.GetRequiredPlayerObjectId(leader.Session);
        var observerBeforeReady = observer.Transport.ReadLegacyPackets().Skip(observerPacketCount).ToArray();
        Check.True(observerBeforeReady.Any(packet =>
                packet.SequenceEqual(PacketBuilder.RemoveWorldObjects(worldObjectId))) &&
            observerBeforeReady.All(packet => ReadOpcode(packet) is not (Opcodes.SceneChange or 10097)),
            "observers remove the old corpse while only the local avatar receives the revival preparation and scene reset");
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        Check.True(observer.Transport.ReadLegacyPackets().Skip(observerPacketCount).Any(packet =>
                ReadOpcode(packet) == 10021 && packet.Length >= 52 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == worldObjectId &&
                BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(44)) == leader.Character.CurrentHp),
            "after scene readiness observers receive a fresh avatar with restored HP in its world identity");
        var revivedLife = registry.GetPlayerLifeRevision(leader.Session);
        var revivedPackets = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448));
        Check.True(registry.GetPlayerLifeRevision(leader.Session) == revivedLife &&
            leader.ReadPackets().Skip(revivedPackets).All(packet =>
                ReadOpcode(packet) is not (Opcodes.SceneChange or 10097)),
            "repeating native revival while alive cannot advance life or reload again");
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var after) && after.State == WonderlandRunState.Active &&
            after.StartedAt == before.StartedAt && after.Deadline == before.Deadline &&
            after.CompletedIslands == before.CompletedIslands && after.Participants.SequenceEqual(before.Participants) &&
            runtime.Map.SnapshotMonsters().Select(monster => monster.ObjectId).Order().SequenceEqual(monsterIds) &&
            initialDailyClaims > 0 && fixture.Daily.Claims.Count == initialDailyClaims && fixture.Titles.Requests.Count == 0,
            "death and revival preserve the original clock, admissions, enemies, progression, and daily claim");
    }
}
