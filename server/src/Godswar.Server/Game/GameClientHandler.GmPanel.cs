using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Operators;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

// Native GM window (client UI/XML/GMPanel.xml). The only way a client script can
// reach the network is NPCFUN:NpcFun_SendMsg (Origin.exe 0x5a9d50): one 10069
// frame with one number at payload offset 4 and stale stack bytes everywhere
// else. Command text therefore arrives three bytes per frame. It then takes the
// same path as a typed command, so every GM authority check still applies.
//
// finalise-gm (2026-11-05): a version-2 window (client GMPanel.lua v2) marks its
// run frame with version 2 in bits 16-23 (0x48020000). Replies to such a command are not
// written to chat: they go back as a GM text message (window 770078) that the
// window shows in its status line or its result window. An older window, or a
// typed command, still gets chat lines exactly as before.
internal sealed partial class GameClientHandler
{
    internal const int GmPanelWindowId = 770077;
    internal const int GmPanelTextWindowId = 770078;
    private const byte GmPanelAppendTag = 0x47;
    private const byte GmPanelRunTag = 0x48;
    private const int GmPanelMaxTextBytes = 240;
    private const int GmPanelReplyVersion = 2;
    private const int GmPanelTextValuesPerFrame = 19;   // 20 entries, the first is the control word
    private const int GmPanelTextMaxFrames = 40;        // 40 x 19 x 3 = 2,280 characters

    private readonly List<byte> _gmPanelText = [];
    private bool _gmPanelTextOverflowed;
    private List<string>? _gmPanelReplyBuffer;

    // What the client was last told by 10999 (payload byte 0 walls, byte 1 speed on foot).
    // A fresh client starts with both off, which is also what an unsent state means.
    private bool _gmWallWalk;
    private byte _gmClientFlagsSent;

    private static bool IsGmPanelFrame(GamePacket packet)
    {
        var payload = packet.Payload;
        return payload.Length >= 8 && payload[7] is GmPanelAppendTag or GmPanelRunTag or InfoTabsDeclareTag; // FINALISE2: 0x49 = info tabs
    }

    private async Task HandleGmPanelFrameAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(4, sizeof(uint)));
        if (value >> 24 == InfoTabsDeclareTag) // FINALISE2 (F-63): client 1.4.2 understands "#n#" info-tab prefixes
        {
            HandleInfoTabsDeclaration(value);
            if (((value >> 16) & 0xFF) >= 2 && _character is not null) // 1.4.3: the board above the chat box
            {
                var first = !_registry.IsBoardCapable(_session);
                _registry.MarkBoardCapable(_session);
                if (first)
                    await _registry.SendBoardToAsync(_session, 'O', _registry.BuildPopulationText(), cancellationToken);
            }
            return;
        }

        if (value >> 24 == GmPanelAppendTag)
        {
            AppendGmPanelText(value);
            return;
        }

        // The version rides in bits 16-23, like the append tag's character: a value with more than 24
        // significant bits is rounded by the client's single-precision FPU (0x48000002 arrives as 0x48000000).
        var version = (int)((value >> 16) & 0xFF);
        var overflowed = _gmPanelTextOverflowed;
        var text = Encoding.UTF8.GetString(_gmPanelText.ToArray());
        _gmPanelText.Clear();
        _gmPanelTextOverflowed = false;
        if (overflowed || text.Length == 0 || text[0] != '/' ||
            _account is null || _character is null)
        {
            Console.WriteLine(
                $"[gm-panel] ignored account={_account?.Id ?? 0} overflow={overflowed} length={text.Length}");
            return;
        }

        var command = new GamePacket(
            PacketBuilder.GmPanelCommandFrame(_character.Id, text));
        var toWindow = version >= GmPanelReplyVersion;
        if (toWindow)
        {
            _gmPanelReplyBuffer = [];
        }

        try
        {
            if (!await HandleDeveloperItemCommandAsync(command, cancellationToken) &&
                await HasOperatorAuthorityAsync(cancellationToken))
            {
                // Never fall through to chat: this text was not typed to anyone.
                await SendDeveloperItemFeedbackAsync(
                    command,
                    "[gm] unknown command, see /commands",
                    cancellationToken);
            }
        }
        finally
        {
            var replies = _gmPanelReplyBuffer;
            _gmPanelReplyBuffer = null;
            if (toWindow && replies is not null && replies.Count > 0)
            {
                // A refused (non-GM) command buffers nothing and so sends nothing, as before.
                await SendGmPanelTextAsync(string.Join('\n', replies.Select(StripReplyTag)), cancellationToken);
            }
        }
    }

    /// <summary>"[speed] god=off ..." -> "god=off ...": the window already knows which button was pressed.</summary>
    internal static string StripReplyTag(string line)
    {
        if (line.Length > 2 && line[0] == '[')
        {
            var close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0 && close <= 16)
            {
                return line[(close + 2)..];
            }
        }

        return line;
    }

    /// <summary>
    /// Sends text to the v2 GM window. Each 10070 frame (window 770078) carries 20 entries: a control word
    /// ((sequence * 32 + values in the frame) * 2, +1 on the last frame) and up to 19 values of three 7-bit characters each
    /// (c1 * 65536 + c2 * 256 + c3, trailing zeros on the last value). Every value stays below 2^23: the
    /// client hands them to Lua as decimal text and Direct3D keeps its FPU in single precision. Zero
    /// entries are skipped by the client, so a value never starts with a zero character.
    /// </summary>
    private async Task SendGmPanelTextAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var frame in BuildGmPanelTextFrames(text))
        {
            await _session.SendAsync(frame, cancellationToken, "GmPanelText");
        }
    }

    internal static IReadOnlyList<byte[]> BuildGmPanelTextFrames(string text, int windowId = GmPanelTextWindowId)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var ch in text.Replace("\r", string.Empty))
        {
            bytes.Add(ch == '\n' ? (byte)'\n' : ch is >= ' ' and <= '~' ? (byte)ch : (byte)'?');
        }

        if (bytes.Count == 0)
        {
            bytes.Add((byte)' ');
        }

        var maxBytes = GmPanelTextMaxFrames * GmPanelTextValuesPerFrame * 3;
        if (bytes.Count > maxBytes)
        {
            var cut = Encoding.ASCII.GetBytes("\n(cut: too long for the window)");
            bytes.RemoveRange(maxBytes - cut.Length, bytes.Count - (maxBytes - cut.Length));
            bytes.AddRange(cut);
        }

        var values = new List<int>((bytes.Count + 2) / 3);
        for (var i = 0; i < bytes.Count; i += 3)
        {
            var c1 = bytes[i];
            var c2 = i + 1 < bytes.Count ? bytes[i + 1] : (byte)0;
            var c3 = i + 2 < bytes.Count ? bytes[i + 2] : (byte)0;
            values.Add((c1 << 16) | (c2 << 8) | c3);
        }

        var frames = new List<byte[]>();
        var frameCount = (values.Count + GmPanelTextValuesPerFrame - 1) / GmPanelTextValuesPerFrame;
        for (var f = 0; f < frameCount; f++)
        {
            var chunk = values.Skip(f * GmPanelTextValuesPerFrame).Take(GmPanelTextValuesPerFrame).ToArray();
            var control = ((((f + 1) * 32) + chunk.Length) * 2) + (f == frameCount - 1 ? 1 : 0);
            frames.Add(PacketBuilder.CapturedNpcFunctionActionResponse(
                (uint)windowId,
                windowId,
                [control, .. chunk]));
        }

        return frames;
    }

    private void AppendGmPanelText(uint value)
    {
        for (var shift = 16; shift >= 0; shift -= 8)
        {
            var next = (byte)(value >> shift);
            if (next == 0)
            {
                return;
            }

            if (_gmPanelText.Count >= GmPanelMaxTextBytes)
            {
                _gmPanelTextOverflowed = true;
                return;
            }

            _gmPanelText.Add(next);
        }
    }

    private async Task<string> OperatorOpenPanelAsync(CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                GmPanelWindowId,
                GmPanelWindowId,
                GmPanelWindowId),
            cancellationToken,
            "GmPanelOpen");
        return "window sent (a client without the GM window shows nothing)";
    }

    /// <summary>
    /// Tells a patched client (Q20-Client/patch_gm_walls.py + finalise gmclient/patch_gm_speed.py) the two GM
    /// movement switches: byte 0 = walk through walls, byte 1 = let /speed act on foot. Sent only when the
    /// state differs from what this connection last sent (both start off, as the client does), so ordinary
    /// players never receive it. An unpatched client ignores message 10999.
    /// </summary>
    private async Task SyncGmClientFlagsAsync(bool force, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var speedOnFoot = OperatorControls.PowersOf(_character.Id).SpeedFactor != 1f;
        var flags = (byte)((_gmWallWalk ? 1 : 0) | (speedOnFoot ? 2 : 0));
        if (!force && flags == _gmClientFlagsSent)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.GmWallWalk(_gmWallWalk, speedOnFoot),
            cancellationToken,
            "GmWallWalk",
            framed: false);
        _gmClientFlagsSent = flags;
    }
}
