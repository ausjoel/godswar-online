namespace Godswar.Server.Application.Warehouse;

/// <summary>
/// B21 (F1, the admin 2026-11-21): nine Storage Boxes on the stock Origin client. The client keeps ONE physical
/// 40-cell warehouse view (its StorageUI holds exactly 160 cell pointers and four tabs, GodsWar.exe
/// 0x62088b-0x6208bf, so more native tabs are impossible without rebuilding the class). Client 1.5.4 shows
/// nine Lua box buttons that send <c>/whpage N</c>; the server then remembers the logical box, projects it
/// into the 40 physical cells and rebases the physical cells of every transfer onto it.
/// </summary>
internal static class WarehousePageCommand
{
    /// <summary>Parses <c>/whpage N</c> (N = 1..9, a box number) out of a chat line, after any "Name:" prefix.</summary>
    public static bool TryParseChatLine(string? line, out int box)
    {
        box = 0;
        var offset = Godswar.Server.Game.DeveloperCommandText.FindBodyOffset(line ?? string.Empty);
        return offset >= 0 && TryParse(line![offset..], out box);
    }

    public static bool TryParse(string? text, out int box)
    {
        box = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 2 &&
            words[0] is "/whpage" or "/whbox" &&
            int.TryParse(words[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out box) &&
            box is >= 1 and <= WarehouseCapacityPolicy.MaximumSupportedBoxCount;
    }

    /// <summary>A physical cell (0-39) of the visible box as its logical warehouse slot; automatic (-1) and
    /// already-logical slots (40+, sent by a four-tab 1.5.3 client, whose page is always 0) pass unchanged.</summary>
    public static int ToLogicalSlot(int physicalSlot, int page) =>
        page > 0 && physicalSlot is >= 0 and < WarehouseCapacityPolicy.SlotsPerBox
            ? checked(page * WarehouseCapacityPolicy.SlotsPerBox + physicalSlot)
            : physicalSlot;

    /// <summary>A logical slot as the physical cell the client shows, or false when it is on another box.
    /// Page 0 keeps the slot as it is (the 1.5.3 behaviour).</summary>
    public static bool TryToVisibleSlot(int logicalSlot, int page, out int physicalSlot)
    {
        physicalSlot = logicalSlot;
        if (page <= 0 || logicalSlot < 0)
        {
            return true;
        }

        var first = page * WarehouseCapacityPolicy.SlotsPerBox;
        if (logicalSlot < first || logicalSlot >= first + WarehouseCapacityPolicy.SlotsPerBox)
        {
            return false;
        }

        physicalSlot = logicalSlot - first;
        return true;
    }

    /// <summary>A transfer from the client with its warehouse slots moved onto the visible box.</summary>
    public static WarehouseTransferIntent ToLogical(WarehouseTransferIntent intent, int page) =>
        page <= 0 || intent.StorageType != WarehouseStorageType.Normal
            ? intent
            : intent with
            {
                WarehouseSlot = ToLogicalSlot(intent.WarehouseSlot, page),
                DestinationWarehouseSlot = ToLogicalSlot(intent.DestinationWarehouseSlot, page)
            };
}
