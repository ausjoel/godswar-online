namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateWonderlandAllIslandTitles() => new(
        "20260916_149_wonderland_all_island_titles",
        "Extend Wonderland title ownership to every island while preserving released awards",
        """
        ALTER TABLE public.wonderland_title_milestones
            DROP CONSTRAINT wonderland_title_milestones_check,
            ADD CONSTRAINT ck_wonderland_title_island_award CHECK (
                (island_number = 1 AND title_id = 5155) OR
                (island_number = 2 AND title_id = 5114) OR
                (island_number = 3 AND title_id = 5156) OR
                (island_number = 4 AND title_id = 5115) OR
                (island_number = 5 AND title_id = 5157) OR
                (island_number = 6 AND title_id = 5116) OR
                (island_number = 7 AND title_id = 5117) OR
                (island_number = 8 AND title_id = 5118)
            );
        ALTER TABLE public.wonderland_character_title_ownership
            DROP CONSTRAINT wonderland_character_title_ownership_title_id_check,
            ADD CONSTRAINT ck_wonderland_character_title_id
                CHECK (title_id IN (5114, 5115, 5116, 5117, 5118, 5155, 5156, 5157));
        """);
}
