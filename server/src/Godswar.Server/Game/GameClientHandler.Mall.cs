using System.Buffers.Binary;
using Godswar.Server.Application.Npc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Whether this session already asked for the function-key mall catalog. The
    /// mall is opened from the client's own button, so its purchases carry no npc
    /// to authorize against and are accepted against the catalog this flag tracks.
    /// </summary>
    private bool _mallCatalogOpen;

    // The tab and its prices are server-side. A client asking for category 7 without this gets nothing.
    private bool IsEditorAdminAccount =>
        _account is not null && NpcContentOverrides.Current.EditorAdminAccounts.Contains(_account.Id);

    /// <summary>
    /// Answers the client's mall request with the camp's captured catalog.
    /// </summary>
    /// <remarks>
    /// Captured: the client sends <c>C2S 10178</c> with eight bytes and one zero
    /// dword the moment the mall is opened (07:10:11.796), and the reference
    /// answers with fifteen <c>S2C 10178</c> frames within 400 ms. The request
    /// never arrives at login: a session that only stands in town sends nothing.
    /// </remarks>
    private async Task HandleMallCatalogRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.MallCatalog(_character.Camp, IsEditorAdminAccount),
            cancellationToken,
            "MallCatalog",
            framed: false);
        _mallCatalogOpen = true;
        Console.WriteLine(
            $"[mall] catalog character={_character.Name} " +
            $"camp={_character.Camp} len={packet.Length} " +
            $"payload={packet.ToHexPreview()}");
    }

    /// <summary>
    /// Buys one mall listing the client asked for with <c>C2S 10180</c>.
    /// </summary>
    /// <remarks>
    /// Captured from the live client on 2026-09-14 23:29:48: the request is twenty
    /// bytes, four little-endian dwords - category, listing index, quantity, item
    /// id. Both observed requests line up with the catalog exactly: category 0
    /// index 9 is item 9040 at 115 gold and category 3 index 2 is item 10154 at
    /// 15550 gold, so the price always comes from the captured record the index
    /// names, never from the request.
    /// </remarks>
    private async Task HandleMallPurchaseAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null ||
            !TryReadMallPurchase(
                packet.Payload,
                out var category,
                out var listingIndex,
                out var quantity,
                out var itemId))
        {
            Console.Error.WriteLine(
                $"[mall] rejected malformed purchase length={packet.Length} " +
                $"payload={packet.ToHexPreview()}");
            return;
        }

        if (!_mallCatalogOpen)
        {
            Console.WriteLine(
                $"[mall] purchase without an open catalog " +
                $"character={_character.Name} item={itemId}");
        }

        if (!PacketBuilder.TryResolveMallCatalogOffer(
                _character.Camp,
                category,
                listingIndex,
                itemId,
                out var offer,
                IsEditorAdminAccount))
        {
            Console.Error.WriteLine(
                $"[mall] rejected untrusted purchase character={_character.Name} " +
                $"category={category} index={listingIndex} item={itemId}");
            return;
        }

        await CompleteMallPurchaseAsync(
            itemId,
            quantity,
            offer,
            cancellationToken);
    }

    /// <summary>
    /// Reads the mall purchase request: four dwords, category and index first.
    /// </summary>
    private static bool TryReadMallPurchase(
        ReadOnlySpan<byte> payload,
        out int category,
        out int listingIndex,
        out int quantity,
        out uint itemId)
    {
        category = 0;
        listingIndex = 0;
        quantity = 0;
        itemId = 0;
        if (payload.Length < 16)
        {
            return false;
        }

        category = BinaryPrimitives.ReadInt32LittleEndian(payload);
        listingIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
        quantity = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8));
        itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12));
        return category is >= 0 and <= byte.MaxValue &&
            listingIndex >= 0 &&
            quantity is >= 1 and <= 99 &&
            itemId != 0;
    }
    /// <summary>
    /// Answers the mall npc's function menu and opens its window.
    /// </summary>
    /// <remarks>
    /// Replayed from the reference capture of Athens_074 (npc 5212). The client
    /// asks for dialog index 16 first, the server answers the sub-menu 101/201,
    /// and choosing 101 makes the server send the four window frames keyed by
    /// window id 765. The same npc carries a second service at index 24 whose
    /// menu is 101/1/2 and whose first choice answers page 601.
    /// <para>
    /// The capture holds no purchase: the shopper browsed the mall and stopped.
    /// A choice inside the opened window is therefore consumed without inventing
    /// a balance or an item, exactly as the credit-exchange browser is.
    /// </para>
    /// </remarks>
    private async Task<bool> TryHandleMallFunctionActionAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (!TryResolveMapNpc(npcId, out var npc) ||
            !CapitalNpcServiceProtocol.TryResolve(npc, out var service) ||
            service != CapitalNpcServiceKind.Mall)
        {
            return false;
        }

        if (dialogIndex == CapitalNpcServiceProtocol.MallDialogIndex)
        {
            if (subId == CapitalNpcServiceProtocol.MallDialogIndex)
            {
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        [.. CapitalNpcServiceProtocol.MallMenu]),
                    cancellationToken,
                    "MallMenu");
                return true;
            }

            if (CapitalNpcServiceProtocol.MallMenu.Contains(subId))
            {
                foreach (var frame in PacketBuilder.MallWindow())
                {
                    await _session.SendAsync(
                        frame,
                        cancellationToken,
                        "MallWindow",
                        framed: false);
                }

                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        [.. CapitalNpcServiceProtocol.MallOpenPage]),
                    cancellationToken,
                    "MallOpenPage");

                Console.WriteLine(
                    $"[npc] mall window npc={npcId} key={npc.NpcKey} " +
                    $"sub={subId}");
                return true;
            }

            return true;
        }

        if (dialogIndex == CapitalNpcServiceProtocol.MallSecondDialogIndex)
        {
            if (subId == CapitalNpcServiceProtocol.MallSecondDialogIndex)
            {
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        [.. CapitalNpcServiceProtocol.MallSecondMenu]),
                    cancellationToken,
                    "MallSecondMenu");
                return true;
            }

            if (CapitalNpcServiceProtocol.MallSecondMenu.Contains(subId))
            {
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        [.. CapitalNpcServiceProtocol.MallSecondPage]),
                    cancellationToken,
                    "MallSecondPage");

                // The capture's page answer is followed 3.6 s later by the stock
                // list and the window owner frame, with no request in between.
                foreach (var frame in PacketBuilder.MallStock())
                {
                    await _session.SendAsync(
                        frame,
                        cancellationToken,
                        "MallStock",
                        framed: false);
                }

                return true;
            }

            Console.WriteLine(
                $"[npc] mall second service selection npc={npcId} " +
                $"key={npc.NpcKey} dialog={dialogIndex} sub={subId} " +
                "(no captured confirmation)");
            return true;
        }

        return true;
    }
}
