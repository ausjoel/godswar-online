using Godswar.Server.State;

namespace Godswar.Server.Packets;

// W6A (the admin 2026-11-13, "errors that are stopping logging in"): one bad item row must not end a session.
//
// The item-record writers used to throw on an item the client record cannot hold (the live case: a packed
// Seal Jade, item 10109, added without its sealed_pet_items row). The throw happened while the world-entry
// packets were built, so the character could never log in.
//
// Now every item-record writer first asks SendableItem(): an item whose record cannot be built is written as an
// EMPTY slot. The item itself is untouched - it stays in the character's bag in memory and in the database,
// so its slot stays reserved (nothing can be granted into it and nothing is duplicated or lost); the player
// just does not see it until the editor fixes or removes it. ItemFault() is the same test for the login check.
internal static partial class PacketBuilder
{
    private const int ItemFaultScratchLength = 72;
    private static long _unsendableItemRecords;

    /// <summary>How many item records were written as an empty slot since start-up (a counter, for the log).</summary>
    internal static long UnsendableItemRecords => Interlocked.Read(ref _unsendableItemRecords);

    /// <summary>Null when the item can be sent to the client; otherwise why it cannot.</summary>
    internal static string? ItemFault(CompactItemEntry item)
    {
        if (item.IsEmpty)
        {
            return null;
        }
        try
        {
            Span<byte> scratch = stackalloc byte[ItemFaultScratchLength];
            WriteItemExtension(scratch, item);
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    private static CompactItemEntry SendableItem(CompactItemEntry item)
    {
        if (item.IsEmpty || ItemFault(item) is null)
        {
            return item;
        }
        Interlocked.Increment(ref _unsendableItemRecords);
        return default;
    }
}

/// <summary>One item of a character that cannot be sent to the client (W6A login check and login record).</summary>
internal sealed record UnsendableItem(string Container, int Slot, uint ItemId, string Reason);

internal static partial class PacketBuilder
{
    /// <summary>Every bag and equipment item of the character that would be hidden from the client, and why.</summary>
    internal static IReadOnlyList<UnsendableItem> UnsendableItems(GameCharacter character)
    {
        List<UnsendableItem>? found = null;
        Scan("bag", KitBagItems(character));
        Scan("equipment", ParseEquipment(character));
        return found ?? (IReadOnlyList<UnsendableItem>)[];

        void Scan(string container, CompactItemEntry[] items)
        {
            for (var slot = 0; slot < items.Length; slot++)
            {
                if (ItemFault(items[slot]) is { } reason)
                {
                    (found ??= []).Add(new(container, slot, items[slot].Id, reason));
                }
            }
        }
    }

    /// <summary>
    /// The owned-pet list for world entry. When the strict list cannot be built, the pets that cannot be sent are
    /// left out (they stay in the database and in the session); if even that fails, an empty list is sent.
    /// <paramref name="hidden"/> says what was left out and why; empty when nothing was.
    /// </summary>
    internal static byte[] OwnedPetListForEntry(
        Godswar.Server.Application.Pets.IPetContentCatalog petContent,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        int openedCellCount,
        out IReadOnlyList<string> hidden)
    {
        try
        {
            hidden = [];
            return OwnedPetList(petContent, pets, openedCellCount);
        }
        catch (Exception listError)
        {
            var notes = new List<string>();
            var sendable = new List<PetBootstrapSnapshot>(pets.Count);
            var seen = new HashSet<long>();
            foreach (var pet in pets)
            {
                try
                {
                    if (!seen.Add(pet.PetId))
                    {
                        throw new InvalidDataException($"Owned-pet bootstrap contains duplicate pet ID {pet.PetId}.");
                    }
                    _ = OwnedPetList(
                        petContent,
                        [pet],
                        petContent.Settings.MaximumOwnedPetCount);
                    sendable.Add(pet);
                }
                catch (Exception petError)
                {
                    notes.Add($"pet {pet.PetId}: {petError.Message}");
                }
            }
            try
            {
                if (notes.Count == 0)
                {
                    notes.Add("pet list: " + listError.Message);
                    hidden = notes;
                    return OwnedPetList(
                        petContent,
                        [],
                        Godswar.Server.Application.Pets.PetShedCapacityPolicy.DefaultOpenedCellCount);
                }
                hidden = notes;
                return OwnedPetList(
                    petContent,
                    sendable,
                    Math.Clamp(
                        Math.Max(openedCellCount, sendable.Count),
                        Godswar.Server.Application.Pets.PetShedCapacityPolicy.DefaultOpenedCellCount,
                        petContent.Settings.MaximumOwnedPetCount));
            }
            catch (Exception secondError)
            {
                notes.Add("pet list: " + secondError.Message);
                hidden = notes;
                return OwnedPetList(
                    petContent,
                    [],
                    Godswar.Server.Application.Pets.PetShedCapacityPolicy.DefaultOpenedCellCount);
            }
        }
    }
}
