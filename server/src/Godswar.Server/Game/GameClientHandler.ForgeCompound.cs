using System.Buffers.Binary;
using System.Diagnostics;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// FORGEGEMS: the forge window's "Gems" (tab 1) and "Comp" (tab 2) tabs.
/// </summary>
/// <remarks>
/// Wire shapes, from the disassembly of the 2.51 client (GodsWar.exe in
/// GodsWar.app, md5 6ab4cb5a...; same code as Origin.exe) and the 2008
/// MsgDefine.h structs. No captured frame exists for tabs 1 and 2, so these
/// are UNPROVEN in a real client:
/// <list type="bullet">
/// <item>C2S 10110, 56-byte payload = MSG_EQUIPFORGE_EQUIP (already parsed by
/// <see cref="ForgeItemSelectionPacket"/>): bag page, page slot, forge slot,
/// tab (+12), then the item descriptor. Tab 1: forge slots 0-5 are the six gem
/// boxes (0x55ea40). The "Fill" button (0x55f594) sends six frames, slots 0-5,
/// all with the same bag coordinates. Tab 2: forge slot 0 is the main gear,
/// 1-5 the other boxes (0x55e980); the client's Start check (0x4238d0) needs
/// 0, 3, 4 and 5.</item>
/// <item>C2S 10109 Start, 36-byte payload: only +4 (the tab) is written
/// (0x55ff0f); the rest is stack data.</item>
/// <item>S2C 10109, see <see cref="PacketBuilder.ForgeCompoundResult"/>. Every
/// Start gets exactly one 10109 reply: the client keeps its forge-wait flag
/// (0x55ff48) until one arrives.</item>
/// </list>
/// GODSWAR_FORGE_GEM_COMPOUND=0 / GODSWAR_FORGE_EQUIP_COMPOUND=0 turn a tab
/// off; its frames then take the old "ignored" path.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private const int CompoundGemTab = 1;
    private const int CompoundEquipmentTab = 2;
    private const int CompoundForgeSlotCount = 6;

    private static readonly bool ForgeGemCompoundEnabled =
        Environment.GetEnvironmentVariable("GODSWAR_FORGE_GEM_COMPOUND") == "1"; // RELEASE: opt-in

    private static readonly bool ForgeEquipmentCompoundEnabled =
        Environment.GetEnvironmentVariable("GODSWAR_FORGE_EQUIP_COMPOUND") == "1"; // RELEASE: opt-in

    private readonly ForgeCompoundSelection?[] _compoundSlots =
        new ForgeCompoundSelection?[CompoundForgeSlotCount];
    private int _compoundTab;
    private int? _compoundCharacterId;
    private long _compoundStartedTimestamp;

    internal static bool IsForgeCompoundTabEnabled(int tab) =>
        (tab == CompoundGemTab && ForgeGemCompoundEnabled) ||
        (tab == CompoundEquipmentTab && ForgeEquipmentCompoundEnabled);

    private void ClearForgeCompoundSelection()
    {
        Array.Clear(_compoundSlots);
        _compoundTab = 0;
        _compoundCharacterId = null;
        _compoundStartedTimestamp = 0;
    }

    /// <summary>Returns true when the 10110 frame belonged to tab 1 or 2.</summary>
    private bool TryHandleForgeCompoundSelection(GamePacket packet)
    {
        if (!ForgeItemSelectionPacket.TryParse(packet.Payload, out var selection) ||
            !IsForgeCompoundTabEnabled(selection.RequestMode))
        {
            return false;
        }

        if (_account is null || _character is null)
        {
            return true;
        }

        if (selection.IsOddsMaterialIncrement)
        {
            // The client may emit its crystal "+1" frame after a gem lands in
            // box 5 (0x55fd16 does not check the tab). Nothing to stage.
            return true;
        }

        if (selection.DestinationSlot is < 0 or >= CompoundForgeSlotCount)
        {
            Console.WriteLine(
                $"[forge-compound] ignored destination={selection.DestinationSlot} tab={selection.RequestMode} character={_character.Name}");
            return true;
        }

        var bagSlot = selection.KitBagSlot;
        var item = KitBagSlots.GetItem(_character.KitBag, bagSlot);
        if (!selection.Matches(item))
        {
            Console.WriteLine(
                $"[forge-compound] ignored stale selection character={_character.Name} slot={bagSlot} item={selection.ItemId}");
            return true;
        }

        if (_compoundTab != selection.RequestMode ||
            _compoundCharacterId != _character.Id ||
            IsForgeCompoundSelectionExpired())
        {
            ClearForgeCompoundSelection();
            _compoundTab = selection.RequestMode;
            _compoundCharacterId = _character.Id;
        }

        if (selection.RequestMode == CompoundGemTab)
        {
            if (!ForgingMaterialRuleCatalog.TryGet(item.Id, out var rule) ||
                !ForgeCompoundPlanner.IsCompoundableMaterialType(rule.MaterialType) ||
                rule.NextItemId is null)
            {
                Console.WriteLine(
                    $"[forge-compound] ignored non-compoundable gem character={_character.Name} item={item.Id}");
                return true;
            }

            var reserved = 0;
            for (var slot = 0; slot < CompoundForgeSlotCount; slot++)
            {
                if (slot != selection.DestinationSlot &&
                    _compoundSlots[slot] is { } other &&
                    other.KitBagSlot == bagSlot)
                {
                    reserved++;
                }
            }

            if (reserved + 1 > item.Stack)
            {
                Console.WriteLine(
                    $"[forge-compound] ignored gem beyond stack character={_character.Name} slot={bagSlot} stack={item.Stack} reserved={reserved}");
                return true;
            }
        }
        else
        {
            if (!RequireItemContent().Templates.TryGet(item.Id, out var template) ||
                !ForgeCompoundPlanner.IsEquipmentKind(template.Kind))
            {
                Console.WriteLine(
                    $"[forge-compound] ignored non-equipment character={_character.Name} item={item.Id}");
                return true;
            }

            // One bag item can sit in one box only; the newest box wins.
            for (var slot = 0; slot < CompoundForgeSlotCount; slot++)
            {
                if (_compoundSlots[slot] is { } other && other.KitBagSlot == bagSlot)
                {
                    _compoundSlots[slot] = null;
                }
            }
        }

        _compoundSlots[selection.DestinationSlot] = new ForgeCompoundSelection(bagSlot, item);
        _compoundStartedTimestamp = Stopwatch.GetTimestamp();
        Console.WriteLine(
            $"[forge-compound] staged character={_character.Name} tab={selection.RequestMode} box={selection.DestinationSlot} bagSlot={bagSlot} item={item.Id}");
        return true;
    }

    /// <summary>Returns true when the 10109 Start belonged to tab 1 or 2.</summary>
    private async Task<bool> TryHandleForgeCompoundStartAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        const int expectedPayloadLength = 36;
        if (packet.Payload.Length != expectedPayloadLength)
        {
            return false;
        }

        var tab = BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.Slice(4, 4));
        if (!IsForgeCompoundTabEnabled(tab))
        {
            return false;
        }

        if (_account is null || _character is null)
        {
            ClearForgeCompoundSelection();
            await SendForgeCompoundRefusalAsync(refreshBag: false, cancellationToken);
            return true;
        }

        var staged = (ForgeCompoundSelection?[])_compoundSlots.Clone();
        var stagedTab = _compoundTab;
        var current = _compoundCharacterId == _character.Id && !IsForgeCompoundSelectionExpired();
        // Clear before any await so a repeated Start can never spend twice.
        ClearForgeCompoundSelection();

        string? refusal = null;
        if (!current || stagedTab != tab)
        {
            refusal = "nothing staged for this tab";
        }
        else if (tab == CompoundGemTab && staged.Any(static slot => slot is null))
        {
            refusal = "fewer than six gems staged";
        }
        else if (tab == CompoundEquipmentTab &&
                 (staged[0] is null || staged[3] is null || staged[4] is null || staged[5] is null))
        {
            refusal = "main gear and three supporting items are required";
        }
        else if (tab == CompoundEquipmentTab && (staged[1] is not null || staged[2] is not null))
        {
            // UNPROVEN client boxes 1 and 2: the guide and the 2008 code use
            // exactly three supporting items, so extra boxes are refused rather
            // than silently consumed.
            refusal = "only three supporting items are allowed";
        }
        else if (_session.IsSecure)
        {
            refusal = "secure session (no durable command yet)";
        }
        else if (!AllowLegacyPlayerMutationFallback("forge_compound"))
        {
            refusal = "legacy mutation fallback disabled";
        }

        if (refusal is not null)
        {
            Console.WriteLine(
                $"[forge-compound] rejected start tab={tab} character={_character.Name}: {refusal}");
            await SendForgeCompoundRefusalAsync(refreshBag: true, cancellationToken);
            return true;
        }

        ForgeCompoundResult result;
        try
        {
            result = tab == CompoundGemTab
                ? await _store.CompoundGemsAsync(
                    _account.Id,
                    _character.Id,
                    staged.Select(static slot => slot!.Value).ToArray(),
                    cancellationToken)
                : await _store.CompoundEquipmentAsync(
                    _account.Id,
                    _character.Id,
                    staged[0]!.Value,
                    [staged[3]!.Value, staged[4]!.Value, staged[5]!.Value],
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[forge-compound] persistence failure account={_account.Id} character={_character.Name}: {ex.Message}");
            await SendForgeCompoundRefusalAsync(refreshBag: true, cancellationToken);
            return true;
        }

        if (result.Status != ForgeCompoundStatus.Succeeded || result.Character is null)
        {
            Console.WriteLine(
                $"[forge-compound] rejected account={_account.Id} character={_character.Name} tab={tab} status={result.Status} reason={result.Reason}");
            await SendForgeCompoundRefusalAsync(refreshBag: true, cancellationToken);
            return true;
        }

        ApplyDurableForgeProjection(_character, result.Character);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await _session.SendAsync(
            PacketBuilder.ForgeCompoundResult(
                tab == CompoundGemTab
                    ? PacketBuilder.ForgeResultKindGemCompound
                    : PacketBuilder.ForgeResultKindEquipmentCompound,
                result.Result),
            cancellationToken,
            "ForgeCompoundResult");
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[forge-compound] committed account={_account.Id} character={_character.Name} tab={tab} result={result.Result.Id} quality={result.Result.Quality} grade={result.Result.Grade} attributes={result.Result.Attribute1},{result.Result.Attribute2},{result.Result.Attribute3},{result.Result.Attribute4},{result.Result.Attribute5} bound={result.Result.Bound}");
        return true;
    }

    private async Task SendForgeCompoundRefusalAsync(bool refreshBag, CancellationToken cancellationToken)
    {
        // Kind 0: "Failed!" and no local item change in the client.
        await _session.SendAsync(
            PacketBuilder.ForgeResult(success: false, resultKind: 0),
            cancellationToken,
            "ForgeCompoundRejected");
        if (refreshBag)
        {
            await SendKitBagRefreshAsync(cancellationToken);
        }
    }

    private bool IsForgeCompoundSelectionExpired() =>
        _compoundStartedTimestamp != 0 &&
        Stopwatch.GetElapsedTime(_compoundStartedTimestamp) > ForgeSelectionTtl;
}
