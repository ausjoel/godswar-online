using System.Buffers.Binary;
using System.Diagnostics;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Level 3 Amethyst transform, the "Trans" tab (tab 3) of the forge window.
/// </summary>
/// <remarks>
/// Wire shapes, all read from Origin.exe (identical to GodsWar.exe):
/// <list type="bullet">
/// <item>C2S 10111, 16 bytes (0x561110, 0x5612e0): page, page slot, window slot,
/// tab. Slot 0 is the gear, slot 1 the amethyst (both carry the dragged bag
/// page and slot), slot 2 is the preview pick and carries the source item id
/// and 0 instead. Tab is always 3.</item>
/// <item>C2S 10112, 40 bytes: the Transform button (0x55f4fa) sends it with no
/// field set; the window reset (0x5613e0) sends dword +4 = -1.</item>
/// <item>S2C 10112, 44 bytes (0x5614c0): the client reads only packet +12, the
/// new item id, and applies it only while gear, amethyst and preview are all
/// staged in the window.</item>
/// </list>
/// GODSWAR_AMETHYST_TRANSFORM=0 turns the feature off (replacement opcodes are
/// then ignored as before).
/// </remarks>
internal sealed partial class GameClientHandler
{
    private const int TransformTab = 3;
    private const int TransformGearSlot = 0;
    private const int TransformAmethystSlot = 1;
    private const int TransformPreviewSlot = 2;

    private static readonly bool AmethystTransformEnabled =
        Environment.GetEnvironmentVariable("GODSWAR_AMETHYST_TRANSFORM") != "0";

    private (int Slot, CompactItemEntry Item)? _transformGear;
    private (int Slot, CompactItemEntry Item)? _transformAmethyst;
    private bool _transformPreviewPicked;
    private uint _transformPreferredTarget; // FORGEGEMS: patched-client pick, 0 = random
    private long _transformStartedTimestamp;

    private void ClearAmethystTransformSelection()
    {
        _transformGear = null;
        _transformAmethyst = null;
        _transformPreviewPicked = false;
        _transformPreferredTarget = 0;
        _transformStartedTimestamp = 0;
    }

    private void HandleAmethystTransformSelection(GamePacket packet)
    {
        var payload = packet.Payload;
        if (_account is null || _character is null || payload.Length < 16)
        {
            Console.WriteLine($"[transform] ignored malformed selection len={packet.Length}");
            return;
        }

        var first = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var second = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4));
        var windowSlot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8));
        var tab = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12));
        if (tab != TransformTab)
        {
            Console.WriteLine(
                $"[transform] ignored selection for tab={tab} character={_character.Name}");
            return;
        }

        if (windowSlot == TransformPreviewSlot)
        {
            if (_transformGear is { } staged && staged.Item.Id == first)
            {
                _transformPreviewPicked = true;
                _transformPreferredTarget = EquipmentTransformPick.Enabled ? second : 0;
                _transformStartedTimestamp = Stopwatch.GetTimestamp();
                Console.WriteLine(
                    $"[transform] preview picked character={_character.Name} source={first} candidate={second}");
            }
            else
            {
                Console.WriteLine(
                    $"[transform] ignored preview pick character={_character.Name} source={first} staged={_transformGear?.Item.Id ?? 0}");
            }

            return;
        }

        if (first >= ForgeItemSelectionPacket.PageCount ||
            second >= ForgeItemSelectionPacket.SlotsPerPage)
        {
            Console.WriteLine(
                $"[transform] ignored selection with bad bag coordinates page={first} slot={second}");
            return;
        }

        var bagSlot = checked((int)(first * ForgeItemSelectionPacket.SlotsPerPage + second));
        var item = KitBagSlots.GetItem(_character.KitBag, bagSlot);
        if (windowSlot == TransformGearSlot)
        {
            if (item.IsEmpty || !EquipmentTransformCatalog.IsTransformable(item.Id))
            {
                Console.WriteLine(
                    $"[transform] ignored non-transformable gear character={_character.Name} bagSlot={bagSlot} item={item.Id}");
                return;
            }

            _transformGear = (bagSlot, item);
            _transformPreviewPicked = false;
            _transformPreferredTarget = 0;
        }
        else if (windowSlot == TransformAmethystSlot)
        {
            if (item.IsEmpty || item.Id != EquipmentTransformCatalog.LevelThreeAmethystItemId)
            {
                Console.WriteLine(
                    $"[transform] ignored non-amethyst character={_character.Name} bagSlot={bagSlot} item={item.Id}");
                return;
            }

            _transformAmethyst = (bagSlot, item);
        }
        else
        {
            Console.WriteLine($"[transform] ignored window slot={windowSlot}");
            return;
        }

        _transformStartedTimestamp = Stopwatch.GetTimestamp();
        Console.WriteLine(
            $"[transform] staged character={_character.Name} windowSlot={windowSlot} bagSlot={bagSlot} item={item.Id}");
    }

    private async Task HandleAmethystTransformActionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var payload = packet.Payload;
        if (_account is null || _character is null)
        {
            return;
        }

        if (payload.Length >= 8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)) == uint.MaxValue)
        {
            ClearAmethystTransformSelection();
            Console.WriteLine($"[transform] window reset character={_character.Name}");
            return;
        }

        // An incomplete press keeps the staging: the client window keeps its
        // slots too (the Transform button does not check them), so clearing
        // here would leave a window that looks ready but can never succeed.
        // There is no expiry either; the store re-checks both slots under lock.
        if (_transformGear is not { } gear ||
            _transformAmethyst is not { } amethyst ||
            !_transformPreviewPicked)
        {
            Console.WriteLine(
                $"[transform] rejected incomplete request character={_character.Name} gear={_transformGear?.Item.Id ?? 0} amethyst={_transformAmethyst?.Item.Id ?? 0} preview={_transformPreviewPicked}");
            return;
        }

        // Clear first so a repeated button press cannot spend twice.
        var preferredTarget = _transformPreferredTarget;
        ClearAmethystTransformSelection();
        if (_session.IsSecure)
        {
            Console.WriteLine("[transform] rejected secure session (no durable command yet)");
            return;
        }

        if (!AllowLegacyPlayerMutationFallback("amethyst_transform"))
        {
            return;
        }

        AmethystTransformResult result;
        try
        {
            result = await _store.TransformEquipmentWithAmethystAsync(
                _account.Id,
                _character.Id,
                gear.Slot,
                gear.Item,
                amethyst.Slot,
                amethyst.Item,
                cancellationToken,
                preferredTarget);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[transform] persistence failure account={_account.Id} character={_character.Name}: {ex.Message}");
            await SendKitBagRefreshAsync(cancellationToken);
            return;
        }

        if (result.Status != AmethystTransformStatus.Transformed || result.Character is null)
        {
            Console.WriteLine(
                $"[transform] rejected account={_account.Id} character={_character.Name} status={result.Status} gear={result.GearBefore.Id}");
            await SendKitBagRefreshAsync(cancellationToken);
            return;
        }

        ApplyDurableForgeProjection(_character, result.Character);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await _session.SendAsync(
            PacketBuilder.AmethystTransformResult(result.GearAfter.Id),
            cancellationToken,
            "AmethystTransformResult");
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[transform] committed account={_account.Id} character={_character.Name} gear={result.GearBefore.Id}->{result.GearAfter.Id} bagSlot={gear.Slot} quality={result.GearAfter.Quality} grade={result.GearAfter.Grade} bound={result.GearBefore.Bound}->{result.GearAfter.Bound} amethystSlot={amethyst.Slot}");
    }
}
