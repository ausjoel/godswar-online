using System.Buffers.Binary;

namespace Godswar.Server.Protocol;

internal static class TitleSelectionProtocol
{
    // Native DesignateUI Change/Hide buttons both send eight bytes:
    // u16 length, u16 10198, u32 owned title ID (zero means hide).
    public static bool TryRead(GamePacket packet, out uint titleId)
    {
        titleId = 0;
        if (packet.Opcode != Opcodes.DesignationSelection || packet.Length != 8 || packet.Payload.Length != 4)
        {
            return false;
        }
        titleId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload);
        return true;
    }
}
