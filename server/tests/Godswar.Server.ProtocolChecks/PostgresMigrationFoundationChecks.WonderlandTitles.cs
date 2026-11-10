using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckWonderlandTitleExtension()
    {
        var released = PostgresSchemaMigrationCatalog.All.Single(migration =>
            migration.Id == "20260916_148_wonderland_titles");
        Check.Equal("6435DF2A1EA0930BAAC898FBC87090B136C964EB956ED9FAA9CBE47572795290", released.Checksum,
            "already deployed Wonderland title migration remains byte-for-byte sealed");
        var extension = PostgresSchemaMigrationCatalog.All.Single(migration =>
            migration.Id == "20260916_149_wonderland_all_island_titles");
        uint[] expected = [5155, 5114, 5156, 5115, 5157, 5116, 5117, 5118];
        for (var island = 1; island <= 8; island++)
        {
            Check.Equal(expected[island - 1], WonderlandTitlePolicy.Resolve(island).TitleId,
                "the all-island policy preserves old IDs and reserves distinct IDs for new awards");
            Check.True(extension.Sql.Contains($"island_number = {island} AND title_id = {expected[island - 1]}",
                    StringComparison.Ordinal), "the forward schema enforces each authoritative island-to-title mapping");
        }
        Check.Equal("wonderland-titles-v1", WonderlandTitlePolicy.Revision,
            "adding previously unsupported islands does not invalidate existing run or request hashes");
        Check.True(extension.Sql.Contains("DROP CONSTRAINT wonderland_title_milestones_check", StringComparison.Ordinal) &&
            extension.Sql.Contains("DROP CONSTRAINT wonderland_character_title_ownership_title_id_check", StringComparison.Ordinal) &&
            !extension.Sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
            !extension.Sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase) &&
            !extension.Sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase),
            "the additive migration expands only exact title constraints without rewriting earned ownership or receipts");
    }
}
