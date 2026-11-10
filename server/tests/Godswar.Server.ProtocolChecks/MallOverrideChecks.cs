using System.Buffers.Binary;
using Godswar.Server.Application.Npc;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MallOverrideChecks
{
    public const string CheckName = "Mall overrides replace a camp catalog and the admin tab is server-gated";

    public static Task RunAsync()
    {
        var captured = PacketBuilder.MallCatalog(GameDefaults.AthensCamp);
        Check.True(PacketBuilder.TryResolveMallCatalogOffer(GameDefaults.AthensCamp, 0, 9, 9040, out var stock) && stock.UnitPrice > 0,
            "captured listing resolves before overrides");
        var admin = new NpcShopOverrideRow(4160, 1, 7, 1, 1, 0, 1);
        var edited = new NpcShopOverrideRow(9040, 777, 0, 1, 1, 0, 1);
        try
        {
            NpcContentOverrides.Install(new NpcContentSnapshot(991,
                new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>
                {
                    [PacketBuilder.MallAdminService] = [admin],
                    [PacketBuilder.MallSpartaService] = Enumerable.Repeat(edited, 20).ToArray()
                }, [], [44]));

            Check.True(captured.SequenceEqual(PacketBuilder.MallCatalog(GameDefaults.AthensCamp)), "Athens stays captured, no admin tab for a normal account");
            var withTab = PacketBuilder.MallCatalog(GameDefaults.AthensCamp, admin: true);
            Check.Equal(captured.Length + 96, withTab.Length, "admin account gets one extra one-record frame");
            Check.Equal((byte)7, withTab[captured.Length + 4], "the extra frame is tab 7");
            Check.True(!PacketBuilder.TryResolveMallCatalogOffer(GameDefaults.AthensCamp, 7, 0, 4160, out _), "normal account cannot buy from tab 7");
            Check.True(PacketBuilder.TryResolveMallCatalogOffer(GameDefaults.AthensCamp, 7, 0, 4160, out var bought, admin: true), "admin account can");
            Check.Equal(1, bought.UnitPrice, "admin price");
            Check.True(bought.Currency == Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency.Silver, "admin tab charges silver");

            var sparta = PacketBuilder.MallCatalog(GameDefaults.SpartaCamp);
            Check.Equal((8 + 16 * 88) + (8 + 4 * 88), sparta.Length, "20 rows lay out as 16 + 4 records");
            Check.Equal(777u, BinaryPrimitives.ReadUInt32LittleEndian(sparta.AsSpan(8 + 68)), "edited price on the wire");
            Check.True(PacketBuilder.TryResolveMallCatalogOffer(GameDefaults.SpartaCamp, 0, 17, 9040, out var page2) && page2.UnitPrice == 777, "second frame keeps the running index");
        }
        finally
        {
            NpcContentOverrides.Install(NpcContentSnapshot.Empty);
        }
        return Task.CompletedTask;
    }
}
