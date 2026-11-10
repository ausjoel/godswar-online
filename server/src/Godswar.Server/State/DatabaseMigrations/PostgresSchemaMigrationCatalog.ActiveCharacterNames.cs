namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateActiveCharacterNames() => new(
        "20261030_235_active_character_names",
        "Reserve case-insensitive character names only while active; release deleted names",
        """
        -- Fail rather than rename any existing active character automatically.
        DO $names$ BEGIN
            IF EXISTS (SELECT lower(name) FROM public.character_base
                WHERE lifecycle_state='active' GROUP BY lower(name) HAVING count(*)>1)
            THEN RAISE EXCEPTION 'Active character names conflict ignoring case; review before migration'; END IF;
        END $names$;
        CREATE UNIQUE INDEX ux_character_base_active_name_ci
            ON public.character_base (lower(name)) WHERE lifecycle_state='active';
        ALTER TABLE public.character_base DROP CONSTRAINT IF EXISTS character_base_name_key;
        DROP INDEX IF EXISTS public.ux_character_base_name;
        COMMENT ON INDEX public.ux_character_base_active_name_ci IS
            'Active characters reserve names ignoring case; deleted characters/accounts do not reserve them. Restore must not reclaim a reused name.';
        """);
}
