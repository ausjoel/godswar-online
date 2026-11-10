using Godswar.Server.Application.Events;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

// the admin 2026-11-10: server info in the top box (board), chat box for player chat + boss spawns + chat notices.
internal static class ServerMessageRoutingChecks
{
    public const string CheckName = "Server message routing (top box vs chat box)";

    public static Task RunAsync()
    {
        try
        {
            var defs = ServerRateCatalog.Definitions.ToDictionary(static d => d.Key);
            Check.Equal(1m, defs[Finalise2RateKeys.BoardServerInfo].DefaultValue, "board.server_info defaults on");
            Check.Equal(1m, defs[Finalise2RateKeys.ChatWorldBossLine].DefaultValue, "board.world_boss_chat_line defaults on");
            Check.Equal(1m, defs[Finalise2RateKeys.EventChatLines].DefaultValue, "board.event_chat_lines default 1");

            Check.Equal(1, BoardText.Wrap("Online: 1 | Athenians: 1 | Spartans: 0").Count, "the population line fits one board line");
            var wrapped = BoardText.Wrap("Rags to Riches: you claimed 1,000,000 Gold. Prizes can be claimed after 11:55pm.");
            Check.True(wrapped.Count is 2 && wrapped.All(static l => l.Length <= BoardText.Width), "a long line wraps at 46");
            var cut = BoardText.Wrap(string.Join(' ', Enumerable.Repeat("word", 60)));
            Check.Equal(BoardText.MaxLines, cut.Count, "at most 3 board lines");
            Check.True(cut[^1].EndsWith("...", StringComparison.Ordinal) && cut.All(static l => l.Length <= BoardText.Width),
                "a cut message ends in ...");
            Check.True(BoardText.Wrap(new string('x', 100)).All(static l => l.Length <= BoardText.Width), "one long word is split");
            var spring = BoardText.Wrap(BoardText.Clip(
                "Thessalonikaa lit the 1,000th Firecracker and drove the Lunar Beast away! Reward: a Flying Carpet."));
            Check.True(spring.Count <= BoardText.MaxLines && !spring[^1].EndsWith("...", StringComparison.Ordinal),
                "the board Lunar Beast line fits uncut");
            Check.Equal(126, BoardText.Clip(new string('y', 200)).Length, "messages are clipped to the 126-byte log line");

            var none = new Dictionary<string, IReadOnlyList<EventWindow>>();
            Check.True(new EventContent(none, [], 5).WelcomeText is null, "no welcome by default");
            Check.True(new EventContent(none, [], 5, "  ").WelcomeText is null, "a blank welcome is off");
            Check.True(new EventContent(none, [], 5, "Hi").WelcomeText == "Hi", "a welcome text is kept");
            var chat = new ServerNotice(1, "Event transporters now open", false, 127, 0, 0, 0, null, null, Chat: true);
            Check.True(chat.Chat && !chat.Board && !chat.Banner, "delivery chat is its own kind");
        }
        finally
        {
            ServerRates.Install(ServerRatesSnapshot.Default);
        }

        return Task.CompletedTask;
    }
}
