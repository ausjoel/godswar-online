using Godswar.Server.Application.Finalise2;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// ITEMS-HERMES (F-56): Hermes Hood (4130). Using it consumes one hood and, for 30 minutes, shows every other player
/// their own camp in this player's remote frames (green name; see <see cref="HermesHoods"/>). Nearby players get the
/// new presentation at once, and again when the hood runs out (also after a relog inside the 30 minutes).
/// No pop-up follows the used item; one gain-log line only (the admin's rule).
/// </summary>
internal sealed partial class GameClientHandler
{
    private CancellationTokenSource? _hermesHoodExpiry;

    private async Task<bool> UseHermesHoodAsync(CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + HermesHoods.Lifetime;
        var done = await RunEventsNpcAsync(async tx =>
        {
            if (!await tx.ConsumeAsync(HermesHoods.ItemId, 1)) { tx.Rollback(); return false; }
            await tx.ScalarAsync("""
                INSERT INTO public.hermes_hood_state (character_id, expires_at) VALUES (@c, @e)
                ON CONFLICT (character_id) DO UPDATE SET expires_at = EXCLUDED.expires_at;
                """, ("e", until));
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[hermes-hood] character={_character!.Name} done={done} until={until:O}");
        if (!done) { await SendKitBagRefreshAsync(cancellationToken); return true; }
        HermesHoods.Set(_character.Id, until);
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await BroadcastEquipmentRefreshAsync("hermes-hood", cancellationToken);
        ScheduleHermesHoodExpiry(cancellationToken);
        await SendGainLogAsync("Hermes Hood: other players see you as a companion for 30 minutes", cancellationToken);
        return true;
    }

    /// <summary>
    /// Map broadcast of a frame that carries this player's camp. Without an active hood it is the plain shared
    /// broadcast; with one, each recipient gets a frame built for its own camp.
    /// </summary>
    private Task<int> BroadcastCampAwareAsync(
        byte mapId,
        Func<byte?, byte[]> build,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        if (_character is null || !HermesHoods.IsActive(_character.Id))
        {
            return _registry.BroadcastToMapAsync(mapId, build(null), cancellationToken, _session, label, framed);
        }

        return _registry.BroadcastToMapPerRecipientAsync(
            mapId,
            recipient => build(recipient.Character.Camp),
            cancellationToken,
            _session,
            label,
            framed);
    }

    /// <summary>
    /// Per-recipient copies of a shared 0x2725 spawn frame while this player is hooded (each copy carries the
    /// recipient's camp in byte 53); null, the plain shared broadcast, otherwise.
    /// </summary>
    private Func<GameSessionContext, byte[]>? HermesHoodSpawnFrames(byte[] spawnPacket)
    {
        if (_character is not { } character || !HermesHoods.IsActive(character.Id))
        {
            return null;
        }

        return recipient => Packets.PacketBuilder.WithPlayerWorldSpawnCamp(
            spawnPacket,
            HermesHoods.ShownCamp(character, recipient.Character.Camp));
    }

    /// <summary>
    /// F-156 (the admin): the hood ends as soon as its wearer attacks another player (an accepted PvP basic attack, damage
    /// skill, area hit or control cast; training dummies do not count). The hood is cleared first, so the
    /// re-announce below carries the real camp to every viewer; the state row goes and one chat line says why.
    /// </summary>
    private async Task EndHermesHoodOnAttackAsync(GameCharacter? target, CancellationToken cancellationToken)
    {
        if (_character is not { } character || target is null || ReferenceEquals(target, character) ||
            _registry.IsTrainingDummy(target) || !HermesHoods.Clear(character.Id))
        {
            return;
        }

        _hermesHoodExpiry?.Cancel();
        _hermesHoodExpiry = null;
        var removed = await RunEventsNpcAsync(async tx =>
        {
            await tx.ScalarAsync("DELETE FROM public.hermes_hood_state WHERE character_id = @c;");
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[hermes-hood] ended by attack character={character.Name} target={target.Name} row={removed}");
        await BroadcastEquipmentRefreshAsync("hermes-hood-attack", cancellationToken);
        await SendGainLogAsync("Hermes Hood: removed because you attacked a player", cancellationToken);
    }

    /// <summary>When the hood runs out, re-announce this player so viewers drop the borrowed camp.</summary>
    private void ScheduleHermesHoodExpiry(CancellationToken cancellationToken)
    {
        if (_character is null || !HermesHoods.TryGetExpiry(_character.Id, out var until))
        {
            return;
        }

        _hermesHoodExpiry?.Cancel();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hermesHoodExpiry = lifetime;
        var characterId = _character.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                var wait = until - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, lifetime.Token);
                }

                await _characterStateGate.WaitAsync(lifetime.Token);
                try
                {
                    if (_character?.Id == characterId && !HermesHoods.TryGetExpiry(characterId, out _))
                    {
                        Console.WriteLine($"[hermes-hood] expired character={_character.Name}");
                        await BroadcastEquipmentRefreshAsync("hermes-hood-expired", lifetime.Token);
                    }
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[hermes-hood] expiry refresh failed: {error.GetType().Name}: {error.Message}");
            }
        });
    }
}
