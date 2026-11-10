namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // Lelantine Farm free wooden tools: what each character has been handed in the current round window.
    // Undo: Development/farm-dogs/farm-tools-undo.sql (drops the table and the migration row).
    private static PostgresSchemaMigration CreateLelantineFarmTools() => new(
        "20261103_237_lelantine_farm_tools",
        "Lelantine Farm free wooden tool handouts per character and round window",
        """
        CREATE TABLE public.lelantine_farm_tool_grants (
            character_id integer PRIMARY KEY REFERENCES public.character_base(id) ON DELETE CASCADE,
            round_start timestamptz NOT NULL,
            grants integer NOT NULL CHECK (grants > 0),
            last_grant_at timestamptz NOT NULL
        );
        """);
}
