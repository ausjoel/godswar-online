using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Game;

/// <summary>
/// ROUND3 summons (2026-11-09): Summoning Totem Poles 4473-4476. Item text: "Right click to summon 5 Athenian
/// soldiers / 3 Athenian mages (Spartan for 4475 / 4476) to aid you for 60 seconds. Only effective in face of
/// players from opposing faction." The summons are the IRM rows 30000 / 30001 / 31000 / 31001
/// ("Summoned Athenian Soldier" A_eliteQ_greecewarrior_003, ...), placed around the user as allied runtime
/// spawns of the user's faction: packet byte 5 = the camp, so the client draws them as friendly, the PORTALS aggro
/// filter keeps them off their own faction and they fight only enemy players; the user and their faction cannot
/// hurt them, enemy players can (no loot). GENERATED: refused and kept unless a living enemy player the user may attack
/// (the basic-attack PvP gate: PvP map, safe zones) is within 30 units; tier = the user's level, at least 30 (monsters below 30 never start a fight); HP by rank; one Totem Pole
/// at a time. Switch: item.totem_poles (default 1); 0 = the items do nothing, as before. No pop-up.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryUseTotemPoleAsync(int itemId, CancellationToken cancellationToken)
    {
        if (!ItemRefreshMonsters.TryGetTotem(itemId, out var rowId, out var camp) ||
            !Finalise2Rates.On(Finalise2RateKeys.TotemPoles) || _character is not { } character)
        {
            return false;
        }

        var mapId = (short)character.CurrentMap;
        var templates = _gameplayCatalogs.Content.MonsterTemplates;
        string? refusal = null;
        IReadOnlyList<ItemRefreshSpawn>? spawns = null;
        if (character.Camp != camp)
        {
            refusal = "This Totem Pole belongs to the other faction.";
        }
        else if (_registry.CountAttackableEnemiesNear(_session, ItemRefreshMonsters.TotemEnemyRadius,
                     DateTimeOffset.UtcNow) == 0)
        {
            // Also covers maps and safe zones where PvP is off: the enemy must be one the user may attack.
            refusal = "The Totem Pole only answers when enemy players you can fight are near.";
        }
        else if (!ItemRefreshMonsters.TryGet(rowId, out var row) ||
                 (spawns = ItemRefreshMonsters.Expand(row, templates, mapId, character.PositionX, character.PositionZ,
                     Random.Shared)) is null)
        {
            refusal = "The Totem Pole cannot summon here.";
        }

        WorldRuntimeSpawnGroupTicket? ticket = null;
        var now = DateTimeOffset.UtcNow;
        if (refusal is null)
        {
            var spec = new WorldRuntimeSpawnSpec(character.Id, OwnerOnly: false, ItemRefreshMonsters.TotemLifetime, [],
                $"totem:{itemId}", ItemRefreshMonsters.TotemOwnerLimit, camp);
            var blueprints = spawns!.Select(spawn =>
            {
                var (level, health) = ItemRefreshMonsters.Stats(spawn.Template, character.Level,
                    (int)MonsterAggroPolicy.MinimumAggressiveTier);
                return new WorldRuntimeSpawnBlueprint(spawn.Template, spawn.X, spawn.Z, 1f, level, health, spec);
            }).ToArray();
            if (_registry.TryStageWorldRuntimeSpawnGroup(_session, blueprints, now, out var staged, out var reason))
            {
                ticket = staged;
            }
            else
            {
                refusal = reason switch
                {
                    "owner limit reached" => "Your summoned helpers are still here.",
                    "map limit reached" => "Too many summoned monsters on this map. Try again later.",
                    _ => "The Totem Pole cannot summon here."
                };
                Console.WriteLine($"[totem] staging refused character={character.Name} item={itemId} map={mapId} " +
                    $"reason={reason}");
            }
        }

        if (refusal is not null || ticket is null)
        {
            Console.WriteLine($"[totem] refused character={character.Name} item={itemId} map={mapId} reason={refusal}");
            await SendKitBagRefreshAsync(cancellationToken);
            await SendInfoLineAsync(refusal ?? "Nothing happens.", InfoTabOther, cancellationToken);
            return true;
        }

        var (done, attached) = await ConsumeAndAttachGroupAsync(itemId, ticket, now, cancellationToken);
        Console.WriteLine($"[totem] use character={character.Name} item={itemId} map={mapId} row={rowId} " +
            $"monsters={ticket.Stages.Count} done={done} attached={attached}");
        if (!done)
        {
            await SendKitBagRefreshAsync(cancellationToken);
            await SendInfoLineAsync("The Totem Pole cannot summon here.", InfoTabOther, cancellationToken);
            return true;
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendInfoLineAsync($"{ticket.Stages.Count} {ticket.Stages[0].Definition.DisplayName}s answer the " +
            "Totem Pole for 60 seconds!", InfoTabOther, cancellationToken);
        return true;
    }

    /// <summary>
    /// ROUND3 summons: takes one <paramref name="itemId"/> and attaches the staged group in one transaction. A
    /// refused attach rolls the item back; a failure after attaching removes the group again.
    /// </summary>
    private async Task<(bool Done, bool Attached)> ConsumeAndAttachGroupAsync(int itemId,
        WorldRuntimeSpawnGroupTicket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var attached = false;
        bool done;
        try
        {
            done = await RunEventsNpcAsync(async tx =>
            {
                if (!await tx.ConsumeAsync(itemId, 1))
                {
                    tx.Rollback();
                    return false;
                }
                attached = _registry.TryAttachWorldRuntimeSpawnGroup(_session, ticket, now, out _);
                if (!attached)
                {
                    tx.Rollback();
                    return false;
                }
                return true;
            }, false, cancellationToken);
        }
        catch
        {
            if (attached)
            {
                _registry.RemoveWorldRuntimeSpawnGroup(ticket, DateTimeOffset.UtcNow); // the item was not taken
            }
            throw;
        }

        if (!done && attached)
        {
            _registry.RemoveWorldRuntimeSpawnGroup(ticket, DateTimeOffset.UtcNow);
            attached = false;
        }
        return (done, attached);
    }
}
