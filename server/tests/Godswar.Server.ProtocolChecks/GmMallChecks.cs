using System.Buffers.Binary;
using Godswar.Server.Application.Items;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static class GmMallChecks
{
    public const string CheckName = "GM Mall windows fit the client shop window and list every generated item once per category";

    public static Task RunAsync()
    {
        var ids = GmMallCatalog.Sources.SelectMany(c => c.Sections).SelectMany(s => s.Ids).Distinct().ToArray();
        Check.True(ids.Length > 3000, "generated list covers the item library");
        Check.True(!ids.Contains(10109u), "packed Seal Jade is never listed");
        var catalog = PinnedItemTemplateCatalog.Create(
            "protocol-check-gm-mall",
            ids.Where(id => id != 1003).Select(Template).ToArray(),
            [], [], []);
        var categories = GmMallCatalog.Build(catalog);
        var listed = categories.SelectMany(c => c.Pages).SelectMany(p => p.Tabs).SelectMany(t => t.ItemIds).ToList();
        Check.Equal(ids.Length - 1, listed.Distinct().Count(), "every known item listed, unknown 1003 dropped");
        Check.True(!listed.Contains(1003u), "item missing from the server catalog is not offered");
        foreach (var category in categories)
        {
            Check.True(category.Pages.All(p => p.Tabs.Length is >= 1 and <= GmMallCatalog.TabsPerPage),
                $"{category.Key}: 1-4 tabs per window");
            Check.True(category.Pages.SelectMany(p => p.Tabs).All(t => t.ItemIds.Length is >= 1 and <= GmMallCatalog.TabSlots),
                $"{category.Key}: 1-50 items per tab");
            var inCategory = category.Pages.SelectMany(p => p.Tabs).SelectMany(t => t.ItemIds).ToList();
            Check.Equal(inCategory.Count, inCategory.Distinct().Count(), $"{category.Key}: no duplicates");
        }

        Check.True(GmMallCatalog.TryFind(categories, "armor", out var armour) && armour.Key == "armour", "alias");
        Check.True(GmMallCatalog.TryFind(categories, "wea", out var weapons) && weapons.Key == "weapons", "prefix");
        Check.True(!GmMallCatalog.TryFind(categories, "zz", out _), "unknown category");

        var page = categories.First(c => c.Key == "pets").Pages[0];
        var frames = PacketBuilder.GmMallCatalog(5166, page, 777, _ => 99);
        var offset = 0;
        var perTab = new int[4];
        var first = true;
        while (offset < frames.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(offset));
            Check.Equal((ushort)10071, BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(offset + 2)), "opcode");
            Check.Equal(5166u, BinaryPrimitives.ReadUInt32LittleEndian(frames.AsSpan(offset + 4)), "npc id");
            var tab = frames[offset + 8];
            var count = frames[offset + 10];
            Check.True(tab < 4, "client rejects category 4 or more");
            Check.True(count is >= 1 and <= 32, "client takes at most 32 records per frame");
            Check.Equal(16 + count * 88, (int)length, "frame length");
            Check.Equal((byte)3, frames[offset + 9], "silver currency");
            Check.Equal(777, BinaryPrimitives.ReadInt32LittleEndian(frames.AsSpan(offset + 12)), "balance");
            if (first)
            {
                Check.True(tab == 0 && frames[offset + 11] == 1, "first frame is tab 0 with the new-list flag");
                first = false;
            }

            for (var i = 0; i < count; i++)
            {
                var record = frames.AsSpan(offset + 16 + i * 88, 88);
                Check.Equal(page.Tabs[tab].ItemIds[perTab[tab] + i], BinaryPrimitives.ReadUInt32LittleEndian(record), "record order");
                Check.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(68)), "price 1");
                Check.True(record[24] == 1 && record[25] == 1 && record[26] == 0 && record[27] == 99, "plain tradable stack 99");
            }

            perTab[tab] += count;
            offset += length;
        }

        for (var tab = 0; tab < page.Tabs.Length; tab++)
        {
            Check.Equal(page.Tabs[tab].ItemIds.Length, perTab[tab], "whole tab sent");
            Check.True(page.TryGetItem(tab, 0, out var head) && head == page.Tabs[tab].ItemIds[0], "listing lookup");
        }

        Check.True(!page.TryGetItem(4, 0, out _) && !page.TryGetItem(0, 50, out _), "out-of-window listing refused");
        var search = GmMallCatalog.Search(categories, catalog, "1000", out var total);
        Check.True(search is not null && total == 1 && search.Tabs[0].ItemIds.Single() == 1000, "search by id");
        var wide = GmMallCatalog.Search(categories, catalog, "Item", out var many);
        Check.True(wide is not null && many > 200 && wide.Tabs.Sum(t => t.ItemIds.Length) == 200, "search capped at one window");
        return Task.CompletedTask;
    }

    private static ItemTemplateDefinition Template(uint id) =>
        new(id, "consume item", "k" + id, "Item " + id, 0, [], (int)(id % 200), null, null, 0, "t.gwo", "1,1",
            "{\"Overlap\":\"99\"}");
}
