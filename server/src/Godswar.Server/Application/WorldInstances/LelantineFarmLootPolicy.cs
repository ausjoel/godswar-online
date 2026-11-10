using System.Security.Cryptography;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// FINALISE-PETS (the admin 2026-11-05): Cerberus, the farm boss, drops one level 1 or level 2 pet skill book half
/// of the time. The roll is done here, not in <c>monster_loot_rules</c>, because the live <c>drop.chance</c>
/// rate (x3) multiplies every table chance, which would turn a 50% table rule into 100%. The ordinary food and
/// capture-tool drops of the non-capturable farm dogs are plain loot tables (<c>template@42</c> rows, SQL in
/// Development/finalise-2026-11-05/pets/farm-dog-loot.sql).
/// </summary>
/// <remarks>
/// The IGG guide (acid=185) has the boss drop "Sprite's Precious Collection" (a random level 3 book) and elites
/// "Sprite's Common Collection" (level 2); those collections have no use handler on this server (item sweep:
/// no_handler), so the book is dropped directly. The 50% and the book pool are the admin's / GENERATED: the pool is
/// every level I and II book whose text says "Available to all pets", plus Guard I/II (the Loyal Dog's own
/// skill per the GodsArena wiki pet table). Deterministic in the death event, so a replayed kill drops the same.
/// </remarks>
internal static class LelantineFarmLootPolicy
{
    public const string CerberusTemplateKey = "B_boss_wolf_006";

    /// <summary>The loot index the code-rolled book uses (tables use 0..31; farm tables keep 31 free).</summary>
    public const int CerberusBookLootIndex = 31;

    public const int CerberusBookChanceBasisPoints = 5_000;

    /// <summary>Level I and II pet skill books "available to all pets", plus Guard I/II.</summary>
    public static IReadOnlyList<uint> CerberusBookPool { get; } =
    [
        // Level I
        10200, 10202, 10203, 10206, 10208, 10211, 10212, 10214, 10215, 10216, 10217, 10218, 10219, 10220, 10221,
        10222, 10223, 10224, 10226, 10227, 10228, 10229, 10230, 10231,
        // Level II
        10233, 10237, 10239, 10245, 10249, 10255, 10257, 10261, 10263, 10265, 10267, 10269, 10271, 10273, 10275,
        10277, 10281, 10283, 10285, 10287, 10289, 10291
    ];

    public static bool IsCerberus(int mapId, string? templateKey) =>
        mapId == LelantineFarmProtocol.MapId &&
        string.Equals(templateKey, CerberusTemplateKey, StringComparison.Ordinal);

    /// <summary>Half of all Cerberus deaths drop one book from the pool.</summary>
    public static bool TryRollCerberusBook(Guid deathEventId, out uint itemId)
    {
        itemId = 0;
        if (deathEventId == Guid.Empty)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[16 + 14];
        deathEventId.TryWriteBytes(input[..16]);
        "cerberus-book"u8.CopyTo(input[16..]);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        if (BitConverter.ToUInt32(hash) % 10_000 >= CerberusBookChanceBasisPoints)
        {
            return false;
        }

        itemId = CerberusBookPool[(int)(BitConverter.ToUInt32(hash[4..]) % (uint)CerberusBookPool.Count)];
        return true;
    }
}
