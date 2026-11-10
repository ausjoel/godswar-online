using System.Buffers.Binary;

namespace Godswar.Server.State;

/// <summary>
/// ITEMS-HERMES (F-55): the pet details Rename button and the Renaming Stone (4112 "PetName", "Use it to rename your
/// pet.", Overlap 1, no Use flag: the rename spends it, a right-click does nothing).
///
/// Protocol, from the 2013 client (GodsWar.exe 1.4.4, static analysis):
/// - The Rename button (PetDetailUI id 830003, handler 0x5b6aac -> 0x5b6b50) opens PetChangeName.xml (edit box
///   MaxText=8). Its OK path 0x5b6180 rejects an empty name (Message.dat PetNameNULL) and more than 16 wide chars
///   (PetNameOverflow), converts to the ANSI code page and calls 0x69c680.
/// - 0x69c680 refuses a resend within a second (PetOperatorFrequent), copies the name to the pet bean's pending
///   name (+0x20) and sends C2S 10242 (0x2802), 40 bytes: [u16 0x28][u16 0x2802][u32 pet id][char name[32]]
///   (strncpy_s, NUL-terminated).
/// - The reply is the shared pet-operation result 10244 (0x2804) [u32 pet id][u8 code]. Its handler 0x69cbf0
///   jumps through 0x69cc64: code 9 -> 0x69cf00 copies the pending name over the bean's name (+0x4), sets the bean's
///   +0x47 flag, renames the summoned pet's label (+0x640), prints PetChangeNameInfo "You are called [%s] from now
///   on." in chat AND opens the generic message box with it. Even codes go to 0x69d130, which prints
///   Message.dat PetOperator&lt;code&gt; in chat only: 8 "Failure to rename! The pet has already been renamed!",
///   12 "Failure to rename! Special characters are not allowed.", 13 "Failure to rename! The renaming item is locked."
/// - The client never checks the Renaming Stone itself; its dialog always says stones are needed after the first
///   rename (text.lua PETCN_X0_1), and the Rookie Guide says "There is only one chance to make a name change free".
///   So: first rename of a pet free, later ones spend one Renaming Stone (rule from the client text).
/// - F-156 (the admin): no code 9 is sent (no box after the stone is spent). The 10237 owned-pet list re-parses every
///   record through 0x6a6340, which assigns record +4 over the bean name (+0x4) with no message, so the owner's
///   panel shows the new name at once when no pet is summoned. With a pet summoned, 10237's handler 0x69c950 first
///   calls 0x69c3c0, the client's own Recall request, so nothing is sent then and the owner sees the name after a
///   relog. The add-pet handler 0x69d250 skips a pet id it already has, so it cannot rename. The rename dialog
///   closes itself once the request is sent (0x5b62ef), it does not wait for a reply.
/// - Names (F-156): only the swear words and slurs of <see cref="NameFilter"/> are refused; no GM/Admin entries.
/// </summary>
internal static class PetRenamePolicy
{
    public const int RenamingStone = 4112;
    public const int RequestLength = 40;
    public const int MaximumNameBytes = 16; // the client's own overflow check (16 wide chars), names are ASCII here

    public static bool TryParseRequest(ReadOnlySpan<byte> frame, out uint petId, out byte[] rawName)
    {
        petId = 0;
        rawName = [];
        if (frame.Length < RequestLength)
        {
            return false;
        }

        petId = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(4, 4));
        var field = frame.Slice(8, 32);
        var end = field.IndexOf((byte)0);
        rawName = field[..(end < 0 ? field.Length : end)].ToArray();
        return petId != 0;
    }

    /// <summary>1-16 ASCII letters or digits and no name-filter word; else the client's code 12 text applies.</summary>
    public static bool TryValidateName(byte[] rawName, out string name)
    {
        name = "";
        if (rawName.Length is 0 or > MaximumNameBytes ||
            rawName.Any(static b => !(b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or
                >= (byte)'a' and <= (byte)'z')))
        {
            return false;
        }

        var candidate = System.Text.Encoding.ASCII.GetString(rawName);
        if (NameFilter.Blocks(candidate)) // F-156
        {
            return false;
        }

        name = candidate;
        return true;
    }
}
