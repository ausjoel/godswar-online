using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterHandlerChecks
{
    private static async Task
        CheckTransitionCompletionAndObserverRemovalAsync()
    {
        var endpoint = Gatekeeper();
        await using var fixture = await CreateFixtureAsync(endpoint);
        var observerCharacter = new GameCharacter
        {
            Id = CharacterId + 1,
            AccountId = AccountId + 1,
            Name = "DuelArenaObserver",
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = DuelArenaTransporterProtocol.MapId,
            PositionX = endpoint.Spawn.X,
            PositionZ = endpoint.Spawn.Z,
            Level = 140,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
        var observerTransport = new FactionCrierCaptureTransport();
        var observerSession = new ClientSession(observerTransport);
        try
        {
            GameHandlerOwnershipTestFences.Bind(
                fixture.Registry,
                observerSession,
                observerCharacter.AccountId,
                observerCharacter);
            fixture.Registry.JoinMap(
                observerSession,
                observerCharacter.AccountId,
                observerCharacter,
                WorldObjectIds.ForPlayer(observerCharacter.Id),
                worldReady: true);
            var observerBefore =
                observerTransport.ReadLegacyPackets().Count;

            await IssueMenuAsync(fixture, endpoint);
            await InvokeAsync(
                fixture.Handler,
                CreateActionPacket(endpoint.InteractionId));

            var observerPackets = observerTransport.ReadLegacyPackets()
                .Skip(observerBefore)
                .ToArray();
            Check.True(
                observerPackets.Any(packet => packet.SequenceEqual(
                    PacketBuilder.RemoveWorldObjects(
                        WorldObjectIds.ForPlayer(CharacterId)))) &&
                !fixture.Registry.GetMapSessions(
                    DuelArenaTransporterProtocol.MapId).Any(context =>
                        ReferenceEquals(context.Session, fixture.Session)),
                "same-scene travel removes the actor from observers and " +
                "keeps it hidden during client rehydration");

            var observerBeforeReappearance =
                observerTransport.ReadLegacyPackets().Count;
            await InvokeAsync(
                fixture.Handler,
                CreateControlPacket(Opcodes.ClientReady));
            await InvokeAsync(
                fixture.Handler,
                CreatePlayerDetailRequest());

            var catalog = GetHandlerField<
                Dictionary<uint, NpcSpawnDefinition>>(
                    fixture.Handler,
                    "_mapNpcsByInteractionId");
            var expectedReappearance = PacketBuilder.PlayerWorldSpawn(
                fixture.Character,
                WorldObjectIds.ForPlayer(CharacterId));
            var observerReappearancePackets = observerTransport
                .ReadLegacyPackets()
                .Skip(observerBeforeReappearance)
                .Count(packet => packet.SequenceEqual(expectedReappearance));
            Check.True(
                GetHandlerField<object>(
                    fixture.Handler,
                    "_pendingMapTransition") is null &&
                fixture.Registry.GetMapSessions(
                    DuelArenaTransporterProtocol.MapId).Any(context =>
                        ReferenceEquals(context.Session, fixture.Session)) &&
                catalog is { Count: 2 } &&
                catalog.ContainsKey(
                    DuelArenaTransporterProtocol.GatekeeperNpcId) &&
                catalog.ContainsKey(
                    DuelArenaTransporterProtocol.DoorkeeperNpcId) &&
                observerReappearancePackets == 1,
                "ClientReady plus PlayerDetail completes the handoff, " +
                "reveals the actor exactly once, and reloads both Arena " +
                "endpoints");

            var replayPackets = fixture.ReadPackets().Count;
            await InvokeAsync(
                fixture.Handler,
                CreateActionPacket(endpoint.InteractionId));
            Check.True(
                fixture.Store.PositionWrites.Count == 1 &&
                fixture.ReadPackets().Count == replayPackets,
                "a completed handoff still cannot replay its consumed lease");
        }
        finally
        {
            fixture.Registry.Remove(observerSession);
            await observerSession.DisposeAsync();
        }
    }

    private static async Task
        CheckMalformedExpiredAndPersistenceFailureAsync()
    {
        var endpoint = Gatekeeper();
        await using (var malformed = await CreateFixtureAsync(endpoint))
        {
            await IssueMenuAsync(malformed, endpoint);
            await AssertRejectedAsync(
                malformed,
                CreateActionPacket(
                    endpoint.InteractionId,
                    declaredLength:
                        DuelArenaTransporterProtocol.ActionPacketBytes - 4),
                "mismatched declared length");
            await AssertRejectedAsync(
                malformed,
                CreateActionPacket(
                    endpoint.InteractionId,
                    bufferLength:
                        DuelArenaTransporterProtocol.ActionPacketBytes - 4),
                "truncated action frame");
        }

        await using (var expired = await CreateFixtureAsync(endpoint))
        {
            await IssueMenuAsync(expired, endpoint);
            var context = GetHandlerField<
                DuelArenaTransporterDialogueContext>(
                    expired.Handler,
                    "_duelArenaTransporterDialogueContext") ??
                throw new InvalidOperationException(
                    "Arena transporter lease was not issued.");
            SetHandlerField(
                expired.Handler,
                "_duelArenaTransporterDialogueContext",
                context with
                {
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });
            await AssertRejectedAsync(
                expired,
                CreateActionPacket(endpoint.InteractionId),
                "expired menu lease");
        }

        await using var failed = await CreateFixtureAsync(endpoint);
        await IssueMenuAsync(failed, endpoint);
        failed.Store.RejectPositionWrite = true;
        var packetCount = failed.ReadPackets().Count;
        await InvokeAsync(
            failed.Handler,
            CreateActionPacket(endpoint.InteractionId));
        var failurePackets = failed.ReadPackets()
            .Skip(packetCount)
            .ToArray();
        Check.True(
            failurePackets.Length == 1 &&
            failurePackets[0].SequenceEqual(PacketBuilder.ServerNote(
                "Duel Arena transportation is temporarily unavailable.")) &&
            failed.Store.PositionWrites.Count == 0 &&
            !failed.Session.IsDisconnected &&
            GetHandlerField<DuelArenaTransporterDialogueContext>(
                failed.Handler,
                "_duelArenaTransporterDialogueContext") is null,
            "persistence failure consumes the lease, keeps source " +
            "authority, and returns the finite unavailable note");
    }

    private static async Task CheckPostPersistenceSceneFenceAsync()
    {
        var endpoint = Gatekeeper();
        await using var fixture = await CreateFixtureAsync(endpoint);
        await IssueMenuAsync(fixture, endpoint);
        var packetsBefore = fixture.ReadPackets().Count;
        fixture.Store.AfterPositionWrite = () =>
        {
            fixture.Character.CurrentMap = GameDefaults.SpartaCapitalMap;
            fixture.Registry.JoinMap(
                fixture.Session,
                AccountId,
                fixture.Character,
                WorldObjectIds.ForPlayer(CharacterId),
                worldReady: true);
        };

        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(endpoint.InteractionId));

        Check.True(
            fixture.Store.PositionWrites is [var write] &&
            write.MapId == DuelArenaTransporterProtocol.MapId &&
            write.X == endpoint.TargetX &&
            write.Z == endpoint.TargetZ &&
            fixture.Session.IsDisconnected &&
            fixture.Character.CurrentMap == GameDefaults.SpartaCapitalMap &&
            fixture.Character.PositionX == endpoint.Spawn.X &&
            fixture.Character.PositionZ == endpoint.Spawn.Z &&
            !fixture.ReadPackets().Skip(packetsBefore).Any(packet =>
                ReadOpcode(packet) == Opcodes.SceneChange),
            "a source map/world change after durable persistence " +
            "disconnects before applying or emitting split scene state");
    }

    private static GamePacket CreateControlPacket(ushort opcode)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), opcode);
        return new GamePacket(bytes);
    }

    private static GamePacket CreatePlayerDetailRequest()
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.PlayerDetailRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            LocalPlayerObjectId);
        return new GamePacket(bytes);
    }
}
