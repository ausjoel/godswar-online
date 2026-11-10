using System.Buffers.Binary;

using Godswar.Server.Application.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

/// <summary>One reward item a quest's answer offers in a numbered slot.</summary>
/// <remarks>
/// The slot index is the client's own numbering: it is what the hand-in request
/// names and what the acknowledgement echoes back, so a claim recorded against
/// it lines up with the eight slots the player was shown.
/// </remarks>
internal readonly record struct QuestRewardItem(
    int SlotIndex,
    uint ItemId,
    ItemGrantAttributes Attributes);

/// <summary>
/// Reads the reward items out of the area
/// <see cref="PacketBuilder.ResolveQuestRewardArea"/> hands to the client.
/// </summary>
/// <remarks>
/// The area is 72-byte records; the slot's item id is the dword at +8 and an
/// unfilled slot carries <c>0xFFFFFFFF</c> there. Verified against the captured
/// answers: quest 518 offers the newbie gift bag 3876 in slot 0 and quest 519
/// offers the four class weapons 1000/1400/1700/1800 in slots 0-3.
/// <para>
/// This is the server's own view of what it promised, which is what a claim has
/// to be checked against: the item the client later announces on opcode 10056 is
/// only payable when it appears here.
/// </para>
/// </remarks>
internal static class QuestRewardItemCatalog
{
    /// <summary>Slots in one reward area, matching the accept answer.</summary>
    internal const int MaximumSlots = 8;

    /// <summary>Bytes of one reward record.</summary>
    internal const int RecordBytes = 72;

    /// <summary>Where a record carries its item id.</summary>
    private const int RecordItemIdOffset = 8;

    /// <summary>What an unfilled slot carries at that offset.</summary>
    private const uint EmptySlotItemId = uint.MaxValue;

    /// <summary>
    /// Every reward item the quest's answer offers, in slot order, with the slots
    /// an empty template leaves free skipped.
    /// </summary>
    public static IReadOnlyList<QuestRewardItem> Resolve(uint questId) =>
        Read(questId, overridden: true);

    /// <summary>
    /// The items the quest shipped with, ignoring any GM override: what a client
    /// that accepted the quest before the change still holds in its reward menu.
    /// </summary>
    public static IReadOnlyList<QuestRewardItem> ResolveBuiltIn(uint questId) =>
        Read(questId, overridden: false);

    /// <summary>
    /// The quality, grade and added attributes the GM configured for this reward
    /// slot, or the plain item when the slot is the quest's own.
    /// </summary>
    private static ItemGrantAttributes ResolveAttributes(
        uint questId,
        int slotIndex) =>
        QuestRewardContentCatalog.Current.TryGetSlots(questId, out var slots) &&
        slots.FirstOrDefault(slot => slot.SlotIndex == slotIndex) is { } match
            ? match.Attributes
            : default;

    private static IReadOnlyList<QuestRewardItem> Read(
        uint questId,
        bool overridden)
    {
        var items = new List<QuestRewardItem>();
        // A quest's area can differ per objective shape (the captured answers are
        // keyed by it), so both shapes are read and the lower slot wins.
        foreach (var kind in (uint[])[4u, 8u])
        {
            var area = overridden
                ? PacketBuilder.ResolveQuestRewardArea(questId, kind)
                : PacketBuilder.ResolveBaseQuestRewardArea(questId, kind);
            var slots = Math.Min(MaximumSlots, area.Length / RecordBytes);
            for (var slot = 0; slot < slots; slot++)
            {
                var itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                    area.AsSpan(slot * RecordBytes + RecordItemIdOffset, 4));
                if (itemId is 0 or EmptySlotItemId ||
                    items.Any(item => item.ItemId == itemId))
                {
                    continue;
                }

                items.Add(new QuestRewardItem(
                    slot,
                    itemId,
                    overridden ? ResolveAttributes(questId, slot) : default));
            }
        }

        return items;
    }
}
