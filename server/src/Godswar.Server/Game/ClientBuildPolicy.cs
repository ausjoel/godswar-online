// ROUND3: patch-channel compatibility gate; the stamp is not client attestation.
using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal static class ClientBuildPolicy
{
    // tools/r2client.py and rates_probe.py: 32 + 32 + 68 bytes before the stamp.
    internal const int BuildPayloadOffset = 132;

    public static bool Allows(GamePacket packet, uint minimumBuild) =>
        minimumBuild == 0 ||
        (packet.Opcode == Opcodes.Login &&
         packet.Payload.Length >= BuildPayloadOffset + sizeof(uint) &&
         BinaryPrimitives.ReadUInt32LittleEndian(
             packet.Payload.Slice(BuildPayloadOffset, sizeof(uint))) >= minimumBuild);
}
