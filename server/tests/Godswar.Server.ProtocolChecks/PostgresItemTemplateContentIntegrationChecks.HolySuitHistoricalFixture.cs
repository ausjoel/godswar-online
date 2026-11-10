using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    // Historical hashes cover the original names, texture coordinates and
    // seven-tier policies. Reconstruct those inputs instead of today's seed.
    private static async Task<ItemTemplateDefinition[]> RestoreLegacyHolySuitDefinitionsAsync(
        NpgsqlDataSource source, IEnumerable<ItemTemplateDefinition> definitions)
    {
        var currentIds = HolySuitContentBaseline.ItemTemplates.Select(item => checked((uint)item.Id)).ToHashSet();
        return (await RestoreLegacyHolyStoneReagentsAsync(source, definitions)).Where(item => !currentIds.Contains(item.Id))
            .Concat(await BuildLegacyHolySuitItemsAsync(source)).OrderBy(item => item.Id).ToArray();
    }

    private static async Task<ItemTemplateDefinition[]> RestoreLegacyHolyStoneReagentsAsync(
        NpgsqlDataSource source, IEnumerable<ItemTemplateDefinition> definitions)
    {
        var old = await BuildCanonicalHolySuitItemsAsync(source,
            HolyStoneMaterialItemContentBaseline.ItemTemplates.Where(item =>
                HolyStoneMaterialItemContentV3.ReagentIds.Contains(item.Id))
                .Concat(SocketSpellItemContentBaseline.ItemTemplates)
                .Concat(HolySuitContentBaselineV3.ItemTemplates.Where(item => item.Id == 9025)).ToArray());
        var byId = old.ToDictionary(item => item.Id);
        return definitions.Select(item => byId.GetValueOrDefault(item.Id, item)).OrderBy(item => item.Id).ToArray();
    }

    private static async Task<ItemTemplateDefinition[]> BuildLegacyHolySuitItemsAsync(NpgsqlDataSource source)
        => await BuildCanonicalHolySuitItemsAsync(source, HolySuitContentBaselineV1.ItemTemplates);

    private static async Task<ItemTemplateDefinition[]> BuildCanonicalHolySuitItemsAsync(
        NpgsqlDataSource source, IReadOnlyList<ItemTemplateSeed> reviewed)
    {
        var seeds = reviewed.OrderBy(item => item.Id).ToArray();
        await using var command = source.CreateCommand("""
            SELECT input.item_id, input.stats::jsonb::text
            FROM unnest(@ids, @stats) AS input(item_id, stats) ORDER BY input.item_id;
            """);
        AddArray(command, "ids", NpgsqlDbType.Integer, seeds.Select(item => item.Id).ToArray());
        AddArray(command, "stats", NpgsqlDbType.Text, seeds.Select(item => item.StatsJson).ToArray());
        var canonical = new Dictionary<int, string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) canonical.Add(reader.GetInt32(0), reader.GetString(1));
        Check.Equal(seeds.Length, canonical.Count, "all reviewed Holy Suit identities have canonical PostgreSQL JSON");
        return seeds.Select(seed => new ItemTemplateDefinition(checked((uint)seed.Id), seed.Kind, seed.NameKey,
            seed.DisplayName, seed.EquipmentSlot, seed.ClassIds, seed.MinLevel, seed.MaxLevel, seed.Hand,
            seed.SkillFlag, seed.Texture, seed.Icon, canonical[seed.Id])).ToArray();
    }
}
