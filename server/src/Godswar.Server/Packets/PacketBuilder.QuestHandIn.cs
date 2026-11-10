using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // The hand-in replies, replayed byte for byte from the reference capture.
    // The 10086, 10076, 10077 and 10080 frames are built per quest by the
    // QuestFrames builders - only the trailing 10097 has no per-quest field and
    // is replayed as captured.

    /// <summary>S2C 10097, replayed from the reference capture (packet 1368).</summary>
    private static readonly byte[] QuestHandInTail = Convert.FromHexString(
        "10007127810500004604000083020000");

    /// <summary>S2C 10097 replayed from the reference capture.</summary>
    public static byte[] QuestHandInTailFrame() => (byte[])QuestHandInTail.Clone();
}
