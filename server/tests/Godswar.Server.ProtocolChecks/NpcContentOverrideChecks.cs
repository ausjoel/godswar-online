using System.Buffers.Binary;
using Godswar.Server.Application.Npc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class NpcContentOverrideChecks
{
    public const string CheckName = "Editor shop overrides produce a catalogue the purchase path accepts";

    public static Task RunAsync()
    {
        const CapitalNpcServiceKind service = CapitalNpcServiceKind.PetMerchant;
        var compiled = PacketBuilder.CapitalNpcShopCatalog(5086, new CapitalNpcShopBalances(), service);
        var category = compiled[8];
        var currency = compiled[9];
        var rows = Enumerable.Range(0, 25)
            .Select(i => new NpcShopOverrideRow((uint)(4000 + i), 100 + i, category, 1, 1, 0, 1))
            .ToArray();
        try
        {
            NpcContentOverrides.Install(new NpcContentSnapshot(
                1,
                new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>> { [service.ToString()] = rows },
                []));
            var packets = PacketBuilder.CapitalNpcShopCatalog(5086, new CapitalNpcShopBalances(), service);
            var offset = 0;
            var total = 0;
            var frames = 0;
            while (offset < packets.Length)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(packets.AsSpan(offset));
                Check.True(length == 16 + (packets[offset + 10] * 88), "each frame's length matches its item count");
                Check.True(packets[offset + 8] == category && packets[offset + 9] == currency, "frames keep the shop's category and currency");
                Check.True(packets[offset + 11] == (frames == 0 ? 1 : 0), "only the first frame starts a listing");
                Check.True(BinaryPrimitives.ReadUInt32LittleEndian(packets.AsSpan(offset + 4)) == 5086, "the NPC id is written");
                total += packets[offset + 10];
                frames++;
                offset += length;
            }

            Check.True(total == rows.Length, "every edited row is listed");
            Check.True(frames > 1 || rows.Length <= compiled[10], "long lists span frames");
            for (var i = 0; i < rows.Length; i++)
            {
                Check.True(
                    PacketBuilder.TryResolveCapitalNpcShopOffer(service, category, i, rows[i].ItemId, out var offer) &&
                    offer.UnitPrice == rows[i].Price &&
                    offer.Item.Attribute1 is null,
                    "each row resolves at its index with its price and no attributes");
            }

            Check.True(
                !PacketBuilder.TryResolveCapitalNpcShopOffer(service, category, 0, rows[1].ItemId, out _),
                "a mismatched item is refused");
            NpcContentOverrides.Install(NpcContentSnapshot.Empty);
            Check.True(
                PacketBuilder.CapitalNpcShopCatalog(5086, new CapitalNpcShopBalances(), service).AsSpan().SequenceEqual(compiled),
                "removing the override restores the compiled list");
        }
        finally
        {
            NpcContentOverrides.Install(NpcContentSnapshot.Empty);
        }

        return Task.CompletedTask;
    }
}

internal static class NpcBattlefieldTeleportChecks
{
    public const string CheckName = "Editor battlefield teleports apply to the resolved destination";

    public static Task RunAsync()
    {
        var buttons = BattlefieldTransporterProtocol.EditableButtons().ToArray();
        Check.True(buttons.Length == 8, "both capitals list four buttons");
        foreach (var (npcKey, subId, destination) in buttons)
        {
            uint npcId = npcKey == "Sparta_056" ? BattlefieldTransporterProtocol.SpartaNpcId : BattlefieldTransporterProtocol.AthensNpcId;
            var path = Enumerable.Repeat(-1, BattlefieldTransporterProtocol.FunctionArgumentCount).ToArray();
            if (subId == BattlefieldTransporterProtocol.NiMiniRootSubId)
            {
                path[0] = BattlefieldTransporterProtocol.NiMiniUpperSubId;
            }

            Check.True(
                BattlefieldTransporterProtocol.TryResolveDestination(
                    npcKey, npcId, (byte)destination.SourceMapId, (byte)(npcKey == "Sparta_056" ? 0 : 1),
                    BattlefieldTransporterProtocol.DialogIndex, subId, path, out var resolved) &&
                resolved == destination,
                $"{npcKey} button {subId} is a button the game resolves to the same place");
        }

        var farm = buttons.First(static b => b.SubId == BattlefieldTransporterProtocol.LelantineFarmSubId).Destination;
        var edited = BattlefieldTransporterProtocol.ApplyEdit(farm, 5, 12.5f, -30f, 44);
        Check.True(edited.TargetMapId == 5 && edited.TargetX == 12.5f && edited.TargetZ == -30f && edited.MinimumLevel == 44, "the edited point and minimum level apply");
        Check.True(edited.MaximumLevel == farm.MaximumLevel && edited.Kind == farm.Kind, "the upper limit and schedule kind stay compiled");
        Check.True(BattlefieldTransporterProtocol.ApplyEdit(farm, null, 0, 0, 1) == farm, "no row leaves the destination alone");
        return Task.CompletedTask;
    }
}
