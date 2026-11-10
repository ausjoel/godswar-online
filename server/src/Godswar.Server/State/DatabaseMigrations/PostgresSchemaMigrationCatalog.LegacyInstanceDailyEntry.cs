namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateLegacyInstanceDailyEntry() => new(
        "20260901_132_legacy_instance_daily_entry",
        "Persist atomic daily Atlantis and Wonderland party entries",
        """
        CREATE TABLE public.legacy_instance_daily_entries (
            realm_id smallint NOT NULL,
            realm_day date NOT NULL,
            instance_kind smallint NOT NULL
                CHECK (instance_kind BETWEEN 1 AND 2),
            character_id integer NOT NULL
                REFERENCES public.character_base(id) ON DELETE CASCADE,
            reservation_id uuid NOT NULL,
            claimed_at timestamptz NOT NULL,
            PRIMARY KEY (
                realm_id,
                realm_day,
                instance_kind,
                character_id)
        );

        CREATE INDEX ix_legacy_instance_daily_entries_reservation
            ON public.legacy_instance_daily_entries (reservation_id);
        """);
}
