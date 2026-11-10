using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Answers the client's corpse click (C2S 10050, 16-byte payload) so the
    /// client opens that corpse's loot window and only then requests the drop.
    /// The installed client sends no pickup at all until this reply arrives,
    /// and the reply has to echo the clicked drop index: capture shows
    /// `{corpseObjectId, 0, dropIndex, 0}` answered by
    /// `{playerObjectId, corpseObjectId, dropIndex}`.
    /// </summary>
    private async Task<bool> TryHandleGroundLootSourceAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || packet.Payload.Length != 16)
        {
            return false;
        }

        var corpseObjectId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload);
        var dropIndex = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8));
        if (corpseObjectId == 0 ||
            !_registry.HasPendingMonsterLoot(
                _session,
                corpseObjectId,
                dropIndex))
        {
            return false;
        }

        MonsterLootTrace.Log(
            $"source-open character={_character.Name} corpse={corpseObjectId} dropIndex={dropIndex}");
        await _session.SendAsync(
            PacketBuilder.MonsterLootSourceOpen(
                LocalPlayerObjectId,
                corpseObjectId,
                dropIndex),
            cancellationToken,
            "MonsterLootSourceOpen");
        return true;
    }

    /// <summary>
    /// The installed client picks ground loot up on 10056 with the captured
    /// 40-byte bag/ground descriptor, not on 10048. Captured 2026-09-15: the
    /// request names the destination bag slot at +12/+16, the item at +20 and
    /// echoes the ground identity at +32/+36; the reference server answered by
    /// echoing the same descriptor with the ground identity it matched.
    /// A bag-to-equipment move uses the same opcode, so the pickup path only
    /// claims a request that names a free slot and matches a pending ground item.
    /// </summary>
    private async Task<bool> TryHandleGroundLootPickupAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        const int DescriptorLength = 40;
        if (_account is null || _character is null ||
            packet.Buffer.Length != DescriptorLength ||
            !TryReadGroundLootPickup(
                packet.Payload,
                out var slot,
                out var itemId,
                out var groundKeyHigh,
                out var groundKeyLow))
        {
            return false;
        }

        // The bag itself is deliberately not consulted before the reservation. A
        // second drop of the same item - the ordinary case for anything
        // stackable - names the slot that now already holds it, and reading that
        // as a bag-to-equipment move is exactly what dropped every pickup after
        // the first: the client showed the item, the server never wrote it, and
        // the next relog took it away. The request's handle word is traced but no
        // longer decides anything: a real pickup and the client's echo of a bag
        // record differ in it, yet the same client was captured sending both
        // shapes, so only the pending ground item settles whether this is a
        // pickup. An offer that names no pending ground item still falls through
        // to the move path below.
        var handle = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload);
        MonsterLootTrace.Log(
            $"pickup-request character={_character.Name} slot={slot} item={itemId} handle={handle:X8} key={groundKeyHigh:X8}:{groundKeyLow:X8}");
        if (!_registry.TryReserveGroundLootPickup(
                _session,
                itemId,
                groundKeyHigh,
                groundKeyLow,
                DateTimeOffset.UtcNow,
                out var reservation))
        {
            MonsterLootTrace.Log(
                $"pickup-unmatched character={_character.Name} slot={slot} item={itemId} handle={handle:X8} bag-has-item={MatchesCurrentKitBagItem(_character, slot, itemId)}");
            return false;
        }

        MonsterLootTrace.Log(
            $"pickup-reserved character={_character.Name} slot={slot} item={itemId} corpse={reservation.MonsterObjectId} quantity={reservation.Quantity}");

        return await CommitGroundLootPickupAsync(
            reservation,
            slot,
            packet.Buffer,
            cancellationToken);
    }

    /// <summary>
    /// Pays a ground item the client announced on opcode 10114.
    /// </summary>
    /// <remarks>
    /// The installed client announces a corpse pickup as a 52-byte 10114 whose
    /// payload names the destination bag slot at +4 and the item at +8 - and on
    /// this server it sends nothing else: the 40-byte 10056 descriptor it was
    /// captured sending in September only appears afterwards, as its echo of the
    /// bag records this server pushes back. Ignoring the announcement is why a
    /// picked-up item showed in the client's bag and was gone after the next
    /// relog. The ground item itself still decides: an announcement that names no
    /// pending drop is an inspection and changes nothing.
    /// </remarks>
    private async Task<bool> TryHandleAnnouncedGroundPickupAsync(
        int slot,
        uint itemId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return false;
        }

        MonsterLootTrace.Log(
            $"announce-request character={_character.Name} slot={slot} item={itemId}");
        if (!_registry.TryReserveGroundLootPickup(
                _session,
                itemId,
                0u,
                0u,
                DateTimeOffset.UtcNow,
                out var reservation))
        {
            MonsterLootTrace.Log(
                $"announce-unmatched character={_character.Name} slot={slot} item={itemId} bag-has-item={MatchesCurrentKitBagItem(_character, slot, itemId)}");
            return false;
        }

        MonsterLootTrace.Log(
            $"announce-reserved character={_character.Name} slot={slot} item={itemId} corpse={reservation.MonsterObjectId} quantity={reservation.Quantity}");

        // 10114 has no descriptor of its own to echo, so the acknowledgement is
        // the authoritative bag the pickup just changed.
        return await CommitGroundLootPickupAsync(
            reservation,
            slot,
            ackFrame: null,
            cancellationToken);
    }

    private async Task<bool> CommitGroundLootPickupAsync(
        MonsterLootPickupReservation reservation,
        int slot,
        byte[]? ackFrame,
        CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            var result = await _monsterRewardExtras.PickupMonsterLootAsync(
                _account!.Id,
                _character!.Id,
                reservation.DeathEventId,
                reservation.RuleLootIndex,
                reservation.ItemId,
                reservation.Quantity,
                reservation.BoundOnPickup,
                reservation.Attributes,
                cancellationToken);
            if (!result.Succeeded || result.Character is null ||
                !RevalidateCurrentWorldEffectOwnership("ground_loot_pickup"))
            {
                if (result.Status ==
                    MonsterLootPickupStatus.InsufficientCapacity)
                {
                    await SendKitBagRefreshAsync(cancellationToken);
                }
                return true;
            }

            InstallUpdatedCharacter(result.Character);
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);
            if (ackFrame is { } frame)
            {
                await _session.SendAsync(
                    BuildGroundLootPickupAck(frame, reservation),
                    cancellationToken,
                    "GroundLootPickupAck");
            }

            await SendKitBagRefreshAsync(cancellationToken);
            var nextPage = _registry.CompleteMonsterLootPickup(reservation, DateTimeOffset.UtcNow);
            completed = true;
            MonsterLootTrace.Log(
                $"picked character={_character.Name} item={reservation.ItemId} quantity={reservation.Quantity} slot={slot} corpse={reservation.MonsterObjectId}");
            await SendNextMonsterLootPageAsync(nextPage, cancellationToken);
        }
        finally
        {
            if (!completed)
            {
                _registry.ReleaseMonsterLootPickup(reservation);
            }
        }

        return true;
    }

    /// <summary>
    /// LOOT PAGING (rate drop.corpse_pages 2): after the pickup ack and bag refresh that emptied a corpse's page, the
    /// next page goes out as a fresh 10029 for the same corpse (the client clears and refills its drop list and shows
    /// the loot sparkle again). Null (always, with one page) sends nothing.
    /// </summary>
    private async Task SendNextMonsterLootPageAsync(
        MonsterLootPresentation? nextPage,
        CancellationToken cancellationToken)
    {
        if (nextPage is not { Entries.Count: > 0 } page)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.MonsterLoot(
                page.MonsterObjectId,
                page.DeathEventId,
                page.Entries),
            cancellationToken,
            "MonsterLootNextPage");
    }

    private static byte[] BuildGroundLootPickupAck(
        ReadOnlySpan<byte> requestPacket,
        MonsterLootPickupReservation reservation)
    {
        var response = requestPacket.ToArray();
        var groundKey = MonsterLootGroundKey.Resolve(
            reservation.DeathEventId,
            reservation.RuleLootIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(
            response.AsSpan(32, 4),
            groundKey.High);
        BinaryPrimitives.WriteUInt32LittleEndian(
            response.AsSpan(36, 4),
            groundKey.Low);
        return response;
    }

    private async Task HandleMonsterLootPickupAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null ||
            packet.Payload.Length != 12)
        {
            return;
        }

        var playerObjectId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload);
        var monsterObjectId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload.Slice(4));
        var pickupIndex = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8));
        if (playerObjectId != LocalPlayerObjectId &&
            playerObjectId != CurrentPlayerObjectId ||
            !_registry.TryReserveMonsterLootPickup(
                _session,
                monsterObjectId,
                pickupIndex,
                DateTimeOffset.UtcNow,
                out var reservation))
        {
            return;
        }

        var completed = false;
        try
        {
            var result = await _monsterRewardExtras.PickupMonsterLootAsync(
                _account.Id,
                _character.Id,
                reservation.DeathEventId,
                reservation.RuleLootIndex,
                reservation.ItemId,
                reservation.Quantity,
                reservation.BoundOnPickup,
                reservation.Attributes,
                cancellationToken);
            if (!result.Succeeded || result.Character is null ||
                !RevalidateCurrentWorldEffectOwnership(
                    "monster_loot_pickup"))
            {
                if (result.Status ==
                    MonsterLootPickupStatus.InsufficientCapacity)
                {
                    await SendKitBagRefreshAsync(cancellationToken);
                }
                return;
            }

            InstallUpdatedCharacter(result.Character);
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);
            await _session.SendAsync(
                PacketBuilder.MonsterLootPickup(
                    LocalPlayerObjectId,
                    monsterObjectId,
                    pickupIndex),
                cancellationToken,
                "MonsterLootPickup");
            await SendKitBagRefreshAsync(cancellationToken);
            var nextPage = _registry.CompleteMonsterLootPickup(reservation, DateTimeOffset.UtcNow);
            completed = true;
            Console.WriteLine(
                $"[loot] picked character={_character.Name} monster={monsterObjectId} item={reservation.ItemId} quantity={reservation.Quantity}");
            await SendNextMonsterLootPageAsync(nextPage, cancellationToken);
        }
        finally
        {
            if (!completed)
            {
                _registry.ReleaseMonsterLootPickup(reservation);
            }
        }
    }
}
