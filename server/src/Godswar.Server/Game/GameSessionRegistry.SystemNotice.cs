using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>W2: a centred announcement to every player in the realm (lottery hints).</summary>
    internal async Task BroadcastSystemNoticeAsync(string text, CancellationToken cancellationToken)
    {
        byte[] packet;
        try { packet = PacketBuilder.CenteredAnnouncement(text); }
        catch (ArgumentException) { return; }
        foreach (var context in _sessions.Values.ToArray())
        {
            try { await context.Session.SendAsync(packet, cancellationToken, "SystemNotice"); }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException) { }
        }
    }
}
