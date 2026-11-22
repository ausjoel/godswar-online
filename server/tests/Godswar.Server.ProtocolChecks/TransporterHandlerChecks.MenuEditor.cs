using Godswar.Server.Application.Npc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// B22 (d32): editor teleporter menus (public.npc_teleport_menu) through the real handler: order, hidden, added
// (borrowed and own target), price per button in silver / gold, the "not enough" line, and the refund.
internal static partial class TransporterHandlerChecks
{
    public const string MenuEditorCheckName = "B22 editor teleporter menus: order, hidden, added, price and refund";

    private static NpcTeleportMenuRow Row(
        string npc, int position, int sub, bool hidden = false, int? price = null, string currency = "silver",
        short? map = null, float? x = null, float? z = null, int? level = null) =>
        new(npc, position, sub, hidden, price, currency, map, x, z, level, null);

    public static async Task RunMenuEditorAsync()
    {
        try
        {
            await CheckEmptyTableKeepsCompiledMenuAsync();
            await CheckEditedMenuAsync();
        }
        finally
        {
            NpcContentOverrides.Install(NpcContentSnapshot.Empty);
        }
    }

    private static async Task CheckEmptyTableKeepsCompiledMenuAsync()
    {
        NpcContentOverrides.Install(NpcContentSnapshot.Empty);
        var endpoint = Sparta();
        await using var fixture = await CreateFixtureAsync(endpoint, level: 140);
        fixture.Store.Silver = 1_000;
        await IssueTransporterMenuAsync(fixture, endpoint);
        Check.True(
            fixture.ReadPackets() is [var menu] &&
            menu.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                endpoint.InteractionId,
                TransporterProtocol.DialogIndex,
                [.. endpoint.Menu, .. TransporterProtocol.GetExtensionMenu(endpoint.NpcKey, endpoint.InteractionId)])),
            "no editor rows: the compiled menu (published + server-added) is sent unchanged");
        var before = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateActionPacket(endpoint.InteractionId, 1));
        Check.True(
            fixture.Store.PositionWrites.Count == 1 && fixture.Store.Silver == 1_000 &&
            fixture.ReadPackets().Count > before,
            "no editor rows: an ordinary button stays free and moves the character");
    }

    private static async Task CheckEditedMenuAsync()
    {
        var endpoint = Sparta();
        var compiledSub1 = TransporterProtocol.Destinations.Single(static d => d.NpcKey == "Sparta_042" && d.SubId == 1);
        var traversal = GameplayContentTestFixtures.Runtime.MapTraversal;
        float arrivalX = 0f, arrivalZ = 0f;
        var resolvedArrival = false;
        if (traversal.TryGetAutomaticLink(compiledSub1.ArrivalAnchorSourceMapId, compiledSub1.TargetMapId, out var anchor) &&
            traversal.TryResolveTargetArrival(anchor, PortalRadius, out var resolved))
        {
            resolvedArrival = true;
            arrivalX = resolved.TargetArrival.X;
            arrivalZ = resolved.TargetArrival.Z;
        }

        Check.True(resolvedArrival, "the Sparta suburb arrival is known (used as an added button's own target)");
        var snapshot = new NpcContentSnapshot(
            7,
            new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>(),
            [],
            teleportMenus:
            [
                Row("Sparta_042", 1, 3),                                    // Peloponnesus first
                Row("Sparta_042", 2, 1, price: 500),                        // Suburbs, 500 Silver
                Row("Sparta_042", 3, 2, hidden: true),                      // Nemea hidden
                Row("Sparta_042", 4, 1093),                                 // Olympus borrowed from Mycenae
                Row("Sparta_042", 5, 1094, price: 50, currency: "gold",     // added slot, own target, 50 Gold
                    map: 4, x: arrivalX, z: arrivalZ, level: 1),
                Row("Sparta_042", 6, 1095, price: 300,                      // added slot to a map that does not exist
                    map: 250, x: 0f, z: 0f, level: 1),
                Row("Sparta_042", 7, 1096)                                  // added slot with no target: skipped
            ]);
        NpcContentOverrides.Install(snapshot);

        await using var fixture = await CreateFixtureAsync(endpoint, level: 140);
        fixture.Store.Silver = 1_000;
        fixture.Store.Gold = 10;
        await IssueTransporterMenuAsync(fixture, endpoint);
        Check.True(
            fixture.ReadPackets() is [var menu] &&
            menu.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                endpoint.InteractionId, TransporterProtocol.DialogIndex, [3, 1, 1093, 1094, 1095])),
            "editor rows: the menu is the visible rows in order, hidden and unresolvable rows left out");

        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, 2), "hidden button");
        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, 8), "compiled button not in the edited menu");

        var count = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateActionPacket(endpoint.InteractionId, 1094));
        Check.True(
            fixture.ReadPackets().Skip(count).ToArray() is [var note] &&
            note.SequenceEqual(PacketBuilder.ServerNote("You need 50 Gold!")) &&
            fixture.Store.Gold == 10 && fixture.Store.PositionWrites.Count == 0,
            "a gold price the wallet cannot cover is refused with a server line and no move");

        await IssueTransporterMenuAsync(fixture, endpoint); // each accepted click uses up the menu lease
        count = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateActionPacket(endpoint.InteractionId, 1095));
        Check.True(
            fixture.Store.Silver == 1_000 && fixture.Store.PositionWrites.Count == 0 &&
            fixture.ReadPackets().Skip(count).Any(p => p.SequenceEqual(PacketBuilder.ServerNote(TransporterUnavailableText))),
            "a charged move that fails is refunded and reported as unavailable");

        // The menu lease is used up by each click; open it again for the paid move.
        await IssueTransporterMenuAsync(fixture, endpoint);
        await InvokeAsync(fixture.Handler, CreateActionPacket(endpoint.InteractionId, 1));
        Check.True(
            fixture.Store.Silver == 500 && fixture.Character.Silver == 500 &&
            fixture.Store.PositionWrites is [var write] && write.MapId == compiledSub1.TargetMapId,
            "a silver price is charged once and the character moves to the compiled destination");

        await using var rich = await CreateFixtureAsync(endpoint, level: 140);
        rich.Store.Gold = 80;
        await IssueTransporterMenuAsync(rich, endpoint);
        await InvokeAsync(rich.Handler, CreateActionPacket(endpoint.InteractionId, 1094));
        Check.True(
            rich.Store.Gold == 30 && rich.Character.Gold == 30 &&
            rich.Store.PositionWrites is [var added] && added.MapId == 4 &&
            added.X == arrivalX && added.Z == arrivalZ,
            "an added button with its own target charges its gold price and lands on its own point");
    }

    private const string TransporterUnavailableText = "Transportation is temporarily unavailable. Please try again.";
}

/// <summary>B22 (d32): the menu policy without a handler: fallback, cap, Athens' two ids, prices.</summary>
internal static class TransporterMenuPolicyChecks
{
    public const string CheckName = "B22 teleporter menu policy: fallback, 12-button cap, Athens ids, fees";

    public static Task RunAsync()
    {
        var empty = NpcContentSnapshot.Empty;
        foreach (var (key, id, map, menu) in new (string, uint, short, IReadOnlyList<int>)[]
                 {
                     ("Sparta_042", TransporterProtocol.SpartaNpcId, 0, TransporterProtocol.SpartaInitialMenuSubIds),
                     ("Athens_041", TransporterProtocol.AthensNpcId, 1, TransporterProtocol.AthensInitialMenuSubIds),
                     ("Athens_041", TransporterProtocol.PublishedAthensNpcId, 1, TransporterProtocol.AthensInitialMenuSubIds),
                     ("Mycenae_All_013", TransporterProtocol.MycenaeNpcId, 6, TransporterProtocol.MycenaeInitialMenuSubIds)
                 })
        {
            Check.True(
                TransporterMenuPolicy.EffectiveMenu(menu, key, id, map, empty)
                    .SequenceEqual(menu.Concat(TransporterProtocol.GetExtensionMenu(key, id))),
                $"{key}/{id}: an empty table keeps the compiled menu");
        }

        var many = Enumerable.Range(0, 14)
            .Select(i => new NpcTeleportMenuRow("Athens_041", i + 1, 1094 + (i % 12), false, null, "silver",
                4, 0f, 0f, 1, null))
            .Concat([new NpcTeleportMenuRow("Athens_041", 20, 4, false, null, "silver", null, null, null, null, null),
                     new NpcTeleportMenuRow("Athens_041", 21, 6, false, null, "silver", null, null, null, null, null)])
            .GroupBy(static r => r.SubId).Select(static g => g.First())
            .ToArray();
        var snapshot = new NpcContentSnapshot(3, new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>(), [], teleportMenus: many);
        foreach (var id in new[] { TransporterProtocol.AthensNpcId, TransporterProtocol.PublishedAthensNpcId })
        {
            var sent = TransporterMenuPolicy.EffectiveMenu(TransporterProtocol.AthensInitialMenuSubIds, "Athens_041", id, 1, snapshot);
            Check.True(sent.Length == TransporterMenuPolicy.MaximumButtons, $"Athens {id}: at most 12 buttons are sent");
            Check.True(!sent.Contains(4) && !sent.Contains(6), $"Athens {id}: rows past the 12th are left out");
        }

        Check.True(
            TransporterMenuPolicy.EffectiveMenu(TransporterProtocol.SpartaInitialMenuSubIds, "Sparta_042", TransporterProtocol.SpartaNpcId, 0, snapshot)
                .SequenceEqual(TransporterMenuPolicy.CompiledMenu(TransporterProtocol.SpartaInitialMenuSubIds, "Sparta_042", TransporterProtocol.SpartaNpcId)),
            "rows for one NPC leave the others on their compiled menu");

        var mycenae = TransporterProtocol.Destinations.Single(static d => d.NpcKey == "Sparta_042" && d.SubId == TransporterProtocol.MycenaeMenuSubId);
        var nemea = TransporterProtocol.Destinations.Single(static d => d.NpcKey == "Sparta_042" && d.SubId == 2);
        Check.True(TransporterMenuPolicy.Price(mycenae, null, false) == new TeleportPrice("silver", 20_000, true),
            "no row: the capitals' Mycenae button keeps its 20,000 Silver and the client's own line");
        Check.True(TransporterMenuPolicy.Price(mycenae, null, gmSkips: true).Amount == 0, "GMs who skip the gate do not pay");
        Check.True(TransporterMenuPolicy.Price(nemea, null, false).Amount == 0, "no row: other buttons stay free");
        var mycenaeRow = new NpcTeleportMenuRow("Sparta_042", 1, 11, false, 0, "silver", null, null, null, null, null);
        Check.True(TransporterMenuPolicy.Price(mycenae, mycenaeRow, false).Amount == 0, "a row price of 0 makes Mycenae free");
        Check.True(
            TransporterMenuPolicy.Price(mycenae, mycenaeRow with { Price = 30_000 }, false) == new TeleportPrice("silver", 30_000, false),
            "another silver price does not use the client's fixed 20,000 line");
        Check.True(
            TransporterMenuPolicy.Price(nemea, mycenaeRow with { Price = 5, Currency = "bgold" }, false) == new TeleportPrice("bgold", 5, false),
            "a row can charge B-Gold");
        Check.True(
            TransporterMenuPolicy.TryResolveRowDestination("Athens_041", TransporterProtocol.AthensNpcId, 1,
                new NpcTeleportMenuRow("Athens_041", 1, 1093, false, null, "silver", null, null, null, 150, null),
                out var olympus, out var noTarget) &&
            olympus.TargetMapId == 21 && olympus.MinimumLevel == 150 && noTarget is null,
            "a capital can borrow Mycenae's Olympus button (map 21) with the row's level");
        return Task.CompletedTask;
    }
}
