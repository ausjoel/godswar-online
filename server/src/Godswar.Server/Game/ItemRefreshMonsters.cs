using Godswar.Server.Application.World;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>One RefreshCoordinate row of an IRM: where (client region + x/z, or beside the player) and which
/// MonsterInfo templates (one spawn per name, so a row can hold several monsters).</summary>
internal readonly record struct ItemRefreshPoint(
    short Region,
    int X,
    int Z,
    bool AtPlayer,
    int Range,
    bool Placard,
    int NoteType,
    string[] Templates);

/// <summary>One client ItemRefreshMonster row (ItemDynamicAttribute.xml).</summary>
internal sealed record ItemRefreshRow(int Id, ItemRefreshPoint[] Points)
{
    public int MonsterCount => Points.Sum(static point => point.Templates.Length);
}

/// <summary>One monster an IRM row puts on the map: its template and where it stands.</summary>
internal readonly record struct ItemRefreshSpawn(GameplayMonsterTemplateDefinition Template, float X, float Z);

/// <summary>
/// ROUND3 summons (2026-11-09): the client's ItemRefreshMonster table (ItemDynamicAttribute.xml) for the items
/// that summon monsters. What the file says (rows 30000-35505, generated in ItemRefreshMonsters.Generated.cs):
/// <list type="bullet">
/// <item>30000 / 30001 = 5 x "Summoned Athenian Soldier" / 3 x "Summoned Athenian Mage" beside the player;
/// 31000 / 31001 = the Spartan ones. They match the Totem Pole texts 4473-4476 (the Magic.ini names of skills
/// 5504 / 5505 are swapped against the items that use them; the item texts and the IRM rows agree).</item>
/// <item>32000-32056 = one monster beside the player (NoteType 1: Treasure Guardian, Treasure Elf, Trial
/// monsters, Ladon/Cyclops/Medusa, ...). Three rows name templates no client Monster.ini lists
/// (A_eliteQ_CentaurWarrior_001, A_eliteQ_Cyclops_001, A_eliteQ_PersianGen_001: rows 32030-32032); they are never picked.</item>
/// <item>33000-35505 = 6 rows for each high-level region 100, 101, 102, 103, 104, 152, each 5 boss points at
/// fixed coordinates (NoteType 2, Range 6); row 35505 has a Feltree with 4 underlings at one point (9 monsters).</item>
/// </list>
/// The file does not say which dig uses which row. The 36 high-region Places (12000-12505, 6 per region) line up
/// one to one with the 36 high rows, so Place 12mk (m = region index, k = 0..5) uses row 33000 + 500 m + k
/// (inferred from the numbering). Every other Place uses one 32xxx row at random (GENERATED).
/// </summary>
internal static partial class ItemRefreshMonsters
{
    public const int AthensSoldierTotem = 4473;
    public const int AthensMageTotem = 4474;
    public const int SpartaSoldierTotem = 4475;
    public const int SpartaMageTotem = 4476;

    private static Dictionary<int, ItemRefreshRow>? _byId;

    // Lazy: the rows live in the generated partial file (static initialisers across partial files run in no set order).
    private static Dictionary<int, ItemRefreshRow> ById => _byId ??= GeneratedRows.ToDictionary(static row => row.Id);

    public static IReadOnlyCollection<ItemRefreshRow> Rows => ById.Values;

    public static bool TryGet(int id, out ItemRefreshRow row) => ById.TryGetValue(id, out row!);

    // ---- Totem Poles ---------------------------------------------------------------------------------------

    /// <summary>The IRM row and faction of a Totem Pole (ItemBaseAttribute Camp: 4473/4474 = 1 Athens, 4475/4476 = 0 Sparta).</summary>
    public static bool TryGetTotem(int itemId, out int rowId, out byte camp)
    {
        (rowId, camp) = itemId switch
        {
            AthensSoldierTotem => (30000, (byte)1),
            AthensMageTotem => (30001, (byte)1),
            SpartaSoldierTotem => (31000, (byte)0),
            SpartaMageTotem => (31001, (byte)0),
            _ => (0, (byte)0)
        };
        return rowId != 0;
    }

    /// <summary>Item text: "to aid you for 60 seconds".</summary>
    public static readonly TimeSpan TotemLifetime = TimeSpan.FromSeconds(60);

    /// <summary>GENERATED: one Totem Pole's summons at a time (5 soldiers).</summary>
    public const int TotemOwnerLimit = 5;

    /// <summary>GENERATED: "only effective in face of players from opposing faction" = the totem is refused (and
    /// kept) unless a living enemy player is this close.</summary>
    public const float TotemEnemyRadius = 30f;

    // ---- Treasure-map guardians ------------------------------------------------------------------------------

    /// <summary>GENERATED: how long an untouched guardian beside the digger stays.</summary>
    public static readonly TimeSpan GuardianLifetime = TimeSpan.FromMinutes(10);

    /// <summary>GENERATED: the high-region boss set stands at fixed points, possibly across the map.</summary>
    public static readonly TimeSpan BossSetLifetime = TimeSpan.FromMinutes(30);

    /// <summary>GENERATED: live treasure spawns per digger and map (the largest set is 9).</summary>
    public const int TreasureOwnerLimit = 10;

    /// <summary>GENERATED: how far from the player "beside the player" spawns stand.</summary>
    public const float BesidePlayerRadius = 2.5f;

    /// <summary>The IRM row a treasure dig at <paramref name="placeId"/> uses, or null for none.</summary>
    public static ItemRefreshRow? DigRow(int placeId, Func<ItemRefreshRow, bool> drawable, Random random)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        ArgumentNullException.ThrowIfNull(random);
        if (placeId is >= 12000 and <= 12599)
        {
            var region = (placeId - 12000) / 100;
            var index = placeId % 100;
            return region is >= 0 and <= 5 && index is >= 0 and <= 5 &&
                   TryGet(33000 + 500 * region + index, out var high) && drawable(high)
                ? high
                : null;
        }

        var beside = Rows.Where(static row => row.Id is >= 32000 and <= 32056).Where(drawable).ToArray();
        return beside.Length == 0 ? null : beside[random.Next(beside.Length)];
    }

    // ---- Shared ----------------------------------------------------------------------------------------------

    /// <summary>The server map id of a client region id (MapIdToNameConfig.ini, MapTemplateSeed.ClientSceneId).</summary>
    public static short? ServerMapForRegion(int region)
    {
        foreach (var map in MapTemplateSeeds.Maps)
        {
            if (map.ClientSceneId == region)
            {
                return map.MapId;
            }
        }
        return null;
    }

    /// <summary>A template the client can draw on <paramref name="mapId"/>: the map's own, else a global one.</summary>
    public static GameplayMonsterTemplateDefinition? ResolveTemplate(string templateKey,
        IReadOnlyList<GameplayMonsterTemplateDefinition> templates, short mapId) =>
        templates.FirstOrDefault(template => template.SourceMapId == mapId &&
            string.Equals(template.TemplateKey, templateKey, StringComparison.Ordinal)) ??
        templates.FirstOrDefault(template => template.SourceMapId is null &&
            string.Equals(template.TemplateKey, templateKey, StringComparison.Ordinal));

    /// <summary>True when every template of the row resolves on <paramref name="mapId"/>.</summary>
    public static bool IsDrawable(ItemRefreshRow row, IReadOnlyList<GameplayMonsterTemplateDefinition> templates,
        short mapId) =>
        row.Points.All(point => point.Templates.All(key => ResolveTemplate(key, templates, mapId) is not null));

    /// <summary>
    /// The monsters of one row on <paramref name="mapId"/>, or null when a point belongs to another map or a
    /// template cannot be drawn. Beside-the-player points stand on a ring around the player; fixed points stand
    /// within their Range of the coordinate (GENERATED spread, so a multi-monster point does not stack).
    /// </summary>
    public static IReadOnlyList<ItemRefreshSpawn>? Expand(ItemRefreshRow row,
        IReadOnlyList<GameplayMonsterTemplateDefinition> templates, short mapId, float playerX, float playerZ,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(random);
        var spawns = new List<ItemRefreshSpawn>(row.MonsterCount);
        foreach (var point in row.Points)
        {
            if (point.Region >= 0 && ServerMapForRegion(point.Region) != mapId)
            {
                return null;
            }
            for (var index = 0; index < point.Templates.Length; index++)
            {
                if (ResolveTemplate(point.Templates[index], templates, mapId) is not { } template)
                {
                    return null;
                }
                float x, z;
                if (point.AtPlayer || point.X == -1 && point.Z == -1)
                {
                    var angle = 2 * Math.PI * index / point.Templates.Length;
                    x = playerX + BesidePlayerRadius * (float)Math.Cos(angle);
                    z = playerZ + BesidePlayerRadius * (float)Math.Sin(angle);
                }
                else
                {
                    var spread = point.Templates.Length == 1 ? 0 : Math.Max(point.Range, 1) * random.NextDouble();
                    var angle = 2 * Math.PI * random.NextDouble();
                    x = point.X + (float)(spread * Math.Cos(angle));
                    z = point.Z + (float)(spread * Math.Sin(angle));
                }
                spawns.Add(new ItemRefreshSpawn(template, x, z));
            }
        }
        return spawns;
    }

    /// <summary>GENERATED stats by template rank: tier = the player's level (at least <paramref name="minimumTier"/>).</summary>
    public static (uint Level, uint MaximumHealth) Stats(GameplayMonsterTemplateDefinition template, int playerLevel,
        int minimumTier = 1)
    {
        var level = (uint)Math.Clamp(playerLevel, Math.Clamp(minimumTier, 1, 200), 200);
        var health = template.IsBoss ? 500u * level + 5_000u
            : template.IsElite ? 100u * level + 1_000u
            : 30u * level + 300u;
        return (level, health);
    }
}
