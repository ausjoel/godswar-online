using Godswar.Server.Packets;

namespace Godswar.QuestProbe;

/// <summary>
/// The installed client's stream cipher: a rolling XOR over two 256-byte tables
/// whose pointer advances one byte per transformed byte and therefore carries
/// across the whole connection, per direction.
/// </summary>
/// <remarks>
/// The same tables the capture proxy and this server use
/// (<c>ReferencePackets.HashOne</c>/<c>HashTwo</c>), so a probe that keeps one
/// instance per direction speaks the wire format exactly.
/// </remarks>
internal sealed class PacketCipher
{
    private static readonly byte[] HashOne = ReferencePackets.HashOne.ToArray();
    private static readonly byte[] HashTwo = ReferencePackets.HashTwo.ToArray();

    private int _pointer;

    public void Transform(Span<byte> packet)
    {
        for (var i = 0; i < packet.Length; i++)
        {
            packet[i] = (byte)((packet[i] ^ HashOne[_pointer]) ^ HashTwo[_pointer]);
            _pointer = (_pointer + 1) & 0xff;
        }
    }
}
