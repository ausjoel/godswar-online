using System.Buffers.Binary;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private static async Task<IReadOnlyList<byte[]>> ReadLethalAttackSequenceAsync(
        BoundaryFixture fixture,
        PlayerRuntimeMode runtimeMode)
    {
        // ECS retains the captured skill impact followed by the basic impact.
        // Legacy emits only the basic impact. Both must finish with player death.
        var impactCount = runtimeMode == PlayerRuntimeMode.Ecs ? 2 : 1;
        var packets = new List<byte[]>
        {
            await ReadLethalFrameAsync(fixture, 8)
        };
        for (var index = 0; index < impactCount; index++)
        {
            var impact = await ReadLethalFrameAsync(fixture, 24);
            Check.True(
                ReadUInt16(impact, 2) == 10046 &&
                ReadUInt32(impact, 4) == LethalAttackMonsterObjectId &&
                ReadUInt32(impact, 8) == 0x1448 &&
                ReadUInt32(impact, 12) == 2000,
                $"{runtimeMode} lethal impact {index + 1} identifies the monster and local player");
            packets.Add(impact);
        }

        var damage = await ReadLethalFrameAsync(fixture, 30);
        Check.True(
            ReadUInt16(damage, 2) == 0x272A &&
            ReadUInt32(damage, 4) == LethalAttackMonsterObjectId &&
            ReadUInt32(damage, 20) == 0x1448 &&
            ReadUInt32(damage, 24) > 0,
            $"{runtimeMode} lethal damage follows the native impact prefix");
        packets.Add(damage);

        // REVIVE: the native 116-byte 10027 MSG_DEAD opens Origin's revive
        // window; the old 28-byte 10018 was a scene load (Polish/REVIVE.md).
        var death = await ReadLethalFrameAsync(fixture, 116);
        Check.True(
            ReadUInt16(death, 2) == 0x272B &&
            ReadUInt32(death, 4) == 0x1448 &&
            Enumerable.Range(0, 5).All(slot => ReadUInt32(death, 8 + slot * 4) == uint.MaxValue) &&
            ReadUInt32(death, 108) == uint.MaxValue,
            $"{runtimeMode} lethal attack finishes with one native death for the local player");
        packets.Add(death);
        return packets;
    }

    private static async Task<byte[]> ReadLethalFrameAsync(
        BoundaryFixture fixture,
        int length)
    {
        var packet = await fixture.Socket.ReadPacketAsync();
        Check.Equal(length, packet.Length, "lethal attack native frame length");
        return packet;
    }
}
