using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Game;

/// <summary>
/// ROUND3 runtime spawns (the admin F-31 "Build both", 2026-11-09): seal boxes 14034-14037 and boss scrolls
/// 14060-14065 release a monster beside the player through the world-map runtime spawn service
/// (<see cref="WorldRuntimeSpawnOverlay"/>, Development/runtime-spawns/README.md). Order: every check and the
/// staged monster first, then the item is consumed and the monster attached inside one transaction (a refused
/// attach rolls the item back). No pop-up follows the use (the admin's rule): one info line says what happened.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryUseSummonItemAsync(int itemId, CancellationToken cancellationToken)
    {
        if (!SummonItemPolicy.TryGet(itemId, out var item) || _character is not { } character ||
            !Finalise2Rates.On(item.Kind == SummonItemKind.SealBox
                ? Finalise2RateKeys.SealBoxes
                : Finalise2RateKeys.BossScrolls))
        {
            return false;
        }

        var mapId = (short)character.CurrentMap;
        string? refusal = null;
        if (character.Level < item.MinimumLevel)
        {
            refusal = $"You must be level {item.MinimumLevel} or higher to use this.";
        }
        else if (item.Camp is { } camp && character.Camp != camp)
        {
            refusal = "This scroll belongs to the other faction.";
        }
        else if (item.Maps is { } maps && !maps.Contains(mapId))
        {
            refusal = $"This scroll can only be used in {item.MapText}.";
        }

        var choice = item.Choices[item.Choices.Count == 1 ? 0 : Random.Shared.Next(item.Choices.Count)];
        var template = refusal is null
            ? SummonItemPolicy.ResolveTemplate(choice, _gameplayCatalogs.Content.MonsterTemplates, mapId, item.Kind)
            : null;
        if (refusal is null && template is null)
        {
            refusal = $"The sealed {choice.Name} cannot be released here.";
        }

        WorldRuntimeSpawnTicket? ticket = null;
        var now = DateTimeOffset.UtcNow;
        if (refusal is null)
        {
            var (level, health) = SummonItemPolicy.Stats(item.Kind, character.Level);
            var offset = item.Kind == SummonItemKind.SealBox
                ? SummonItemPolicy.SealBoxOffset
                : SummonItemPolicy.BossScrollOffset;
            // ROUND3 summons (Sacred Sealer, F-27): with npc.sacred_sealer on, a scroll boss is the summoner's
            // own fight (the scroll was bought with Gold) and drops its Treasure Chest instead of the pills.
            var sealerChest = item.Kind == SummonItemKind.BossScroll && Finalise2Rates.On(Finalise2RateKeys.SacredSealer)
                ? SacredSealerPolicy.ChestFor(itemId)
                : 0;
            var spec = new WorldRuntimeSpawnSpec(
                character.Id,
                OwnerOnly: item.Kind == SummonItemKind.SealBox || sealerChest != 0,
                item.Kind == SummonItemKind.SealBox
                    ? SummonItemPolicy.SealBoxLifetime
                    : SummonItemPolicy.BossScrollLifetime,
                sealerChest != 0
                    ? [new WorldRuntimeSpawnDrop((uint)sealerChest, 1)]
                    : SummonItemPolicy.SplitStacks(SummonItemPolicy.ExperiencePill, choice.PillCount),
                $"item:{itemId}");
            var blueprint = new WorldRuntimeSpawnBlueprint(template!, character.PositionX + offset,
                character.PositionZ, 1f, level, health, spec);
            if (!_registry.TryStageWorldRuntimeSpawn(_session, blueprint, now, out var staged, out var reason))
            {
                refusal = reason switch
                {
                    "owner limit reached" => "You already have a released monster nearby. Defeat it first.",
                    "map limit reached" => "Too many released monsters on this map. Try again later.",
                    _ => $"The sealed {choice.Name} cannot be released here."
                };
                Console.WriteLine($"[summon-item] staging refused character={character.Name} item={itemId} " +
                    $"map={mapId} reason={reason}");
            }
            else
            {
                ticket = staged;
            }
        }

        if (refusal is not null || ticket is null)
        {
            Console.WriteLine($"[summon-item] refused character={character.Name} item={itemId} map={mapId} " +
                $"reason={refusal}");
            await SendKitBagRefreshAsync(cancellationToken);
            await SendInfoLineAsync(refusal ?? "Nothing happens.", InfoTabOther, cancellationToken);
            return true;
        }

        var attached = false;
        string attachRefusal = "";
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
                attached = _registry.TryAttachWorldRuntimeSpawn(_session, ticket, now, out attachRefusal);
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
                _registry.RemoveWorldRuntimeSpawn(ticket, DateTimeOffset.UtcNow); // the item was not taken
            }
            throw;
        }

        Console.WriteLine($"[summon-item] use character={character.Name} item={itemId} map={mapId} " +
            $"template={ticket.Stage.Definition.TemplateKey} object={ticket.Stage.Definition.ObjectId} " +
            $"done={done} attachRefusal={attachRefusal}");
        if (!done)
        {
            if (attached)
            {
                _registry.RemoveWorldRuntimeSpawn(ticket, DateTimeOffset.UtcNow);
            }
            await SendKitBagRefreshAsync(cancellationToken);
            if (attachRefusal.Length > 0)
            {
                await SendInfoLineAsync($"The sealed {choice.Name} cannot be released here.", InfoTabOther,
                    cancellationToken);
            }
            return true;
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendInfoLineAsync(item.Kind == SummonItemKind.SealBox
                ? $"The sealed {choice.Name} breaks free beside you!"
                : $"{choice.Name} has been summoned!", InfoTabOther, cancellationToken);
        return true;
    }
}
