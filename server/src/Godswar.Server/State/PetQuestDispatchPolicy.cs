using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.State;

/// <summary>
/// W3 Magic Bell (F-136): the rules of the Quest Log's "Send Pet" button, kept free of I/O so they can be checked.
/// Client side (GodsWar.exe 1.4.4): the button runs 0x69d800 (pet summoned, Quest Dispatch talent bit 2, quest in the
/// log, not finished, not already dispatched, quest sort = Pet_Quest.xml QuestSort 5, Magic Bell in the bag), then sends
/// C2S 10254 [8][0x280e][u32 quest]. The answer is S2C 10259 [u32 quest @4][u8 code @8] (handler 0x69ec50).
/// </summary>
internal static class PetQuestDispatchPolicy
{
    /// <summary>Magic Bell, Pet_Quest.xml Cost="4113" Type="3" (an item) for every quest level 1-140.</summary>
    public const int MagicBellItemId = 4113;

    /// <summary>Pet_Quest.xml lists quest levels 1-140.</summary>
    public const int MaximumQuestLevel = 140;

    /// <summary>Highest quest id the client's quest table indexes (0x4bade0: id &lt;= 2000, else a null read).</summary>
    public const uint MaximumClientQuestId = 2000;

    /// <summary>PetDisQuestReturn1..7 in Text/Message.dat; the client prints PetDisQuestReturn&lt;code&gt;.</summary>
    public enum Result : byte
    {
        Failed = 1,          // "Failure to assign the quest."
        AlreadyCompleted = 2, // "That quest has already been completed."
        NotDispatchable = 3, // "You cannot send a pet to do that quest."
        NoMoney = 4,         // "You don't have enough money to send a pet."
        Accepted = 5,        // "%s has accepted the %s quest and has set off."
        NoBell = 6,          // "The dispatch quest has failed. A dispatch item is needed."
        AlreadyDispatched = 7 // "This quest has been dispatched."
    }

    /// <summary>
    /// Minutes a dispatch takes (GENERATED formula from the admin's F-136 note): linear in the pet's rank, from the slowest
    /// time at rank 0 down to the fastest at the fastest rank (defaults 70 min at rank 0, 10 min at rank 75 and above),
    /// rounded up to whole minutes.
    /// </summary>
    public static int DispatchMinutes(decimal rank, int slowestMinutes, int fastestMinutes, int fastestRank)
    {
        var slow = Math.Max(1, slowestMinutes);
        var fast = Math.Clamp(fastestMinutes, 1, slow);
        var top = Math.Max(1, fastestRank);
        var share = Math.Clamp(rank, 0m, top) / top;
        return (int)Math.Ceiling(slow - ((slow - fast) * share));
    }

    /// <summary>The same with the editor's hot values (pet.dispatch_* rates).</summary>
    public static int DispatchMinutes(decimal rank) =>
        DispatchMinutes(
            rank,
            Finalise2Rates.Amount(Finalise2RateKeys.MagicBellSlowestMinutes),
            Finalise2Rates.Amount(Finalise2RateKeys.MagicBellFastestMinutes),
            Finalise2Rates.Amount(Finalise2RateKeys.MagicBellFastestRank));

    /// <summary>
    /// A quest a pet may take: the client's list is the quests with Quest.xml pet="5" (Pet_Quest.xml QuestSort 5),
    /// which is exactly the 160 quests with UIQuestSort 3 (daily), with a quest level Pet_Quest.xml prices (1-140).
    /// </summary>
    public static bool IsDispatchable(StarterQuestChain.Step step) =>
        step.UIQuestSort == StarterQuestChain.SortDaily &&
        !step.IsMainLine &&
        step.QuestId is > 0 and <= MaximumClientQuestId &&
        step.MinLevel is >= 1 and <= MaximumQuestLevel;

    /// <summary>S2C 10259: [u16 9][u16 10259][u32 quest][u8 code], the shape of its sibling 10244 (same +4/+8 reads).</summary>
    public static byte[] ResultPacket(uint questId, Result result)
    {
        var packet = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 9);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Opcodes.PetQuestDispatchResult);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), questId);
        packet[8] = (byte)result;
        return packet;
    }

    /// <summary>C2S 10254 carries the quest id right after the header (payload +0).</summary>
    public static bool TryReadRequest(ReadOnlySpan<byte> payload, out uint questId)
    {
        questId = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
        return questId != 0;
    }
}
