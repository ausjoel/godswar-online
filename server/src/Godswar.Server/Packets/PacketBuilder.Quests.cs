using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    internal const int QuestSceneOfferBytes = 17;
    internal const int QuestActionBytes = 648;
    internal const int QuestAcceptedBytes = 12;
    internal const int QuestActionPairAckBytes = 48;

    // Every frame below is the reference server's own reply, replayed byte for
    // byte. The client is the same client the capture was taken from and the
    // quest giver's interaction id matches, so nothing is reconstructed.

    /// <summary>S2C 10083, captured: the quest a menu-offering scene advertises.</summary>
    /// <remarks>Replayed verbatim from the reference capture; generated from the
    /// recorded bytes so the frame cannot drift.</remarks>
    private static readonly byte[] QuestSceneOffer = Convert.FromHexString(
        "1100632706020000ef1300000602000001");

    /// <summary>S2C 10082, captured: the full quest answer including its snapshot block.</summary>
    /// <remarks>Replayed verbatim from the reference capture; generated from the
    /// recorded bytes so the frame cannot drift.</remarks>
    private static readonly byte[] QuestActionAck = Convert.FromHexString(
        "88026227e3130000ef1300000602000000000000040000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000240f0000ffffffffffffffffffffffffffffffffffffffff" +
        "0101010100000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffffffffffffffffffff" +
        "ffffffffffffffff010100010000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000ffffffffffffffff" +
        "ffffffffffffffffffffffffffffffff01010001000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "ffffffffffffffffffffffffffffffffffffffffffffffff0101000100000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000ffffffffffffffffffffffffffffffffffffffff" +
        "0101000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000ffffffffffffffff00000000ffffffffffffffffffffffff" +
        "ffffffffffffffff010100000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffff00000000ffffffff" +
        "ffffffffffffffffffffffffffffffff01010000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000ffffffffffffffff" +
        "00000000ffffffffffffffffffffffffffffffffffffffff0101000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "ffffffffffffffff");

    /// <summary>S2C 10092, captured: the paired confirmation reply.</summary>
    /// <remarks>Replayed verbatim from the reference capture; generated from the
    /// recorded bytes so the frame cannot drift.</remarks>
    private static readonly byte[] QuestPairAck = Convert.FromHexString(
        "30006c2700000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000");

    /// <summary>S2C 10084, captured: confirms the quest as accepted.</summary>
    /// <remarks>Replayed verbatim from the reference capture; generated from the
    /// recorded bytes so the frame cannot drift.</remarks>
    private static readonly byte[] QuestAcceptedAck = Convert.FromHexString(
        "0c006427ef13000006020000");

    /// <summary>
    /// S2C 10090, the login-time quest snapshot, taken from the reference
    /// capture verbatim (its packet 104).
    /// </summary>
    /// <remarks>
    /// The reference server published this 2048-byte frame as soon as the
    /// character entered the world, and the stock client needs it to know
    /// which quests it holds. Without it the client routes a quest click
    /// down a different path and never offers the accept prompt.
    /// </remarks>
    private static readonly byte[] LoginQuestSnapshot = Convert.FromHexString(
        "00086a270100000006020000e3130000ef130000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffff0000000000000000" +
        "0000000000000000000000000400000003000000000000000000000000000000" +
        "00000000000000000000000000000000240f0000ffffffffffffffffffffffff" +
        "ffffffffffffffff010101010000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000ffffffff" +
        "ffffffffffffffffffffffffffffffff01010001000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000ffffffffffffffffffffffffffffffffffffffff0101000100000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000ffffffffffffffffffffffffffffffffffffffff" +
        "0101000100000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000ffffffffffffffffffffffff" +
        "ffffffffffffffff010100000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffff00000000ffffffff" +
        "ffffffffffffffffffffffffffffffff01010000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000ffffffffffffffff" +
        "00000000ffffffffffffffffffffffffffffffffffffffff0101000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "ffffffffffffffff00000000ffffffffffffffffffffffffffffffffffffffff" +
        "0101000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000ffffffffffffffffffffffff000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000ffffffff" +
        "ffffffffffffffffffffffffffffffff01010000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000ffffffffffffffff" +
        "00000000ffffffffffffffffffffffffffffffffffffffff0101000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "ffffffffffffffff00000000ffffffffffffffffffffffffffffffffffffffff" +
        "0101000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000ffffffffffffffff00000000ffffffffffffffffffffffff" +
        "ffffffffffffffff010100000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffffffffffff00000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000ffffffffffffffffffffffffffffffffffffffff0101000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "ffffffffffffffff00000000ffffffffffffffffffffffffffffffffffffffff" +
        "0101000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000ffffffffffffffff00000000ffffffffffffffffffffffff" +
        "ffffffffffffffff010100000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffff00000000ffffffff" +
        "ffffffffffffffffffffffffffffffff01010000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000ffffffffffffffff");


    /// <summary>S2C 10083 replayed verbatim from the reference capture.</summary>
    public static byte[] QuestSceneOfferAck() => (byte[])QuestSceneOffer.Clone();

    /// <summary>S2C 10082 replayed verbatim from the reference capture.</summary>
    public static byte[] QuestActionAckFrame() => (byte[])QuestActionAck.Clone();

    /// <summary>S2C 10092 replayed verbatim from the reference capture.</summary>
    public static byte[] QuestActionPairAckFrame() =>
        (byte[])QuestPairAck.Clone();


    /// <summary>S2C 10090 replayed verbatim from the reference capture.</summary>
    public static byte[] LoginQuestSnapshotFrame() =>
        (byte[])LoginQuestSnapshot.Clone();

    /// <summary>S2C 10084 replayed verbatim from the reference capture.</summary>
    public static byte[] QuestAcceptedAckFrame() =>
        (byte[])QuestAcceptedAck.Clone();
}
