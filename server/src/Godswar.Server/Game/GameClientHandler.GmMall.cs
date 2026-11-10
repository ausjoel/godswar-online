using System.Globalization;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// GM Mall (the admin 2026-11-02): every item in the library, sorted, bought from a
// stock NPC shop window. /mall opens a window on the nearest visible NPC (the
// client stores a shop catalog on an NPC object and drops a 10071 frame whose
// npc id it cannot find, Origin.exe 0x4e0c2d, so a window cannot float free).
// GM authority is checked when the window opens and again on every purchase;
// a session that never opened the mall has no GM Mall state, so a forged
// purchase from a non-GM falls through to the ordinary shop checks and is
// refused there. Price: 1 silver per unit, or nothing with GODSWAR_GM_MALL_FREE=1.
internal sealed partial class GameClientHandler
{
    private sealed record GmMallSession(uint NpcId, string NpcKey, GmMallPage Page);

    private static readonly object GmMallCatalogLock = new();
    private static IReadOnlyList<GmMallCategory>? _gmMallCategories;
    private GmMallSession? _gmMall;

    // Far enough to reach the NPC you stand beside in any town; the client may
    // close a shop window when its NPC is far away (not checked in a real client).
    private const float GmMallComfortDistance = 12f;

    private static bool GmMallIsFree =>
        Environment.GetEnvironmentVariable("GODSWAR_GM_MALL_FREE") is "1" or "true" or "yes";

    private IReadOnlyList<GmMallCategory> GmMallCategories()
    {
        lock (GmMallCatalogLock)
        {
            return _gmMallCategories ??= GmMallCatalog.Build(RequireItemContent().Templates);
        }
    }

    private async Task<string> OperatorMallAsync(string[] args, CancellationToken cancellationToken)
    {
        var categories = GmMallCategories();
        var words = args.Skip(1).ToArray();
        if (words.Length == 0 || words[0] is "help" or "?" or "list")
        {
            return GmMallDirectory(categories);
        }

        GmMallPage? page;
        var verb = words[0].ToLowerInvariant();
        if (verb is "next" or "prev" or "+" or "-")
        {
            page = StepGmMallPage(categories, verb is "next" or "+" ? 1 : -1);
            if (page is null)
            {
                return "open a window first: /mall <category> [page]";
            }
        }
        else if (verb is "find" or "search" or "s")
        {
            var query = string.Join(' ', words.Skip(1));
            page = GmMallCatalog.Search(categories, RequireItemContent().Templates, query, out var total);
            if (page is null)
            {
                return $"nothing listed matches \"{query}\"";
            }

            if (total > GmMallCatalog.TabSlots * GmMallCatalog.TabsPerPage)
            {
                await LogOperatorAsync("mall", _character!.Name, $"find \"{query}\" total={total}", cancellationToken);
            }
        }
        else if (GmMallCatalog.TryFind(categories, verb, out var category))
        {
            var number = 1;
            if (words.Length >= 2 &&
                (!int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                 number < 1 || number > category.Pages.Length))
            {
                return $"{category.Key} has pages 1-{category.Pages.Length}";
            }

            page = category.Pages[number - 1];
        }
        else
        {
            return $"no category \"{words[0]}\". " + GmMallDirectory(categories);
        }

        return await OpenGmMallPageAsync(page, cancellationToken);
    }

    private GmMallPage? StepGmMallPage(IReadOnlyList<GmMallCategory> categories, int step)
    {
        if (_gmMall is null)
        {
            return null;
        }

        var all = categories.SelectMany(c => c.Pages).ToList();
        var current = all.FindIndex(p => p.Key == _gmMall.Page.Key && p.Number == _gmMall.Page.Number);
        if (current < 0)
        {
            return all.FirstOrDefault();
        }

        return all[((current + step) % all.Count + all.Count) % all.Count];
    }

    private async Task<string> OpenGmMallPageAsync(GmMallPage page, CancellationToken cancellationToken)
    {
        if (!TryFindGmMallNpc(out var npc, out var distance))
        {
            return "no NPC in sight: stand next to any NPC (a town is easiest), then /mall again";
        }

        _gmMall = new GmMallSession(npc.InteractionId, npc.NpcKey, page);
        // Selling to this window must not be priced as the NPC's own shop.
        _openCapitalShopService = null;
        // Opening the NPC as a shop makes the client show the trade page; the
        // catalog frames follow at once and again when the client asks (10068).
        await _session.SendAsync(
            PacketBuilder.NpcShopDialogOpenAck(npc.InteractionId, npc.NpcKey),
            cancellationToken,
            "GmMallDialogOpenAck");
        await SendGmMallCatalogAsync(cancellationToken);
        await LogOperatorAsync(
            "mall",
            _character!.Name,
            $"open {page.Key} {page.Number}/{page.Count} npc={npc.InteractionId} key={npc.NpcKey} " +
            $"distance={distance:0.0}",
            cancellationToken);

        // Chat lines stay near the length of the existing /commands lines: two tabs per line.
        var tabs = string.Join("\n", page.Tabs
            .Select((tab, index) =>
                $"tab {index + 1}: {tab.Section} Lv {tab.MinLevel}-{tab.MaxLevel} ({tab.ItemIds.Length})")
            .Chunk(2)
            .Select(pair => string.Join(" | ", pair)));
        var price = GmMallIsFree ? "free (shown as 1)" : "1 silver each";
        var far = distance > GmMallComfortDistance
            ? "\nIf the window does not open or closes, walk closer to that NPC."
            : string.Empty;
        return $"{page.Title} {page.Number}/{page.Count}, {price}\n{tabs}\n" +
               "/mall next | prev | <category> [page] | find <name or id> | /mall = list" + far;
    }

    private static string GmMallDirectory(IReadOnlyList<GmMallCategory> categories)
    {
        var list = string.Join("\n", categories
            .Select(c => $"{c.Key} {c.Pages.Length}")
            .Chunk(8)
            .Select(group => string.Join(", ", group)));
        var total = categories.SelectMany(c => c.Pages).SelectMany(p => p.Tabs).Sum(t => t.ItemIds.Length);
        var price = GmMallIsFree ? "free" : "1 silver each";
        return $"GM Mall, {total} items, {price}. Windows per category:\n{list}\n" +
               "/mall <category> [page] opens a shop window on the nearest NPC\n" +
               "/mall next | prev | find <name or id>";
    }

    private bool TryFindGmMallNpc(out NpcSpawnDefinition npc, out double distance)
    {
        npc = default!;
        distance = double.MaxValue;
        if (_character is null)
        {
            return false;
        }

        foreach (var candidate in _mapNpcsByInteractionId.Values)
        {
            if (!TryResolveMapNpc(candidate.InteractionId, out var visible))
            {
                continue;
            }

            var dx = (double)_character.PositionX - visible.X;
            var dz = (double)_character.PositionZ - visible.Z;
            var d = Math.Sqrt((dx * dx) + (dz * dz));
            if (double.IsFinite(d) && d < distance)
            {
                distance = d;
                npc = visible;
            }
        }

        return distance < double.MaxValue;
    }

    private Task SendGmMallCatalogAsync(CancellationToken cancellationToken)
    {
        var templates = RequireItemContent().Templates;
        return _session.SendAsync(
            PacketBuilder.GmMallCatalog(
                _gmMall!.NpcId,
                _gmMall.Page,
                _character?.Silver ?? 0,
                id => templates.TryGet(id, out var t)
                    ? (byte)Math.Clamp((int)ReadItemStackCap(t.StatsJson), 1, 99)
                    : (byte)1),
            cancellationToken,
            "GmMallCatalog",
            framed: false);
    }

    /// <summary>The client asked for the shop page of the NPC the GM Mall is on.</summary>
    private async Task<bool> TryHandleGmMallPageRequestAsync(uint npcId, CancellationToken cancellationToken)
    {
        if (_gmMall is null || _gmMall.NpcId != npcId)
        {
            return false;
        }

        await SendGmMallCatalogAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// A 10073 purchase addressed to the NPC the GM Mall window is on. Always
    /// consumed: the listing is re-read from the server's own page by tab and
    /// index, the item id must match it, and GM authority is checked again.
    /// </summary>
    private async Task<bool> TryHandleGmMallPurchaseAsync(
        CapitalNpcShopPurchaseIntent intent,
        CancellationToken cancellationToken)
    {
        if (_gmMall is null || _gmMall.NpcId != intent.NpcId || _account is null || _character is null)
        {
            return false;
        }

        var notice = new GamePacket(PacketBuilder.GmPanelCommandFrame(_character.Id, "/mall"));
        if (!await HasOperatorAuthorityAsync(cancellationToken))
        {
            _gmMall = null;
            Console.WriteLine(
                $"[gm-mall] purchase refused, not a GM account={_account.Id} character={_character.Name} " +
                $"item={intent.ItemId}");
            return true;
        }

        if (!_gmMall.Page.TryGetItem(intent.Category, intent.ListingIndex, out var listed) ||
            listed != intent.ItemId)
        {
            Console.WriteLine(
                $"[gm-mall] purchase refused, listing mismatch character={_character.Name} " +
                $"tab={intent.Category} index={intent.ListingIndex} item={intent.ItemId} listed={listed}");
            return true;
        }

        var unitSilver = GmMallIsFree ? 0 : 1;
        CapitalShopPurchaseResult result;
        try
        {
            result = await _capitalShopPurchases.GrantGmMallItemAsync(
                _account.Id, _character.Id, Guid.NewGuid(), intent.ItemId, unitSilver,
                intent.Quantity, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[gm-mall] purchase failed character={_character.Name} item={intent.ItemId} " +
                $"{ex.GetType().Name}: {ex.Message}");
            await SendDeveloperItemFeedbackAsync(notice, "[mall] not bought: server error", cancellationToken);
            return true;
        }

        var name = RequireItemContent().Templates.TryGet(intent.ItemId, out var template)
            ? template.DisplayName
            : intent.ItemId.ToString(CultureInfo.InvariantCulture);
        if (!result.Purchased || result.Character is null)
        {
            await LogOperatorAsync(
                "mall", _character.Name,
                $"refused item={intent.ItemId} name=\"{name}\" quantity={intent.Quantity} status={result.Status}",
                cancellationToken);
            var why = result.Status switch
            {
                CapitalShopPurchaseStatus.InsufficientCapacity => "bag full",
                CapitalShopPurchaseStatus.InsufficientCurrency => "not enough silver",
                _ => result.Status.ToString()
            };
            await SendDeveloperItemFeedbackAsync(notice, $"[mall] not bought: {why}", cancellationToken);
            return true;
        }

        InstallCapitalShopProjection(result.Character);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "GmMallWalletStatus");
        await SendKitBagRefreshAsync(cancellationToken);
        await SendGmMallCatalogAsync(cancellationToken);
        await LogOperatorAsync(
            "mall", _character.Name,
            $"bought item={intent.ItemId} name=\"{name}\" quantity={intent.Quantity} " +
            $"silver={unitSilver * intent.Quantity} page={_gmMall.Page.Key}:{_gmMall.Page.Number} " +
            $"tab={intent.Category} index={intent.ListingIndex}",
            cancellationToken);
        return true;
    }
}
