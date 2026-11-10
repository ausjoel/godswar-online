using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.World;

internal sealed record MonsterCombatBalanceDefinition(
    short MapId, string TemplateKey, int CriticalResistance);

/// <summary>Database balance rows are copied once; combat never queries PostgreSQL.</summary>
internal static class MonsterCombatBalanceContent
{
    public static ImmutableArray<MonsterCombatBalanceDefinition> Pin(
        IReadOnlyList<MonsterCombatBalanceDefinition> rows,
        IReadOnlyList<GameplayMonsterTemplateDefinition> templates)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(templates);
        if (rows.Count > 4096)
            throw new InvalidDataException("Monster combat balance exceeds 4096 rows.");
        var identities = templates.Where(t => t.SourceMapId.HasValue)
            .GroupBy(t => (Map: t.SourceMapId!.Value, t.TemplateKey))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var seen = new HashSet<(short, string)>();
        foreach (var row in rows)
        {
            if (row is null || row.MapId is < 0 or > 255 ||
                string.IsNullOrWhiteSpace(row.TemplateKey) || row.TemplateKey.Length > 128 ||
                row.CriticalResistance < 0 || !seen.Add((row.MapId, row.TemplateKey)) ||
                !identities.TryGetValue((row.MapId, row.TemplateKey), out var matching) ||
                matching.Length != 1 || !matching[0].IsBoss || matching[0].IsPet)
                throw new InvalidDataException(
                    "Monster combat balance requires unique published boss identities and nonnegative ratings.");
        }
        return rows.ToImmutableArray();
    }

    public static string CoordinationRevision(IReadOnlyList<MonsterCombatBalanceDefinition> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var canonical = new StringBuilder("monster-combat-balance-v1\n");
        foreach (var row in rows.OrderBy(row => row.MapId)
                     .ThenBy(row => row.TemplateKey, StringComparer.Ordinal))
        {
            canonical.Append(row.MapId.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(row.TemplateKey.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(row.TemplateKey).Append(':')
                .Append(row.CriticalResistance.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
