using Godswar.Server.Domain.Characters;
using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    public const string SameMapCheckName =
        "Native faction portal return within destination map";

    public static async Task RunSameMapAsync()
    {
        foreach (var skillId in new[]
                 {
                     FactionPortalSkillPolicy.AthensCapitalPortalSkillId,
                     FactionPortalSkillPolicy.AthensSuburbPortalSkillId,
                     FactionPortalSkillPolicy.SpartaCapitalPortalSkillId,
                     FactionPortalSkillPolicy.SpartaSuburbPortalSkillId
                 })
        {
            await CheckSameMapCastAsync(skillId, rejectPositionWrite: false);
        }
        await CheckSameMapCastAsync(
            FactionPortalSkillPolicy.SpartaCapitalPortalSkillId,
            rejectPositionWrite: true);
    }

    private static async Task CheckSameMapCastAsync(
        uint skillId,
        bool rejectPositionWrite)
    {
        Check.True(BackhaulSkillCatalog.TryGet(skillId, out var definition),
            $"same-map fixture resolves faction portal {skillId}");
        var transport = new FactionCrierCaptureTransport();
        await using var session = new ClientSession(transport);
        var character = CreateCharacter($"SameMap{skillId}",
            checked((byte)definition.RequiredCamp));
        character.CurrentMap = definition.TargetMapId;
        character.PositionX = definition.TargetX + 12f;
        character.PositionZ = definition.TargetZ - 8f;
        var sourceX = character.PositionX;
        var sourceZ = character.PositionZ;
        var initialMp = character.CurrentMp;
        var store = new BackhaulStore(character,
            [new SkillState { SkillId = checked((int)skillId), Level = 1 }])
        {
            RejectPositionWrite = rejectPositionWrite
        };
        var registry = CreateRegistry();
        GameHandlerOwnershipTestFences.Bind(registry, session, AccountId, character);
        registry.JoinMap(session, AccountId, character,
            WorldObjectIds.ForPlayer(CharacterId), worldReady: true, joinedAt: TestTime);
        Check.True(registry.TryGetSessionWorldInstanceId(session, out var originalWorld),
            "same-map portal starts with authoritative world membership");
        var handler = CreateEnteredHandler(session, store, registry, character);
        const float forgedX = 8_765f;
        const float forgedZ = -7_654f;
        try
        {
            await InvokePacketAsync(handler,
                CreateSkillCastPacket(skillId, sourceX, sourceZ, forgedX, forgedZ));
            await WaitForSameMapCastCompletionAsync(handler, transport,
                expectSceneChange: !rejectPositionWrite);
            var packets = transport.ReadLegacyPackets();
            if (rejectPositionWrite)
            {
                Check.True(!packets.Any(packet => ReadUInt16(packet, 2) == Opcodes.SceneChange) &&
                    character.CurrentMap == definition.TargetMapId &&
                    character.PositionX == sourceX && character.PositionZ == sourceZ &&
                    store.PositionWrites.Count == 0 && !session.IsDisconnected,
                    "failed same-map persistence leaves the original position and session intact");
                Check.True(character.CurrentMp == initialMp &&
                    store.VitalsWrites.Select(static write => write.CurrentMp)
                        .SequenceEqual([initialMp - definition.ManaCost, initialMp]) &&
                    packets.Where(packet => ReadUInt16(packet, 2) == 0x2797)
                        .Select(packet => ReadInt32(packet, 8))
                        .SequenceEqual([initialMp - definition.ManaCost, initialMp]),
                    "failed same-map persistence refunds MP in authority, storage, and client updates");
                Check.True(registry.IsSessionInWorldInstance(session, originalWorld) &&
                    registry.GetMapSessions(definition.TargetMapId).Any(context =>
                        ReferenceEquals(context.Session, session) && context.WorldReady),
                    "failed same-map persistence preserves visible original-world membership");
                return;
            }

            var scene = packets.Single(packet => ReadUInt16(packet, 2) == Opcodes.SceneChange);
            Check.True(ReadUInt16(scene, 22) == definition.TargetMapId &&
                ReadSingle(scene, 8) == definition.TargetX &&
                ReadSingle(scene, 16) == definition.TargetZ &&
                ReadSingle(scene, 8) != forgedX && ReadSingle(scene, 16) != forgedZ,
                $"same-map portal {skillId} uses the authoritative catalog arrival");
            Check.True(packets.Count(packet => ReadUInt16(packet, 2) == Opcodes.SkillCast) == 1 &&
                packets.Where(packet => ReadUInt16(packet, 2) == 0x2797)
                    .Select(packet => ReadInt32(packet, 8))
                    .SequenceEqual([initialMp - definition.ManaCost]),
                $"same-map portal {skillId} emits one native cast and MP charge");
            Check.True(character.CurrentMp == initialMp - definition.ManaCost &&
                store.VitalsWrites is [var vitals] &&
                vitals.CurrentMp == initialMp - definition.ManaCost &&
                store.PositionWrites is [var position] &&
                position.MapId == definition.TargetMapId &&
                position.X == definition.TargetX && position.Z == definition.TargetZ,
                $"same-map portal {skillId} persists one MP charge and one relocation");
            Check.True(character.CurrentMap == definition.TargetMapId &&
                character.PositionX == definition.TargetX &&
                character.PositionZ == definition.TargetZ &&
                registry.IsSessionInWorldInstance(session, originalWorld) &&
                registry.GetMapPopulation(definition.TargetMapId) == 1 &&
                !registry.GetMapSessions(definition.TargetMapId).Any(context =>
                    ReferenceEquals(context.Session, session)),
                $"same-map portal {skillId} retains one hidden actor in its original world");

            await InvokePacketAsync(handler, CreateControlPacket(Opcodes.ClientReady));
            Check.True(!registry.GetMapSessions(definition.TargetMapId).Any(context =>
                    ReferenceEquals(context.Session, session)),
                $"same-map portal {skillId} remains hidden after ClientReady alone");
            await InvokePacketAsync(handler, CreateSameMapPlayerDetailRequest());
            Check.True(registry.IsSessionInWorldInstance(session, originalWorld) &&
                registry.GetMapSessions(definition.TargetMapId).Count(context =>
                    ReferenceEquals(context.Session, session) && context.WorldReady) == 1 &&
                !session.IsDisconnected,
                $"same-map portal {skillId} becomes visible after native readiness completes");

            var packetCount = transport.ReadLegacyPackets().Count;
            await InvokePacketAsync(handler, CreateSkillCastPacket(skillId,
                character.PositionX, character.PositionZ, forgedX, forgedZ));
            Check.True(!HasPendingSkillCast(handler) &&
                transport.ReadLegacyPackets().Count == packetCount &&
                character.CurrentMp == initialMp - definition.ManaCost &&
                store.VitalsWrites.Count == 1 && store.PositionWrites.Count == 1,
                $"same-map portal {skillId} cooldown blocks a second cast without another charge");
        }
        finally
        {
            await StopHandlerAsync(handler);
            registry.Remove(session);
        }
    }

    private static async Task WaitForSameMapCastCompletionAsync(
        GameClientHandler handler,
        FactionCrierCaptureTransport transport,
        bool expectSceneChange)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (HasPendingSkillCast(handler) ||
               !transport.ReadLegacyPackets().Any(packet =>
                   ReadUInt16(packet, 2) ==
                       (expectSceneChange ? Opcodes.SceneChange : 0x2797)))
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static GamePacket CreateSameMapPlayerDetailRequest()
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.PlayerDetailRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), LocalPlayerObjectId);
        return new GamePacket(bytes);
    }
}
