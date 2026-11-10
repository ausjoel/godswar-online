using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// TRADE: ends trades and stalls whose players logged out, died, revived, changed map or moved to
/// another instance (checked once a second against the session registry, so no hook in logout,
/// death or travel code is needed). A trade that is already committing is left to its commit.
/// Also ends every open trade when <c>trade.player</c> is switched to 0 and closes every stall
/// when <c>trade.stalls</c> is switched to 0 (review finding 15/19), so the gate is a clean
/// rollback for sessions in progress.
/// </summary>
internal static class PlayerTradeWatchdog
{
    private static int _started;

    public static void Ensure(GameSessionRegistry registry)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(() => RunAsync(registry));
    }

    private static async Task RunAsync(GameSessionRegistry registry)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                await SweepAsync(registry, CancellationToken.None);
            }
            catch (Exception error)
            {
                Console.WriteLine($"[trade] watchdog sweep failed: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    internal static Task SweepAsync(GameSessionRegistry registry, CancellationToken cancellationToken) =>
        SweepAsync(registry, GameClientHandler.PlayerTradeEnabled, GameClientHandler.PlayerStallsEnabled, cancellationToken);

    internal static async Task SweepAsync(
        GameSessionRegistry registry, bool tradeEnabled, bool stallsEnabled, CancellationToken cancellationToken)
    {
        foreach (var trade in PlayerTradeBook.Shared.SnapshotTrades())
        {
            if (trade.Phase != PlayerTradePhase.Open) continue;
            var requesterHere = IsPartyPresent(registry, trade.Requester.Party, out _);
            var targetHere = IsPartyPresent(registry, trade.Target.Party, out _);
            if (requesterHere && targetHere && tradeEnabled) continue;
            var leaver = !tradeEnabled || !requesterHere ? trade.Requester.Party : trade.Target.Party;
            if (PlayerTradeBook.Shared.Cancel(leaver.CharacterId) is null) continue;
            var note = tradeEnabled
                ? $"Trade cancelled: {leaver.Name} is no longer available."
                : "Trade cancelled: trading has been switched off.";
            Console.WriteLine($"[trade] watchdog cancelled id={trade.Id}: {(tradeEnabled ? leaver.Name + " left, died or travelled" : "trade.player is 0")}");
            await SendCancelAsync(registry, trade, note, cancellationToken);
            if (requesterHere) PostReload(registry, trade.Requester.Party.Session, "trade-watchdog");
            if (targetHere) PostReload(registry, trade.Target.Party.Session, "trade-watchdog");
        }

        foreach (var stall in PlayerTradeBook.Shared.SnapshotStalls())
        {
            var sellerHere = IsPartyPresent(registry, stall.Seller, out _);
            if (sellerHere && stallsEnabled) continue;
            var wasOpen = stall.Open;   // CloseStall clears it on this same object
            if (PlayerTradeBook.Shared.CloseStall(stall.Seller.CharacterId) is null) continue;
            Console.WriteLine($"[stall] watchdog closed the stall of {stall.Seller.Name}{(stallsEnabled ? string.Empty : " (trade.stalls is 0)")}");
            if (wasOpen) await BroadcastStallStateAsync(registry, stall.Seller, false, string.Empty, cancellationToken);
            if (!stallsEnabled && sellerHere)
                await TrySendAsync(stall.Seller.Session, PacketBuilder.ServerNote("Your stall was closed: stalls have been switched off."), "PlayerStallNote", cancellationToken);
            PostReload(registry, stall.Seller.Session, "stall-watchdog");
        }
    }

    /// <summary>Online on the same session, world-ready, same map and instance, same life, alive.</summary>
    internal static bool IsPartyPresent(GameSessionRegistry registry, PlayerTradeParty party, out GameSessionContext context)
    {
        if (!registry.TryGetCharacterContext(party.CharacterId, out context) ||
            !ReferenceEquals(context.Session, party.Session) ||
            context.Session.IsDisconnected ||
            context.MapId != party.MapId ||
            context.WorldInstanceId != party.WorldInstanceId ||
            context.Character.CurrentHp <= 0)
        {
            return false;
        }
        return registry.GetPlayerLifeRevision(context.Session) == party.LifeRevision;
    }

    /// <summary>State 4 to both players, each from his own point of view, plus an optional note.</summary>
    internal static async Task SendCancelAsync(
        GameSessionRegistry registry, PlayerTradeSession trade, string? note, CancellationToken cancellationToken)
    {
        foreach (var (recipient, other) in new[] { (trade.Requester.Party, trade.Target.Party), (trade.Target.Party, trade.Requester.Party) })
        {
            var frame = PacketBuilder.PlayerTrade(
                PacketBuilder.TradeObjectIdFor(recipient.ObjectId, true),
                recipient.Name,
                other.ObjectId,
                other.Name,
                PacketBuilder.PlayerTradeStateCancel,
                PacketBuilder.PlayerTradeStateCancel);
            await TrySendAsync(recipient.Session, frame, "PlayerTradeCancel", cancellationToken);
            if (note is not null)
                await TrySendAsync(recipient.Session, PacketBuilder.ServerNote(note), "PlayerTradeNote", cancellationToken);
        }
    }

    /// <summary>10061 for a stall sign: the seller sees himself as 0x1448, everyone else by object id.</summary>
    internal static async Task BroadcastStallStateAsync(
        GameSessionRegistry registry, PlayerTradeParty seller, bool open, string text, CancellationToken cancellationToken)
    {
        await TrySendAsync(seller.Session,
            PacketBuilder.PlayerStall(PacketBuilder.TradeObjectIdFor(seller.ObjectId, true), open, text, seller.Name),
            "PlayerStallState", cancellationToken);
        try
        {
            await registry.BroadcastToWorldInstanceAsync(
                seller.WorldInstanceId,
                PacketBuilder.PlayerStall(seller.ObjectId, open, text, seller.Name),
                cancellationToken,
                excludeSession: seller.Session,
                label: "PlayerStallSign");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[stall] sign broadcast failed: {error.Message}");
        }
    }

    /// <summary>Fire-and-forget: the other handler reloads under its own gate; never awaited under ours.</summary>
    internal static void PostReload(GameSessionRegistry registry, ClientSession session, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await registry.RunOperatorActionAsync(session, new PlayerTradeReloadAction(reason), CancellationToken.None);
                Console.WriteLine($"[trade] partner reload reason={reason}: {result}");
            }
            catch (Exception error)
            {
                Console.WriteLine($"[trade] partner reload failed: {error.GetType().Name}: {error.Message}");
            }
        });
    }

    internal static async Task TrySendAsync(ClientSession session, byte[] frame, string label, CancellationToken cancellationToken)
    {
        if (session.IsDisconnected) return;
        try
        {
            await session.SendAsync(frame, cancellationToken, label);
        }
        catch (Exception error) when (error is ObjectDisposedException or IOException or InvalidOperationException ||
                                      (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A closing session must not break the other player's trade handling.
        }
    }
}
