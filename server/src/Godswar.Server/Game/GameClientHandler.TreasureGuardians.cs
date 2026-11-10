using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Game;

/// <summary>
/// ROUND3 summons (2026-11-09): treasure-map guardians. After a dig has paid out (#87's flow, unchanged), the
/// dig's ItemRefreshMonster row puts its monsters on the map through the runtime spawn service
/// (<see cref="ItemRefreshMonsters"/>):
/// <list type="bullet">
/// <item>a Place on a starting or mid map: one 32xxx row (one monster, sometimes 2 or 5 of one kind) beside the
/// digger;</item>
/// <item>a Place on a high-level map (regions 100-104, 152): its 33000-35505 row, five boss points (nine monsters
/// for 35505) at the row's fixed coordinates on that map.</item>
/// </list>
/// All GENERATED: owner-only (the digger's own fight), 10 minutes beside the digger / 30 minutes for a boss set,
/// tier = the digger's level, HP by rank, ordinary kill EXP and no corpse loot (the treasure was paid at the dig).
/// A guardian that cannot appear (map limit, the digger's earlier guardians still alive) never undoes the dig:
/// one log line, nothing else. Switch: item.treasure_guardians (default 1); 0 = digs give loot only, as before.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task TrySpawnTreasureGuardiansAsync(int placeId, CancellationToken cancellationToken)
    {
        if (!Finalise2Rates.On(Finalise2RateKeys.TreasureGuardians) || _character is not { } character)
        {
            return;
        }

        var mapId = (short)character.CurrentMap;
        var templates = _gameplayCatalogs.Content.MonsterTemplates;
        var row = ItemRefreshMonsters.DigRow(placeId,
            candidate => ItemRefreshMonsters.IsDrawable(candidate, templates, mapId), Random.Shared);
        var spawns = row is null
            ? null
            : ItemRefreshMonsters.Expand(row, templates, mapId, character.PositionX, character.PositionZ, Random.Shared);
        if (row is null || spawns is null || spawns.Count == 0)
        {
            Console.WriteLine($"[treasure-guardian] none character={character.Name} place={placeId} map={mapId} " +
                $"row={row?.Id.ToString() ?? "-"}");
            return;
        }

        var bossSet = row.Id >= 33000;
        var spec = new WorldRuntimeSpawnSpec(character.Id, OwnerOnly: true,
            bossSet ? ItemRefreshMonsters.BossSetLifetime : ItemRefreshMonsters.GuardianLifetime,
            [], $"treasure:{row.Id}", ItemRefreshMonsters.TreasureOwnerLimit);
        var blueprints = spawns.Select(spawn =>
        {
            var (level, health) = ItemRefreshMonsters.Stats(spawn.Template, character.Level);
            return new WorldRuntimeSpawnBlueprint(spawn.Template, spawn.X, spawn.Z, 1f, level, health, spec);
        }).ToArray();

        var now = DateTimeOffset.UtcNow;
        var refusal = "";
        var attached = _registry.TryStageWorldRuntimeSpawnGroup(_session, blueprints, now, out var ticket, out refusal) &&
                       _registry.TryAttachWorldRuntimeSpawnGroup(_session, ticket, now, out refusal);
        Console.WriteLine($"[treasure-guardian] character={character.Name} place={placeId} map={mapId} row={row.Id} " +
            $"monsters={blueprints.Length} attached={attached} refusal={refusal}");
        if (!attached)
        {
            return;
        }

        var name = spawns[0].Template.DisplayName;
        await SendInfoLineAsync(bossSet
                ? $"The treasure woke {spawns.Count} guardians. They wait at their lairs on this map for 30 minutes."
                : spawns.Count == 1
                    ? $"A {name} guards the treasure!"
                    : $"{spawns.Count} {name}s guard the treasure!",
            InfoTabOther, cancellationToken);
    }
}
