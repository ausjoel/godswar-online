using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Events;

namespace Godswar.Server.Packets;

// EVENTSBOARD: message 10204, one entry of the Event window per frame.
// Layout read from Origin.exe 2.51 (Ghidra export GodsWar-Native/decomp/out):
//  - handler 0x4e3532 copies a fixed 0x762 (1,890) byte body from [ebx+8] (= wire offset 4) into the board
//    object at +0x98 (0x617b50), then raises Lua event 102 (UpDate_EventInfo) when game state is 4 (in world);
//  - the client's own GM upload (0x6946xx) frames the same body as size 0x766, type 0x27dc;
//  - GameAPI:GetEventInfo (0x694870) returns the fields in this order from these body offsets:
//      +0 Flags u8, +1 Type u8, +2 Time[32], +34 Name[64], +98 Link[128], +226 Content[1152],
//      +1378 AutoName1[64], +1442 AutoAddr1[32], +1474 AutoName2[64], +1538 AutoAddr2[32],
//      +1570 AutoName3[64], +1634 AutoAddr3[32], +1666 AutoName4[64], +1730 AutoAddr4[32],
//      +1762 DayTimeID[32], +1794 DayTime1[32], +1826 DayTime2[32], +1858 DayTime3[32]  (= 1,890).
// Flags (EventProc.lua / GMEventEditProc.lua): 1 = first of a list (clears it), 0 = middle, 2 = last, 3 = only.
// Type: 1 today, 2 notices, 3 this week, 4 growth guide. An empty Name is dropped by the Lua.
// DayTimeID is always left empty: for a Type 3 entry the client (0x617bb0) would copy it into the Today list
// on matching weekdays by its own clock and clear that list; the server builds Today itself instead.
// Strings are char* for lua_pushstring: ASCII only here, NUL-terminated, cut to fit.
internal static partial class PacketBuilder
{
    public const ushort EventBoardOpcode = 10204;
    public const int EventBoardBodyLength = 1890;
    public const int EventBoardFrameLength = 4 + EventBoardBodyLength;

    public const byte EventBoardFlagMiddle = 0;
    public const byte EventBoardFlagFirst = 1;
    public const byte EventBoardFlagLast = 2;
    public const byte EventBoardFlagOnly = 3;

    internal const int EventBoardTimeOffset = 2, EventBoardTimeLength = 32;
    internal const int EventBoardNameOffset = 34, EventBoardNameLength = 64;
    internal const int EventBoardLinkOffset = 98, EventBoardLinkLength = 128;
    internal const int EventBoardContentOffset = 226, EventBoardContentLength = 1152;
    internal const int EventBoardAutoName1Offset = 1378, EventBoardAutoNameLength = 64;
    internal const int EventBoardAutoAddr1Offset = 1442, EventBoardAutoAddrLength = 32;
    internal const int EventBoardDayTimeIdOffset = 1762;

    /// <summary>One 10204 frame. Text is made ASCII and cut to its field (always NUL-terminated).</summary>
    public static byte[] EventBoardEntryFrame(byte flags, EventBoardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (flags > EventBoardFlagOnly) throw new ArgumentOutOfRangeException(nameof(flags));
        if (entry.List is < EventBoardList.Today or > EventBoardList.Guide) throw new ArgumentOutOfRangeException(nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Name)) throw new ArgumentException("The client drops an entry with no name.", nameof(entry));

        var packet = new byte[EventBoardFrameLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), EventBoardOpcode);
        var body = packet.AsSpan(4);
        body[0] = flags;
        body[1] = (byte)entry.List;
        WriteBoardText(body, EventBoardTimeOffset, EventBoardTimeLength, entry.Time);
        WriteBoardText(body, EventBoardNameOffset, EventBoardNameLength, entry.Name);
        WriteBoardText(body, EventBoardLinkOffset, EventBoardLinkLength, entry.Link);
        WriteBoardText(body, EventBoardContentOffset, EventBoardContentLength, entry.Content);
        WriteBoardText(body, EventBoardAutoName1Offset, EventBoardAutoNameLength, entry.AutoName);
        WriteBoardText(body, EventBoardAutoAddr1Offset, EventBoardAutoAddrLength, entry.AutoAddress);
        return packet;
    }

    /// <summary>
    /// Every entry as frames, each list flagged first..last so the client clears it before refilling. A list
    /// with no entries is not sent (the client keeps what it had; the board always carries the time note).
    /// </summary>
    public static IReadOnlyList<byte[]> EventBoardFrames(IReadOnlyList<EventBoardEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var frames = new List<byte[]>(entries.Count);
        foreach (var list in entries.GroupBy(static e => e.List))
        {
            var items = list.ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                var flags = items.Length == 1 ? EventBoardFlagOnly
                    : i == 0 ? EventBoardFlagFirst
                    : i == items.Length - 1 ? EventBoardFlagLast
                    : EventBoardFlagMiddle;
                frames.Add(EventBoardEntryFrame(flags, items[i]));
            }
        }

        return frames;
    }

    private static void WriteBoardText(Span<byte> body, int offset, int length, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ascii = new StringBuilder(Math.Min(text.Length, length));
        foreach (var c in text)
        {
            if (ascii.Length == length - 1) break;
            ascii.Append(c == '\n' || c >= ' ' && c < 127 ? c : '?');
        }

        Encoding.ASCII.GetBytes(ascii.ToString(), body.Slice(offset, length - 1));
    }
}
