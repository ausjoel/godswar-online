using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckWonderlandStunInputBurstAsync(Fixture f, GameClientHandler handler)
    {
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        AssertWonderlandStatus(f, WonderlandClientStatusIds.Stunned, 2);
        var before = f.Transport.ReadLegacyPackets().Count;
        var x = f.Character.PositionX;
        var z = f.Character.PositionZ;
        var revision = f.Character.PositionRevision;
        var mp = f.Character.CurrentMp;
        var dispatch = typeof(GameClientHandler).GetMethod("HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        for (var attempt = 0; attempt < 8; attempt++)
        foreach (var opcode in new[] { Opcodes.WalkBegin, Opcodes.Walk, Opcodes.WalkEnd })
        {
            var request = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(request, 20);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 0x0002_1448);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(8), x + 0.5f);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(12), z + 0.5f);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(16), 1f);
            await (Task)dispatch.Invoke(handler, [new GamePacket(request), CancellationToken.None])!;
        }
        await f.FlushAsync();
        var after = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
        Check.True(after.All(packet => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) is not
                (Opcodes.Walk or Opcodes.WalkBegin or Opcodes.WalkEnd)),
            "real Wonderland stun rejects held movement without native camera-reset packets");
        Check.True(f.Character.PositionX == x && f.Character.PositionZ == z &&
            f.Character.PositionRevision == revision && f.Character.CurrentMp == mp,
            "held movement does not mutate authoritative position, revision or mana");
        Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) == PlayerSkillCastControl.Stunned,
            "silent movement rejection retains authoritative stun and its skill restriction");
        AssertWonderlandStatus(f, WonderlandClientStatusIds.Stunned, 2);
    }
}
