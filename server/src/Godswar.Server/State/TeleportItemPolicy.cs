using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal readonly record struct TeleportItemTarget(int SkillId, byte MapId, float X, float Z);

/// <summary>
/// Portal scrolls: one use, consumed, moves the user to a fixed point.
/// </summary>
/// <remarks>
/// 4300-4307 are the points in the 2008 server's Skill.py (FlyToAthens1-4, FlyToSparta1-4). The later scrolls have no
/// script in any source, so they use the arrival point of the Transporter or walking portal into the map their item
/// text names (Development/teleport-audit). All points are walkable on the client's block plane.
/// Hermes Stone 4308 ("randomly within one map") uses RandomMapId and the generated RandomPoints.
/// </remarks>
internal static partial class TeleportItemPolicy
{
    internal const byte RandomMapId = 255;

    private static readonly FrozenDictionary<uint, TeleportItemTarget> ByItemId =
        new Dictionary<uint, TeleportItemTarget>
        {
            [4300] = new(3000, 1, 150f, -146f),
            [4301] = new(3001, 2, 98f, -178f),
            [4302] = new(3002, 11, 9f, -21f),
            [4303] = new(3003, 19, 141f, -138f),
            [4304] = new(3025, 0, 6f, -2f),
            [4305] = new(3026, 4, -108f, -122f),
            [4306] = new(3027, 13, 33f, 22f),
            [4307] = new(3028, 15, 203f, 150f),
            [4308] = new(3050, RandomMapId, 0f, 0f),
            [4311] = new(3031, 3, 192.17f, -36.15f),
            [4312] = new(3032, 18, 73.79f, -217.53f),
            [4313] = new(3033, 17, 56.56f, -225.3f),
            [4314] = new(3034, 16, -147.46f, -221.67f),
            [4315] = new(3035, 5, 105.69f, -220.98f),
            [4316] = new(3036, 15, 22.89f, 205.06f),
            [4317] = new(3037, 19, -17.18f, -209.03f),
            [4318] = new(3038, 17, 56.56f, -225.3f),
            [4319] = new(3039, 17, 41.01f, 202.2f),
            [4320] = new(3004, 12, 74.76f, -218.54f),
            [4321] = new(3029, 14, 218.09f, -28.7f),
            [4322] = new(3005, 8, 212.16f, -167.8f),
            [4323] = new(3030, 8, -131.19f, -183.86f),
            [4324] = new(5626, 20, 126.92f, -215.38f),
            [4325] = new(5627, 20, -190f, -3.8f),
            [4326] = new(5628, 22, 198.03f, -15.23f),
            [4327] = new(5629, 22, -199.41f, 118.88f),
            [4328] = new(5630, 21, 202.64f, 76.47f),
            [4329] = new(5631, 21, 202.64f, 76.47f)
        }.ToFrozenDictionary();

    internal static IEnumerable<KeyValuePair<uint, ProgressItemGrant>> ProgressGrants =>
        ByItemId.Select(static pair => KeyValuePair.Create(pair.Key,
            new ProgressItemGrant(pair.Value.SkillId, ProgressItemKind.Teleport, 1)));

    public static bool TryPickRandom(byte mapId, out float x, out float z)
    {
        x = z = 0f;
        if (!RandomPoints.TryGetValue(mapId, out var points)) return false;
        (x, z) = points[Random.Shared.Next(points.Length)];
        return true;
    }

    public static bool TryResolve(IItemTemplateCatalog templates, uint itemId, out TeleportItemTarget target) =>
        ByItemId.TryGetValue(itemId, out target) &&
        BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) && skill == target.SkillId;
}
