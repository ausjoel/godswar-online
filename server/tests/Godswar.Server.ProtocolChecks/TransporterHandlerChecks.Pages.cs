using Godswar.Server.Application.Npc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// B22 B26-T (the admin d121-d123): a second menu page (npc_teleport_menu.parent_sub_id) through the real handler. The page
// button opens its page in the same window; a page click arrives as (sub = page button, path[0] = clicked), which is
// what the real client sends (rig-read, Development/batch-2026-11-22/B26-T).
internal static partial class TransporterHandlerChecks
{
    public const string PagesCheckName = "B22 B26-T teleporter menu pages: page button, page-2 clicks, refusals, 12 per page";

    private const int PageButton = 1104;

    private static NpcTeleportMenuRow PageRow(int position, int sub, int? parent, bool hidden = false, int? level = null) =>
        new("Sparta_042", position, sub, hidden, null, "silver", null, null, null, level, null, parent);

    public static async Task RunPagesAsync()
    {
        try
        {
            CheckPagePolicy();
            await CheckPageHandlerAsync();
        }
        finally
        {
            NpcContentOverrides.Install(NpcContentSnapshot.Empty);
        }
    }

    private static NpcContentSnapshot Snapshot(params NpcTeleportMenuRow[] rows) =>
        new(11, new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>(), [], teleportMenus: rows);

    private static void CheckPagePolicy()
    {
        var id = TransporterProtocol.SpartaNpcId;
        var published = TransporterProtocol.SpartaInitialMenuSubIds;
        var rows = new List<NpcTeleportMenuRow> { PageRow(1, 1, null), PageRow(2, 11, null), PageRow(3, PageButton, null) };
        // 14 page-2 rows that resolve (borrowed from the compiled destinations), so the page is cut at 12.
        var page2 = new[] { 1089, 1088, 1091, 1090, 1092, 1093, 2, 3, 8, 9, 10, 4, 5, 6 };
        rows.AddRange(page2.Select((sub, i) => PageRow(10 + i, sub, PageButton)));
        var snapshot = Snapshot([.. rows]);
        Check.True(
            TransporterMenuPolicy.EffectiveMenu(published, "Sparta_042", id, 0, snapshot).SequenceEqual([1, 11, PageButton]),
            "page 1 = the rows without a parent, the page button included although it goes nowhere itself");
        var page = TransporterMenuPolicy.PageMenu("Sparta_042", id, 0, PageButton, snapshot);
        Check.True(page.Length == TransporterMenuPolicy.MaximumButtons && page.SequenceEqual(page2.Take(12)),
            "page 2 = its rows in order, at most 12");
        Check.True(TransporterMenuPolicy.PageMenu("Sparta_042", id, 0, 11, snapshot).Length == 0,
            "a button with no rows under it opens no page");

        var allHidden = Snapshot(PageRow(1, 1, null), PageRow(2, PageButton, null), PageRow(3, 1093, PageButton, hidden: true));
        Check.True(
            TransporterMenuPolicy.EffectiveMenu(published, "Sparta_042", id, 0, allHidden).SequenceEqual([1]),
            "a page button whose page shows nothing is not sent");
        var hiddenButton = Snapshot(PageRow(1, 1, null), PageRow(2, PageButton, null, hidden: true), PageRow(3, 1093, PageButton));
        Check.True(
            TransporterMenuPolicy.EffectiveMenu(published, "Sparta_042", id, 0, hiddenButton).SequenceEqual([1]) &&
            TransporterMenuPolicy.PageMenu("Sparta_042", id, 0, PageButton, hiddenButton).Length == 0,
            "a hidden page button hides its page too");
        Check.True(
            TransporterMenuPolicy.EffectiveMenu(TransporterProtocol.MycenaeInitialMenuSubIds, "Mycenae_All_013",
                    TransporterProtocol.MycenaeNpcId, 6, snapshot)
                .SequenceEqual(TransporterMenuPolicy.CompiledMenu(TransporterProtocol.MycenaeInitialMenuSubIds,
                    "Mycenae_All_013", TransporterProtocol.MycenaeNpcId)),
            "capital page rows leave Mycenae's menu alone");
        Check.True(
            !TransporterMenuPolicy.TryResolve("Sparta_042", id, 0, TransporterProtocol.DialogIndex, 1093,
                Enumerable.Repeat(-1, TransporterProtocol.FunctionArgumentCount).ToArray(), snapshot, out _, out _, out _),
            "a page-2 row does not resolve as a page-1 click");
    }

    private static async Task CheckPageHandlerAsync()
    {
        var endpoint = Sparta();
        NpcContentOverrides.Install(Snapshot(
            PageRow(1, 1, null),
            PageRow(2, 11, null),
            PageRow(3, PageButton, null),
            PageRow(4, 1093, PageButton),
            PageRow(5, 1089, PageButton),
            PageRow(6, 1088, PageButton, hidden: true)));

        await using var fixture = await CreateFixtureAsync(endpoint, level: 190);
        await IssueTransporterMenuAsync(fixture, endpoint);
        Check.True(
            fixture.ReadPackets() is [var menu] &&
            menu.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(endpoint.InteractionId, TransporterProtocol.DialogIndex,
                [1, 11, PageButton])),
            "page 1 is sent with the page button");

        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, 1093), "page-2 button clicked from page 1");
        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, PageButton, a => a[0] = 1093),
            "page click before the page was opened");

        var count = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateActionPacket(endpoint.InteractionId, PageButton));
        Check.True(
            fixture.ReadPackets().Skip(count).ToArray() is [var page] &&
            page.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(endpoint.InteractionId, TransporterProtocol.DialogIndex,
                [1093, 1089])) &&
            fixture.Store.PositionWrites.Count == 0,
            "the page button answers with its page (hidden rows left out) and moves nobody");

        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, PageButton, a => a[0] = 1088), "hidden page-2 button");
        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, PageButton, a => a[0] = 1), "button not on page 2");
        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, PageButton, a => { a[0] = 1093; a[1] = 5; }),
            "page click with a crafted longer path");
        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, 11), "page-1 button while page 2 is open");
        await AssertRejectedAsync(fixture, CreateActionPacket(endpoint.InteractionId, 11, a => a[0] = 1093), "page click under the wrong page button");

        await InvokeAsync(fixture.Handler, CreateActionPacket(endpoint.InteractionId, PageButton, a => a[0] = 1093));
        Check.True(fixture.Store.PositionWrites is [var olympus] && olympus.MapId == 21,
            "a page-2 click moves the character (Olympus, borrowed from Mycenae)");

        await using var low = await CreateFixtureAsync(endpoint, level: 150);
        await IssueTransporterMenuAsync(low, endpoint);
        await InvokeAsync(low.Handler, CreateActionPacket(endpoint.InteractionId, PageButton));
        count = low.ReadPackets().Count;
        await InvokeAsync(low.Handler, CreateActionPacket(endpoint.InteractionId, PageButton, a => a[0] = 1093));
        Check.True(
            low.ReadPackets().Skip(count).ToArray() is [var refusal] &&
            refusal.SequenceEqual(PacketBuilder.CapturedNpcFunctionActionResponse(endpoint.InteractionId,
                TransporterProtocol.DialogIndex, TransporterProtocol.GenericLevelRequirementResultSubId)) &&
            low.Store.PositionWrites.Count == 0,
            "an under-level page-2 click gets the type-1 sub 2001 line (drawn by the client with Index 3) and no move");
    }
}
