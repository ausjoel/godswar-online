using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerMovementEcsLiveAdapterChecks
{
    public static async Task RunUniversalControlsAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            foreach (var status in new uint[] { 330, 331, 299, 360, 404, 1446, 1448 })
                await CheckOrdinaryMovementControlAsync(mode, status);
        }
    }

    private static async Task CheckOrdinaryMovementControlAsync(PlayerRuntimeMode mode, uint status)
    {
        await using var transport = new RealtimeMovementControlTransport();
        await using var session = new ClientSession(transport);
        var character = CreateCharacter(CharacterId, AccountId, $"Control{status}{mode}");
        var registry = CreateRegistry(mode);
        registry.JoinPlayerMap(session, AccountId, character);
        var store = new RecordingPositionStore();
        var handler = CreateHandler(session, store, registry, character);
        try
        {
            var now = DateTimeOffset.UtcNow;
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(session,
                MovementControlDefinition(status, TimeSpan.FromMinutes(1)), now,
                "movement-control-regression", CancellationToken.None),
                $"{mode}/{status} applies real ordinary runtime status");
            await FlushMovementWritesAsync(session, transport);

            var blocked = status is 330 or 331 or 299 or 1446;
            Check.Equal(!blocked, registry.IsPlayerStatusMovementAllowed(session, now),
                $"{mode}/{status} native NonMoving authority");
            Check.True(registry.IsPlayerStatusMovementAllowed(session, now.AddMinutes(2)),
                $"{mode}/{status} expired control no longer blocks movement");
            if (status == 299)
                Check.True(registry.GetPlayerSkillCastControl(session, now) == PlayerSkillCastControl.None,
                    "HaltIntonate-only Frozen does not continuously block skills");

            // Both native Begin/End opcodes and the position-bearing10194
            // packet pass through the actual handler dispatch.
            for (var attempt = 0; attempt < 8; attempt++)
            foreach (var opcode in new[] { Opcodes.WalkBegin, Opcodes.WalkEnd, Opcodes.Walk })
            {
                var packet = CreateWalkPacket(0x0002_1448, 3f, 4f).Buffer;
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
                await InvokePacketAsync(handler, new GamePacket(packet));
                var writes = transport.TakeClearLegacyWrites();
                if (blocked)
                {
                    Check.Equal(0, writes.Length,
                        $"{mode}/{status}/{opcode} rejects repeated input without camera-reset packet spam");
                    Check.True(character.PositionX == 0f && character.PositionZ == 0f && store.SaveAttempts == 0,
                        $"{mode}/{status}/{opcode} does not move or persist");
                }
            }
            if (!blocked)
                Check.True(character.PositionX == 3f && character.PositionZ == 4f && store.SaveAttempts > 0,
                    $"{mode}/{status} silence permits real movement and persistence");

            // Expire a short replacement through the real publication timer,
            // preserving the monotonically increasing status observation time.
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(session,
                MovementControlDefinition(status, TimeSpan.FromMilliseconds(10)), DateTimeOffset.UtcNow,
                "movement-control-expired", CancellationToken.None), "expired replacement is reconciled");
            await Task.Delay(50);
            await FlushMovementWritesAsync(session, transport);
            await InvokePacketAsync(handler, CreateWalkPacket(0x0002_1448, 5f, 6f));
            Check.True(character.PositionX == 5f && character.PositionZ == 6f,
                $"{mode}/{status} actual handler accepts movement after control expiry");
        }
        finally { registry.Remove(session); }
    }

    private static SkillStatusEffectDefinition MovementControlDefinition(uint status, TimeSpan duration) =>
        new(0, status, checked((int)status), 1, false, duration, TimeSpan.Zero, 0, 0);

    private static async Task FlushMovementWritesAsync(ClientSession session,
        RealtimeMovementControlTransport transport)
    {
        // Exact status publication returns on admission; this queued marker
        // waits for its physical transport write before starting assertions.
        await session.SendAsync(PacketBuilder.ServerNote("Movement test boundary"),
            CancellationToken.None, "MovementTestBoundary");
        transport.TakeClearLegacyWrites();
    }
}
