using System.Buffers.Binary;
using Godswar.Server.Application.Awards;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.Awards;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W4-B (the admin: "allow awards to go to warehouse event awarder (not regular warehouse, I think there is an npc next to
/// them like that)"). That NPC is the Prize Chest, Athens_123 / Sparta_123 at (92,-159), five steps from the
/// warehouse keeper (97,-159) and beside the Warehouse Manager (91,-155); its client text: "A convenient and thrilling
/// chest where you claim the prizes (including B-GOLD) handed out by GM." The server opens it with the 0x40 window
/// flag, which the client shows as the Storage window's separate award tab.
///
/// prize.prize_chest_window = 1 (default since W5-F, seen working in the real client): the award tab is filled
/// (40 cells, B-Gold box); the Draw button takes everything that fits, a right-click or a drag to the bag takes one
/// cell, and the tab is repainted after each. Silver and Gold rows have no place in that tab: one chat line says so
/// and Draw takes them. = 0: talking to the chest hands over every waiting prize that fits (bag and wallet) and says
/// so in one chat line; the rest keeps waiting.
/// </summary>
internal sealed partial class GameClientHandler
{
    private static readonly TimeSpan PrizeChestLease = TimeSpan.FromMinutes(5);
    private DateTimeOffset _prizeChestOpenedAt = DateTimeOffset.MinValue;
    private long[] _prizeChestVisibleIds = [];

    private async Task OnPrizeChestOpenedAsync(CancellationToken cancellationToken)
    {
        if (!Finalise2Rates.On(Finalise2RateKeys.PrizeChest) || _character is null)
        {
            return;
        }

        if (Finalise2Rates.On(Finalise2RateKeys.PrizeChestWindow))
        {
            _prizeChestOpenedAt = DateTimeOffset.UtcNow;
            await SendPrizeChestSnapshotAsync(cancellationToken);
            return;
        }

        await DrawPrizeChestAsync(null, cancellationToken);
    }

    /// <summary>
    /// A 10059 frame with storage type 1: Draw, or one cell right-clicked / dragged out of the award tab
    /// (<see cref="PacketBuilder.ParsePrizeChestTransfer"/>). The tab is repainted after every request, so a
    /// refused or repeated one still leaves the client showing what the database holds.
    /// </summary>
    private async Task HandlePrizeChestTransferAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var request = PacketBuilder.ParsePrizeChestTransfer(packet.Buffer);
        var switchedOn = Finalise2Rates.On(Finalise2RateKeys.PrizeChest) &&
            Finalise2Rates.On(Finalise2RateKeys.PrizeChestWindow);
        if (!switchedOn || _character is null || _prizeChestOpenedAt == DateTimeOffset.MinValue)
        {
            Console.WriteLine($"[prize-chest] transfer ignored character={_character?.Name ?? "<none>"} (chest not open)");
            return;
        }

        if (DateTimeOffset.UtcNow - _prizeChestOpenedAt > PrizeChestLease)
        {
            // The window can stay on screen for longer than the lease; say why nothing happened.
            Console.WriteLine($"[prize-chest] transfer ignored character={_character.Name} (lease over)");
            await SendEventsNpcNoticeAsync("Talk to the Prize Chest again to take your prizes.", "PrizeChestLease",
                cancellationToken);
            return;
        }

        long? entryId = null;
        switch (request.Kind)
        {
            case PrizeChestRequestKind.DrawAll:
                break;
            case PrizeChestRequestKind.Cell when request.Cell < _prizeChestVisibleIds.Length:
                entryId = _prizeChestVisibleIds[request.Cell];
                break;
            default:
                Console.WriteLine($"[prize-chest] transfer refused character={_character.Name} " +
                    $"frame={Convert.ToHexString(packet.Buffer.AsSpan(4, 16))}");
                await SendPrizeChestSnapshotAsync(cancellationToken);
                return;
        }

        Console.WriteLine($"[prize-chest] transfer character={_character.Name} kind={request.Kind} cell={request.Cell} " +
            $"entry={(entryId?.ToString() ?? "all")}");
        _prizeChestOpenedAt = DateTimeOffset.UtcNow; // a player who is using the tab keeps it
        await DrawPrizeChestAsync(entryId, cancellationToken);
        await SendPrizeChestSnapshotAsync(cancellationToken);
    }

    /// <summary>
    /// W5-F online delivery (POST /admin/prize-chest/notify after the editor inserted rows): one chat line, and a
    /// repaint when this player has the award tab open. No pop-up (the admin's rule). The rows are already in the
    /// database, so a player who misses the line still finds them at the chest and gets the login reminder.
    /// </summary>
    private async Task<string> NotifyPrizeChestDeliveryAsync(CancellationToken cancellationToken)
    {
        if (!Finalise2Rates.On(Finalise2RateKeys.PrizeChest) || _character is not { } character ||
            EventsNpcRuntime.Store is not { } store)
        {
            return "prize chest is switched off";
        }

        int waiting;
        try
        {
            waiting = (await store.RunAsync(character.Id, EventsNpcDay(EventsNpcNow),
                LoginAwardLedger.ReadPrizeChestAsync, cancellationToken)).Count;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[prize-chest] notify read failed character={character.Name}: {error.GetType().Name}");
            return "error: could not read the prize chest";
        }

        if (waiting == 0)
        {
            return "nothing waiting";
        }

        await SendEventsNpcNoticeAsync(
            waiting == 1
                ? "A prize is waiting for you at the Prize Chest by the warehouse."
                : $"{waiting} prizes are waiting for you at the Prize Chest by the warehouse.",
            "PrizeChestDelivery", cancellationToken);
        var repainted = Finalise2Rates.On(Finalise2RateKeys.PrizeChestWindow) &&
            DateTimeOffset.UtcNow - _prizeChestOpenedAt <= PrizeChestLease;
        if (repainted)
        {
            await SendPrizeChestSnapshotAsync(cancellationToken);
        }

        return repainted ? $"notified; award tab repainted; waiting={waiting}" : $"notified; waiting={waiting}";
    }

    private async Task DrawPrizeChestAsync(long? entryId, CancellationToken cancellationToken)
    {
        if (_character is not { } character || EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        PrizeChestDraw draw;
        try
        {
            draw = await store.RunAsync(character.Id, EventsNpcDay(EventsNpcNow),
                tx => LoginAwardLedger.DrawPrizeChestAsync(tx, entryId), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[prize-chest] draw failed character={character.Name}: {error.GetType().Name} {error.Message}");
            await SendEventsNpcNoticeAsync("The Prize Chest is busy. Please try again.", "PrizeChestBusy", cancellationToken);
            return;
        }

        if (draw.Items.Count > 0 || draw.Silver > 0 || draw.Gold > 0 || draw.BindingGold > 0)
        {
            await RefreshAfterEventsNpcAsync(draw.Items, cancellationToken);
        }

        Console.WriteLine($"[prize-chest] draw character={character.Name} items={draw.Items.Count} silver={draw.Silver} " +
            $"gold={draw.Gold} bgold={draw.BindingGold} waiting={draw.Waiting}");
        await SendEventsNpcNoticeAsync(PrizeChestPolicy.Describe(draw), "PrizeChestDraw", cancellationToken);
    }

    private async Task SendPrizeChestSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_character is not { } character || EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        IReadOnlyList<PrizeChestEntry> pending;
        try
        {
            pending = await store.RunAsync(character.Id, EventsNpcDay(EventsNpcNow),
                LoginAwardLedger.ReadPrizeChestAsync, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[prize-chest] read failed character={character.Name}: {error.GetType().Name}");
            return;
        }

        var visible = PrizeChestPolicy.VisibleItems(pending);
        _prizeChestVisibleIds = visible.Select(static entry => entry.Id).ToArray();
        foreach (var frame in PacketBuilder.PrizeChestSnapshotPackets(visible, PrizeChestPolicy.VisibleBindingGold(pending)))
        {
            await _session.SendAsync(frame, cancellationToken, "PrizeChestSnapshot");
        }

        var hidden = pending.Count(static entry => entry.Kind is PrizeChestPolicy.KindSilver or PrizeChestPolicy.KindGold) +
            Math.Max(0, pending.Count(static entry => entry.Kind == PrizeChestPolicy.KindItem) - visible.Count);
        if (hidden > 0)
        {
            await SendEventsNpcNoticeAsync($"The Prize Chest also holds {hidden} prize(s) it cannot show; Draw takes them too.",
                "PrizeChestHidden", cancellationToken);
        }
    }
}
