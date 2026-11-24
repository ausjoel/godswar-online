using System.Collections.Concurrent;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

/// <summary>
/// W20 (npc-form-notice, found live on 22 Nov by B27-C): the board above the chat box is fed by 10070 frames on window
/// 770079 (GameSessionRegistry.Board.cs). The exe keeps ONE NPC-function context, the npc id and dialog index of the
/// last 10070 it received, and builds every 10069 from it; Board.lua only stops the repaint. So a board line that
/// arrived while an NPC form was open made the form's OK go out as npc=770079 dialog=770079, which no NPC owns, and the
/// server ignored it (live log: "[npc] function action ignored: npc=770079 dialog=770079 subId=0").
/// Fix: while a session has had NPC traffic in the last <see cref="NpcFormHold"/>, its board lines are held (latest
/// online line, last few messages, each stamped when it happened) and sent by the 15 s notice tick once the session
/// has been quiet at NPCs for that long. The 10038 fallback for clients without the board never touched the NPC
/// context and is still sent at once. The server cannot see a form close (the exe sends nothing), hence the idle window.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    internal static readonly TimeSpan NpcFormHold = TimeSpan.FromSeconds(120);
    private const int HeldBoardMessageLimit = 3; // the board shows 3 message lines

    private readonly ConcurrentDictionary<ClientSession, DateTimeOffset> _npcFormSeen = new();
    private readonly ConcurrentDictionary<ClientSession, HeldBoardLines> _boardHeld = new();

    private sealed class HeldBoardLines
    {
        public bool Taken;
        public string? Online;
        public readonly List<string> Messages = [];
    }

    /// <summary>The client sent 10067 / 10068 / a real 10069: an NPC window is (or was just) open.</summary>
    internal void NoteNpcFormActivity(ClientSession session) => NoteNpcFormActivity(session, DateTimeOffset.UtcNow);

    internal void NoteNpcFormActivity(ClientSession session, DateTimeOffset at) => _npcFormSeen[session] = at;

    private bool NpcFormMayBeOpen(ClientSession session, DateTimeOffset now) =>
        _npcFormSeen.TryGetValue(session, out var seen) && now - seen < NpcFormHold;

    private bool TryHoldBoardLine(ClientSession session, char kind, string stamped, DateTimeOffset now)
    {
        if (!NpcFormMayBeOpen(session, now)) return false;
        while (true)
        {
            var held = _boardHeld.GetOrAdd(session, static _ => new HeldBoardLines());
            lock (held)
            {
                if (held.Taken) continue; // a flush just took this one: hold in a fresh entry
                Hold(held, kind, stamped);
            }
            break;
        }
        Console.WriteLine($"[board] held kind={kind}: NPC form may be open (W20)");
        return true;
    }

    private static void Hold(HeldBoardLines held, char kind, string stamped)
    {
        if (kind == 'O')
        {
            held.Online = stamped; // the online line replaces itself on the board anyway
            return;
        }
        held.Messages.Add(stamped);
        if (held.Messages.Count > HeldBoardMessageLimit) held.Messages.RemoveAt(0);
    }

    /// <summary>Run by the notice tick: sends held board lines to sessions that have been quiet at NPCs long enough.</summary>
    internal async Task<int> FlushHeldBoardAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var gone in _npcFormSeen.Keys.Where(static s => s.IsDisconnected).ToArray())
            _npcFormSeen.TryRemove(gone, out _);
        var flushed = 0;
        foreach (var (session, held) in _boardHeld.ToArray())
        {
            if (session.IsDisconnected)
            {
                _boardHeld.TryRemove(session, out _);
                continue;
            }
            if (NpcFormMayBeOpen(session, now) || !_boardHeld.TryRemove(session, out _)) continue;
            string? online;
            string[] messages;
            lock (held)
            {
                held.Taken = true;
                online = held.Online;
                messages = held.Messages.ToArray();
            }
            try
            {
                if (online is not null) await SendBoardFramesAsync(session, 'O', online, cancellationToken);
                foreach (var message in messages) await SendBoardFramesAsync(session, 'M', message, cancellationToken);
                flushed++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[board] held delivery skipped: {error.GetType().Name}");
            }
        }
        if (flushed > 0) Console.WriteLine($"[board] held lines delivered sessions={flushed} (W20)");
        return flushed;
    }
}
