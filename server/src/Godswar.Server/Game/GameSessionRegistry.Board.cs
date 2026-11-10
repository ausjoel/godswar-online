using System.Collections.Concurrent;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// the admin 2026-11-08: "use this area [above the chat box] for displaying the online players from each faction ... toggled in
/// the online editor what system messages can be displayed and what rate + instant posting from the editor".
/// Client 1.4.3 has a small Board window there (Board.xml / Board.lua) fed by GM-style text frames on window 770079:
/// text "O..." replaces the online line, "M..." adds a message line. A client says it has the board by declaring info
/// tabs with version 2 (SrvMsg.lua 0x49020000). Older clients get the same text as a personal info line instead.
/// What goes there: the population line (editor Events &amp; notices: population_minutes), scheduled notices with
/// delivery "board", instant posts (POST /admin/board/post from the editor), and, behind hot switches, GM
/// announcements and the lottery hints.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    internal const int BoardWindowId = 770079;
    private readonly ConcurrentDictionary<ClientSession, byte> _boardSessions = new();

    private readonly ConcurrentDictionary<ClientSession, byte> _boardGreeted = new();

    private readonly ConcurrentDictionary<ClientSession, byte> _boardWelcomePending = new(); // the admin 2026-11-10

    internal void MarkBoardCapable(ClientSession session) => _boardSessions[session] = 0;

    /// <summary>
    /// Run by the notice ticker (every 15 s): one "Online: ..." personal line for each session that entered the world
    /// since the last tick (board.login_line). Sent outside the login and map-change packet sequences, and being a
    /// plain personal note it prompts client 1.4.2+ to declare its info tabs (1.4.3: the board then fills).
    /// the admin 2026-11-10 ("remove server Welcome or add server messaging customisation in the editor"): the line no longer
    /// starts with "Welcome."; the editor's welcome text (server_messages 'welcome', off by default) goes to the board
    /// one tick later, once the client has had the chance to declare the board.
    /// </summary>
    internal async Task GreetNewBoardSessionsAsync(CancellationToken cancellationToken)
    {
        foreach (var gone in _boardGreeted.Keys.Where(static s => s.IsDisconnected).ToArray())
        {
            _boardGreeted.TryRemove(gone, out _);
            _boardSessions.TryRemove(gone, out _);
            _boardWelcomePending.TryRemove(gone, out _);
        }
        var welcome = Godswar.Server.Application.Npc.NpcContentOverrides.Current.Events.WelcomeText;
        foreach (var pending in _boardWelcomePending.Keys.ToArray())
        {
            _boardWelcomePending.TryRemove(pending, out _);
            if (welcome is not null && !pending.IsDisconnected)
                await SendBoardToAsync(pending, 'M', welcome, cancellationToken);
        }
        var sessions = SnapshotWorldReadySessions();
        var fresh = sessions.Where(s => _boardGreeted.TryAdd(s.Session, 0)).ToArray();
        foreach (var context in fresh) _boardWelcomePending[context.Session] = 0;
        if (fresh.Length == 0 || !Finalise2Rates.On(Finalise2RateKeys.BoardLoginLine)) return;
        var text = PopulationNotice.Build(sessions.Select(s => (s.CharacterId, s.Character.Camp)));
        byte[] packet;
        try { packet = PacketBuilder.PersonalGameLog(text); }
        catch (ArgumentException) { return; }
        foreach (var context in fresh)
        {
            try { await context.Session.SendAsync(packet, cancellationToken, "BoardWelcome"); }
            catch (Exception error) when (error is not OperationCanceledException)
            { Console.WriteLine($"[board] welcome skipped: {error.GetType().Name}"); }
        }
    }

    internal bool IsBoardCapable(ClientSession session)
    {
        if (!_boardSessions.ContainsKey(session)) return false;
        if (!session.IsDisconnected) return true;
        _boardSessions.TryRemove(session, out _);
        return false;
    }

    internal string BuildPopulationText() =>
        PopulationNotice.Build(SnapshotWorldReadySessions().Select(s => (s.CharacterId, s.Character.Camp)));

    /// <summary>Sends one board text to one session: board frames for a 1.4.3 client, else a personal info line.</summary>
    internal async Task<bool> SendBoardToAsync(ClientSession session, char kind, string text, CancellationToken cancellationToken)
    {
        try
        {
            if (IsBoardCapable(session))
            {
                // Board.lua cuts a line at 46 characters, so a message wraps over up to 3 board lines instead.
                var lines = kind == 'M' ? BoardText.Wrap(text) : [text];
                foreach (var line in lines)
                foreach (var frame in GameClientHandler.BuildGmPanelTextFrames(kind + line, BoardWindowId))
                    await session.SendAsync(frame, cancellationToken, "Board");
            }
            else
            {
                await session.SendAsync(PacketBuilder.PersonalGameLog(BoardText.Clip(text)), cancellationToken, "BoardFallback");
            }
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[board] delivery skipped: {error.GetType().Name}");
            return false;
        }
    }

    /// <summary>A message line on everyone's board (instant editor post, board notice, mirrored announcement).</summary>
    internal async Task<int> PublishBoardMessageAsync(string text, CancellationToken cancellationToken)
    {
        var clean = BoardText.Clip(new string(text.Where(static c => c is >= ' ' and <= '~').ToArray()).Trim());
        if (clean.Length == 0) return 0;
        var delivered = 0;
        foreach (var context in SnapshotWorldReadySessions())
            if (await SendBoardToAsync(context.Session, 'M', clean, cancellationToken)) delivered++;
        Console.WriteLine($"[board] message delivered={delivered}: {clean}");
        return delivered;
    }

    /// <summary>Mirrors a system announcement onto the board when its switch is on.</summary>
    internal Task MirrorToBoardAsync(string rateKey, string text, CancellationToken cancellationToken) =>
        Finalise2Rates.On(rateKey) ? PublishBoardMessageAsync(text, cancellationToken) : Task.CompletedTask;

    /// <summary>ITEMS-HERMES: active Hermes Hoods back into memory after a restart.</summary>
    private static async Task LoadHermesHoodsAsync(CancellationToken cancellationToken)
    {
        if (Godswar.Server.Infrastructure.EventsNpc.EventsNpcRuntime.Store is not { } store) return;
        try
        {
            var rows = await store.ReadScalarAsync(
                "SELECT string_agg(character_id || ':' || extract(epoch FROM expires_at)::bigint, ',') FROM public.hermes_hood_state WHERE expires_at > now();",
                cancellationToken) as string;
            foreach (var part in (rows ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split(':');
                Godswar.Server.State.HermesHoods.Set(int.Parse(bits[0]), DateTimeOffset.FromUnixTimeSeconds(long.Parse(bits[1])));
            }
            Console.WriteLine($"[hermes-hood] hoods loaded: {(rows ?? "none")}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[hermes-hood] hood load skipped: {error.GetType().Name}");
        }
    }

    /// <summary>W1 Cypress Leave: active masks back into memory after a restart.</summary>
    private static async Task LoadMaskedNamesAsync(CancellationToken cancellationToken)
    {
        if (Godswar.Server.Infrastructure.EventsNpc.EventsNpcRuntime.Store is not { } store) return;
        try
        {
            var rows = await store.ReadScalarAsync(
                "SELECT string_agg(character_id || ':' || extract(epoch FROM expires_at)::bigint, ',') FROM public.masked_state WHERE expires_at > now();",
                cancellationToken) as string;
            foreach (var part in (rows ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split(':');
                Godswar.Server.State.MaskedNames.Set(int.Parse(bits[0]), DateTimeOffset.FromUnixTimeSeconds(long.Parse(bits[1])));
            }
            Console.WriteLine($"[cypress-leave] masks loaded: {(rows ?? "none")}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[cypress-leave] mask load skipped: {error.GetType().Name}");
        }
    }
}
