using System.Globalization;
using Godswar.Server.Application.Rates;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// FINALISE (the admin 2026-11-05): opening an EXP / TP / money / item bag says what it gave in the client's
/// personal info window (lower right). The stock client's SrvMsg.lua prints every server personal note with
/// info type 6, which the Specialmsg.xml filter panel lists as "Other Info" (Trade, Quest, Team, PK, Guild,
/// Other, Pet, Zodiac = 1..8), so the server alone can only reach that one tab; routing per tab needs the
/// client change described in Development/finalise-2026-11-05/serverfix/README.md. One line per bag open,
/// admitted without waiting so a full egress queue skips the line rather than the connection. Off with the hot rate log.bag_gains = 0.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task SendGainLogAsync(string text, CancellationToken token)
    {
        if (ServerRates.Current.Absolute(ServerRateKeys.BagGainLog) != 1) return;
        await SendInfoLineAsync(text, InfoTabOther, token); // FINALISE2: shared with the quest/kill/pet lines
    }

    // FINALISE2 (F-63): one line in the lower-right info window. A client that declared info tabs (1.4.2
    // SrvMsg.lua, tag 0x49 frame) gets a "#<tab>#" prefix and files the line under that tab; any other client
    // gets the bare text, which its SrvMsg.lua puts under Other Info as before.
    private async Task SendInfoLineAsync(string text, int tab, CancellationToken token)
    {
        var clean = new string(text.Where(static c => c is >= ' ' and <= '~' and not '|' and not '#').ToArray()).Trim();
        if (clean.Length == 0) return;
        var prefix = _infoTabsDeclared && tab is >= 1 and <= 8 ? $"#{tab}#" : ""; // FINALISE2
        var room = PacketBuilder.CenteredAnnouncementMaximumTextLength - prefix.Length;
        if (clean.Length > room)
            clean = clean[..(room - 3)] + "...";
        clean = prefix + clean;
        byte[] packet;
        try
        {
            packet = PacketBuilder.PersonalGameLog(clean);
        }
        catch (ArgumentException error)
        {
            Console.WriteLine($"[gain-log] skipped: {error.Message}");
            return;
        }

        // Non-waiting admission: a busy egress queue (a big gift pack on a slow link) drops this
        // informational line instead of failing the queue and closing the connection.
        if (!_session.TryAdmitExact(packet, out var write))
        {
            Console.WriteLine("[gain-log] skipped (egress queue busy)");
            return;
        }

        try
        {
            await write;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Console.WriteLine($"[gain-log] write not completed: {error.GetType().Name}");
        }
    }

    private static string Amount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private string GainItemName(uint itemId) =>
        RequireItemContent().Templates.TryGet(itemId, out var template) && !string.IsNullOrWhiteSpace(template.DisplayName)
            ? template.DisplayName
            : $"item {itemId}";

    private string GainItemList(IEnumerable<(uint ItemId, int Quantity)> items) =>
        string.Join(", ", items
            .GroupBy(static item => item.ItemId)
            .Select(group => (Name: GainItemName(group.Key), Count: group.Sum(static item => item.Quantity)))
            .Select(static item => item.Count > 1 ? $"{item.Name} x{item.Count}" : item.Name));
}
