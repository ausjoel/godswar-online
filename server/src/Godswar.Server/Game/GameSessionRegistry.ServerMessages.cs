using Godswar.Server.Application.Finalise2;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// the admin 2026-11-10 (real-client screenshot): "Messages from the server should be in the top box" and the chat box is
/// "player chat ... with exception of world boss spawns, event transporters now open".
/// <para>
/// Client panes (client 1.4.7 Localization/en_us/UI/XML/ChatUI.xml, Board.xml, Specialmsg.xml; GodsWar.exe 1.4.7):
/// the top box is RecentMessageWin (y 388-457) with the 1.4.3 Board (window 770079, y 364-450) drawn over it; the
/// chat box is HistoryMessageWin (y 505+). The exe writes RecentMessageListBox (CChatUI+0x68) from two chat-line
/// adders, 0x5231f0 (Talk path, caller 0x6dae0f) and 0x524570 (7 callers), both gated only by the mask byte
/// CChatUI+0xf0, which the constructor sets to 0xff (0x5222fa) and nothing in ChatUI changes: the top box mirrors
/// the latest chat lines, and no channel word goes to the top box alone. Lua AddSystemMessage_UTF8 goes to the
/// SystemMessageWin bar at y 170 (CChatUI+0x30/+0x34), not the top box. So the server's only way to write into the
/// top box without also writing into the chat box is the board ('M' lines). World chat frames (0x482) go to the
/// chat box.
/// </para>
/// </summary>
internal static class ServerMessageRouting
{
    /// <summary>The World channel word the operator announcement and event-NPC lines have always used.</summary>
    public const uint WorldChatWord = 0x482;

    /// <summary>board.server_info (default 1): server info goes to the board, not World chat.</summary>
    public static bool ToTopBox => Finalise2Rates.On(Finalise2RateKeys.BoardServerInfo);
}

/// <summary>Board line fitting (Board.lua: WIDTH = 46, 3 message lines).</summary>
internal static class BoardText
{
    public const int Width = 46;
    public const int MaxLines = 3;

    /// <summary>Longest message: the fallback game log line's 126 bytes (the board itself holds 3 x 46).</summary>
    public const int MaxText = PacketBuilder.CenteredAnnouncementMaximumTextLength;

    public static string Clip(string text) => text.Length <= MaxText ? text : text[..(MaxText - 3)] + "...";

    /// <summary>Word-wraps a message into at most 3 lines of at most 46 characters; the last line ends in "..." if cut.</summary>
    public static IReadOnlyList<string> Wrap(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var raw in words)
        {
            var word = raw;
            while (word.Length > Width)
            {
                if (current.Length > 0) { lines.Add(current); current = string.Empty; }
                lines.Add(word[..Width]);
                word = word[Width..];
            }
            if (current.Length == 0) current = word;
            else if (current.Length + 1 + word.Length <= Width) current += " " + word;
            else { lines.Add(current); current = word; }
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count == 0) return [text];
        if (lines.Count <= MaxLines) return lines;
        var last = lines[MaxLines - 1];
        last = last.Length > Width - 3 ? last[..(Width - 3)] + "..." : last + "...";
        return [.. lines.Take(MaxLines - 1), last];
    }
}

internal sealed partial class GameSessionRegistry
{
    /// <summary>One World chat line (chat box) to one session, spoken by the session's own object id.</summary>
    internal static async Task<bool> SendChatLineAsync(GameSessionContext context, string text, string label,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.Session.SendAsync(
                PacketBuilder.ChatMessage(context.ObjectId, ServerMessageRouting.WorldChatWord, text),
                cancellationToken, label);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[chat-line] {label} skipped: {error.GetType().Name}");
            return false;
        }
    }

    /// <summary>One World chat line to every world-ready session that <paramref name="reaches"/> accepts.</summary>
    internal async Task<int> PublishChatLineAsync(string text, string label, CancellationToken cancellationToken,
        Func<GameSessionContext, bool>? reaches = null)
    {
        var delivered = 0;
        foreach (var context in SnapshotWorldReadySessions())
            if ((reaches is null || reaches(context)) &&
                await SendChatLineAsync(context, text, label, cancellationToken)) delivered++;
        return delivered;
    }

    /// <summary>A server info line for one player on the board (top box); clients without the board get a game log line.</summary>
    internal Task<bool> SendServerInfoAsync(ClientSession session, string text, CancellationToken cancellationToken)
    {
        var clean = BoardText.Clip(new string(text.Where(static c => c is >= ' ' and <= '~').ToArray()).Trim());
        return clean.Length == 0 ? Task.FromResult(false) : SendBoardToAsync(session, 'M', clean, cancellationToken);
    }
}
