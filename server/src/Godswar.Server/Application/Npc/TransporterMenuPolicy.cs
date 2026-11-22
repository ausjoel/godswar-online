using Godswar.Server.Application.Gates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Npc;

// B22 (the admin d32, 2026-11-21): editor-written teleporter menus (table public.npc_teleport_menu).
// Rows for an NPC replace the buttons the server sends for that NPC (order, hidden, added) and set a price per button.
// An NPC without rows keeps today's list (published menu + server-added buttons) and today's Mycenae fee, so an empty
// table changes nothing. The sealed NPC dialogue publication is not touched: only the list sent to the client and the
// lookup done when a button is clicked change.

/// <summary>B22: one editor row. Null price = the compiled fee (20,000 Silver on the capitals' Mycenae button, else free).</summary>
internal sealed record NpcTeleportMenuRow(
    string NpcKey,
    int Position,
    int SubId,
    bool Hidden,
    int? Price,
    string Currency,
    short? TargetMapId,
    float? X,
    float? Z,
    int? MinimumLevel,
    string? DisplayName);

/// <summary>B22: what one button costs. <see cref="CapturedMycenaeLine"/> = the client's own "You need 20,000 Silver!".</summary>
internal readonly record struct TeleportPrice(string Currency, int Amount, bool CapturedMycenaeLine)
{
    public static TeleportPrice Free { get; } = new(TransporterMenuPolicy.Silver, 0, false);
}

/// <summary>B22: where an added button goes when it is not a compiled destination (row map + X / Z).</summary>
internal sealed record TeleportMenuTarget(short MapId, float X, float Z);

internal static class TransporterMenuPolicy
{
    public const string Silver = "silver";
    public const string Gold = "gold";
    public const string BindingGold = "bgold";

    /// <summary>The client's NPC window has 12 buttons (NpcFun.xml FirstWin_Button1..12).</summary>
    public const int MaximumButtons = 12;

    /// <summary>
    /// Sub-ids reserved for editor-added destinations. NpcFunTranmit.lua (Index 1) has no branch or range for
    /// 1094-1105, so an older client draws no button for them; the client generator (P1/client) adds their labels.
    /// </summary>
    public const int FirstSlotSubId = 1094;
    public const int LastSlotSubId = 1105;

    public static bool IsCurrency(string currency) => currency is Silver or Gold or BindingGold;

    public static string CurrencyName(string currency) => currency switch
    {
        Gold => "Gold",
        BindingGold => "B-Gold",
        _ => "Silver"
    };

    /// <summary>The ordinary world transporters this table can change (not battlefield or duel-arena NPCs).</summary>
    public static bool IsEditableNpc(string npcKey) =>
        TransporterProtocol.Destinations.Any(d => string.Equals(d.NpcKey, npcKey, StringComparison.Ordinal));

    /// <summary>Today's list: the published menu, then the server-added buttons.</summary>
    public static int[] CompiledMenu(IEnumerable<int> publishedMenu, string npcKey, uint interactionId) =>
        publishedMenu.Concat(TransporterProtocol.GetExtensionMenu(npcKey, interactionId)).ToArray();

    public static IReadOnlyList<NpcTeleportMenuRow> RowsFor(NpcContentSnapshot snapshot, string npcKey) =>
        snapshot.TeleportMenus.TryGetValue(npcKey, out var rows) ? rows : [];

    /// <summary>The buttons to send: the compiled list without rows, else the visible rows that resolve, in order, at most 12.</summary>
    public static int[] EffectiveMenu(
        IEnumerable<int> publishedMenu,
        string npcKey,
        uint interactionId,
        short sourceMapId,
        NpcContentSnapshot snapshot)
    {
        var compiled = CompiledMenu(publishedMenu, npcKey, interactionId);
        var rows = RowsFor(snapshot, npcKey);
        if (rows.Count == 0)
        {
            return compiled;
        }

        return rows
            .Where(static row => !row.Hidden)
            .OrderBy(static row => row.Position)
            .ThenBy(static row => row.SubId)
            .Where(row => TryResolveRowDestination(npcKey, interactionId, sourceMapId, row, out _, out _))
            .Select(static row => row.SubId)
            .Distinct()
            .Take(MaximumButtons)
            .ToArray();
    }

    /// <summary>
    /// The destination and price of one clicked button. Without rows for the NPC this is exactly
    /// <see cref="TransporterProtocol.TryResolveDestination"/>; with rows only a visible row's sub-id resolves.
    /// </summary>
    public static bool TryResolve(
        string npcKey,
        uint interactionId,
        short sourceMapId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        NpcContentSnapshot snapshot,
        out TransporterDestination destination,
        out TeleportMenuTarget? rowTarget,
        out NpcTeleportMenuRow? row)
    {
        rowTarget = null;
        row = null;
        var rows = RowsFor(snapshot, npcKey);
        if (rows.Count == 0)
        {
            return TransporterProtocol.TryResolveDestination(
                npcKey, interactionId, sourceMapId, dialogIndex, subId, arguments, out destination);
        }

        destination = null!;
        // The packet shape checks of the compiled resolver (dialog index, empty path) still apply.
        if (dialogIndex != TransporterProtocol.DialogIndex || !HasEmptyPath(arguments))
        {
            return false;
        }

        row = rows.FirstOrDefault(r => r.SubId == subId && !r.Hidden);
        if (row is null)
        {
            return false;
        }

        return TryResolveRowDestination(npcKey, interactionId, sourceMapId, row, out destination, out rowTarget);
    }

    /// <summary>
    /// A row resolves to (1) this NPC's compiled destination for the sub-id, else (2) the same sub-id of another
    /// ordinary transporter (e.g. Olympus 1093 on a capital), else (3) its own map + X / Z.
    /// </summary>
    public static bool TryResolveRowDestination(
        string npcKey,
        uint interactionId,
        short sourceMapId,
        NpcTeleportMenuRow row,
        out TransporterDestination destination,
        out TeleportMenuTarget? rowTarget)
    {
        rowTarget = null;
        var empty = EmptyPath;
        if (TransporterProtocol.TryResolveDestination(
                npcKey, interactionId, sourceMapId, TransporterProtocol.DialogIndex, row.SubId, empty, out destination))
        {
            destination = WithRowLevel(destination, row);
            return true;
        }

        var borrowed = TransporterProtocol.Destinations.FirstOrDefault(d => d.SubId == row.SubId);
        var hasOwnTarget = row.TargetMapId is >= 0 and <= 255 && row.X is { } x && row.Z is { } z &&
            float.IsFinite(x) && float.IsFinite(z) && x is >= -256 and < 256 && z is > -256 and <= 256;
        if (hasOwnTarget)
        {
            // Own target wins over a borrowed one: the editor set it on purpose.
            rowTarget = new TeleportMenuTarget(row.TargetMapId!.Value, row.X!.Value, row.Z!.Value);
            destination = new TransporterDestination(
                npcKey,
                interactionId,
                sourceMapId,
                row.SubId,
                row.TargetMapId!.Value,
                borrowed?.ArrivalAnchorSourceMapId ?? sourceMapId,
                row.MinimumLevel ?? borrowed?.MinimumLevel ?? 1,
                TransporterProtocol.GenericLevelRequirementResultSubId,
                string.IsNullOrWhiteSpace(row.DisplayName) ? borrowed?.DisplayName ?? $"map {row.TargetMapId}" : row.DisplayName!);
            return true;
        }

        if (borrowed is not null)
        {
            destination = WithRowLevel(borrowed with
            {
                NpcKey = npcKey,
                InteractionId = interactionId,
                SourceMapId = sourceMapId,
                LevelRequirementResultSubId = TransporterProtocol.GenericLevelRequirementResultSubId
            }, row);
            return true;
        }

        destination = null!;
        return false;
    }

    /// <summary>
    /// The fee for a button. A row price replaces the compiled one (0 = free); no row or a null price keeps the
    /// compiled fee: 20,000 Silver on the capitals' Mycenae button, nothing elsewhere. GMs who skip the gate never pay.
    /// </summary>
    public static TeleportPrice Price(TransporterDestination destination, NpcTeleportMenuRow? row, bool gmSkips)
    {
        if (gmSkips)
        {
            return TeleportPrice.Free;
        }

        var compiled = GateCatalog.TransporterKey(destination.TargetMapId) == GateKeys.Mycenae && destination.TargetMapId == 6
            ? GateCatalog.MycenaeSilverFee
            : 0;
        if (row?.Price is not { } price)
        {
            return new TeleportPrice(Silver, compiled, compiled > 0);
        }

        var currency = IsCurrency(row.Currency) ? row.Currency : Silver;
        if (price <= 0)
        {
            return TeleportPrice.Free;
        }

        // The client's own line says "You need 20,000 Silver!", so it is only used when that is the truth.
        return new TeleportPrice(currency, price, currency == Silver && price == GateCatalog.MycenaeSilverFee && compiled > 0);
    }

    private static TransporterDestination WithRowLevel(TransporterDestination destination, NpcTeleportMenuRow row) =>
        row.MinimumLevel is { } level and >= 1 and <= 255
            ? destination with { MinimumLevel = level }
            : destination;

    private static readonly int[] EmptyPathValues = Enumerable.Repeat(-1, TransporterProtocol.FunctionArgumentCount).ToArray();

    private static IReadOnlyList<int> EmptyPath => EmptyPathValues;

    private static bool HasEmptyPath(IReadOnlyList<int> arguments) =>
        arguments.Count == TransporterProtocol.FunctionArgumentCount && arguments.All(static a => a == -1);
}
