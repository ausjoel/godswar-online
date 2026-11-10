using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    public static async Task RunUniversalControlsAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckOrdinaryQueuedMovementControlAsync(mode);
            await CheckStunDuringRealtimeInterruptionAsync(mode);
        }
    }

    private static async Task CheckOrdinaryQueuedMovementControlAsync(PlayerRuntimeMode mode)
    {
        await using var transport = new RealtimeMovementControlTransport();
        await using var session = new ClientSession(transport);
        var character = CreateCharacter(CharacterId, AccountId, $"QueuedControl{mode}");
        var registry = new GameSessionRegistry(null, null, MonsterRuntimeMode.Ecs, mode);
        registry.InitializeMapMonsters(character.CurrentMap, [], TestTime);
        registry.JoinPlayerMap(session, character.AccountId, character);
        var handler = CreateReadyHandler(session, registry, character);
        try
        {
            await ProcessTickAsync(handler);
            var initial = transport.Snapshots.Single();
            transport.EnqueueMovement(CreateIngress(SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input, 1, 1, initial.WorldGeneration,
                0x0002_1448, 0.5f, 0.25f, TimeSpan.FromMilliseconds(100)));
            Check.True(await ApplyMovementControlAsync(registry, session, 330, TimeSpan.FromMinutes(1)),
                "ordinary stun arrives after UDP input is queued");
            var blocked = await ProcessTickAsync(handler);
            Check.True(GetEffectPacket(blocked, "ViewerMovement") is null &&
                character.PositionX == 0f && character.PositionZ == 0f,
                $"{mode} queued movement cannot bypass an ordinary stun");
            Check.True(GetEffectPacket(blocked, "ReliableCorrection") is null,
                "queued stun relies on native NonMoving without a camera-reset packet");
            Check.True(transport.Snapshots.Last().Rejection != SecureRealtimeMovementRejection.None,
                "queued stun preserves authoritative rejection and acknowledgement metadata");
            await FlushControlWritesAsync(session, transport);
            await PublishEffectsAsync(handler, blocked);
            for (ulong input = 2; input <= 8; input++)
            {
                transport.EnqueueMovement(CreateIngress(SecureRealtimeTransportSource.Udp,
                    SecureRealtimeMovementIngressKind.Input, 1, input, initial.WorldGeneration,
                    0x0002_1448, 0.5f, 0.25f, TimeSpan.FromMilliseconds(100 + input * 50)));
                var repeated = await ProcessTickAsync(handler);
                AssertNoMovementEffects(repeated, "repeated stunned realtime input");
                await PublishEffectsAsync(handler, repeated);
            }
            // Even an untagged legacy packet after secure cutover must not
            // sneak through the unrelated cutover correction path while stunned.
            await InvokePacketAsync(handler, new GamePacket(CreateLegacyWalk(0x0002_1448, 3f, 4f)));
            Check.Equal(0, transport.TakeClearLegacyWrites().Length,
                "holding movement while stunned emits no repeated native position reset");
            Check.True(character.PositionX == 0f && character.PositionZ == 0f,
                "all repeated inputs leave the authoritative position unchanged");

            // Let the real expiry publisher retire stun, then leave a genuine
            // silence active: movement resumes while skills remain disabled.
            await ApplyMovementControlAsync(registry, session, 330, TimeSpan.FromMilliseconds(10));
            await Task.Delay(50);
            await ApplyMovementControlAsync(registry, session, 360, TimeSpan.FromMinutes(1));
            Check.True(registry.GetPlayerSkillCastControl(session, DateTimeOffset.UtcNow) ==
                PlayerSkillCastControl.Silenced,
                "only silence remains after stun expiry");
            var current = transport.Snapshots.Last();
            transport.EnqueueMovement(CreateIngress(SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input, 1, 9, current.WorldGeneration,
                0x0002_1448, 0.5f, 0.25f, TimeSpan.FromMilliseconds(600)));
            var resumed = await ProcessTickAsync(handler);
            Check.True(GetEffectPacket(resumed, "ViewerMovement") is not null &&
                character.PositionX == 0.5f && character.PositionZ == 0.25f,
                $"{mode} actual realtime movement resumes under silence after stun expiry");
        }
        finally { registry.Remove(session); }
    }

    private static async Task CheckStunDuringRealtimeInterruptionAsync(PlayerRuntimeMode mode)
    {
        await using var transport = new RealtimeMovementControlTransport();
        await using var session = new ClientSession(transport);
        var character = CreateCharacter(CharacterId + 51, AccountId + 51, $"LateStun{mode}");
        character.CurrentMap = 13;
        character.PositionX = -57f;
        character.PositionZ = 34f;
        var registry = new GameSessionRegistry(null, null, MonsterRuntimeMode.Ecs, mode);
        registry.InitializeMapMonsters(character.CurrentMap, [], TestTime);
        registry.JoinPlayerMap(session, character.AccountId, character);
        var handler = CreateRealtimeCastingHandler(session, new RealtimeBackhaulStore(), registry, character);
        RealtimeMovementControlTransport.WritePause? pause = null;
        try
        {
            await ProcessTickAsync(handler);
            var initial = transport.Snapshots.Single();
            await InvokePacketAsync(handler, CreateRealtimeBackhaulCast(character));
            Check.True(HasRealtimePendingSkillCast(handler), "real backhaul cast is pending before movement");
            transport.TakeClearLegacyWrites();
            pause = transport.PauseNextWrite();
            transport.EnqueueMovement(CreateIngress(SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input, 1, 1, initial.WorldGeneration,
                0x0002_1448, -56.5f, 34.25f, TimeSpan.FromMilliseconds(100), character.CurrentMap));
            var tick = ProcessTickAsync(handler);
            await pause.WaitUntilStartedAsync();
            Check.True(!tick.IsCompleted && character.PositionX == -57f,
                "accepted movement awaits real native cast-interruption publication before position commit");

            var statusApplication = ApplyMovementControlAsync(registry, session, 330, TimeSpan.FromMinutes(1));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (registry.GetPlayerSkillCastControl(session, DateTimeOffset.UtcNow) !=
                       PlayerSkillCastControl.Stunned && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(1);
            Check.True(registry.GetPlayerSkillCastControl(session, DateTimeOffset.UtcNow) ==
                PlayerSkillCastControl.Stunned,
                "ordinary stun commits while movement interruption is awaiting publication");
            pause.Release();
            Check.True(await statusApplication.WaitAsync(TimeSpan.FromSeconds(5)), "concurrent stun publishes");
            var rejected = await tick.WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(character.PositionX == -57f && character.PositionZ == 34f &&
                GetEffectPacket(rejected, "ViewerMovement") is null &&
                rejected.GetType().GetProperty("PositionSave")!.GetValue(rejected) is null,
                $"{mode} stun between acceptance and commit prevents position mutation, persistence and viewer walk");
            Check.True(GetEffectPacket(rejected, "ReliableCorrection") is null,
                "late stun retains native NonMoving without resetting the camera");
            var corrected = transport.Snapshots.Last();
            Check.True(corrected.X == -57f && corrected.Z == 34f &&
                corrected.WorldGeneration != initial.WorldGeneration &&
                corrected.Rejection != SecureRealtimeMovementRejection.None,
                "late rejection rebases movement authority instead of retaining its tentative position");
            Check.True(!HasRealtimePendingSkillCast(handler) && character.CurrentMp == 4000,
                "movement interruption still completes without consuming cast mana");

            // An old authenticated datagram cannot restore the accepted-but-
            // canceled coordinates after the authority generation changed.
            transport.EnqueueMovement(CreateIngress(SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input, 1, 2, initial.WorldGeneration,
                0x0002_1448, -56.5f, 34.25f, TimeSpan.FromMilliseconds(200), character.CurrentMap));
            var stale = await ProcessTickAsync(handler);
            AssertNoMovementEffects(stale, "stale queued generation while stunned");
            Check.True(character.PositionX == -57f && character.PositionZ == 34f,
                "stale queued generation cannot restore canceled movement");
        }
        finally
        {
            pause?.Release();
            await StopRealtimePendingSkillCastsAsync(handler);
            registry.Remove(session);
        }
    }

    private static Task<bool> ApplyMovementControlAsync(GameSessionRegistry registry,
        ClientSession session, uint id, TimeSpan duration) =>
        registry.ApplyRuntimeStatusAndPublishAsync(session,
            new SkillStatusEffectDefinition(0, id, checked((int)id), 1, false,
                duration, TimeSpan.Zero, 0, 0), DateTimeOffset.UtcNow,
            "universal-movement-control", CancellationToken.None);

    private static async Task FlushControlWritesAsync(ClientSession session, RealtimeMovementControlTransport transport)
    {
        await session.SendAsync(PacketBuilder.ServerNote("Realtime control test boundary"),
            CancellationToken.None, "RealtimeControlTestBoundary");
        transport.TakeClearLegacyWrites();
    }
}
