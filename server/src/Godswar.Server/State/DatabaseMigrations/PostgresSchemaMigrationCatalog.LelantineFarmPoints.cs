namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The Lelantine Farm's two score ledgers, and the dialogue-profile
    /// constraint change the farm's two new behaviours need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original NPC dialogue release pinned
    /// <c>npc_dialogue_profiles.behavior</c> to <c>BETWEEN 1 AND 4</c> and made
    /// it unique per revision. The farm publishes two profiles (the activity
    /// window and the Returning Helper's teleport) whose behaviours are the new
    /// values 18 and 19, so both the range and the one-profile-per-behaviour
    /// rule have to move. The uniqueness rule was a convenience, not an
    /// invariant anything reads back: the publication validates row counts, not
    /// behaviour cardinality.
    /// </para>
    /// <para>
    /// The two ledgers are the activity's own bookkeeping. Donated points are
    /// held per faction, with a per-character donation row behind each faction
    /// advance so a faction total can always be reconciled; kill points are held
    /// per character.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration CreateLelantineFarmPoints() =>
        new(
            "20260926_200_lelantine_farm_points",
            "Record Lelantine Farm egg-donation and kill scores",
            """
            ALTER TABLE public.npc_dialogue_profiles
                DROP CONSTRAINT IF EXISTS ck_npc_dialogue_profiles_behavior;

            ALTER TABLE public.npc_dialogue_profiles
                DROP CONSTRAINT IF EXISTS uq_npc_dialogue_profiles_behavior;

            ALTER TABLE public.npc_dialogue_profiles
                ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
                CHECK (behavior BETWEEN 1 AND 64);

            CREATE TABLE IF NOT EXISTS public.lelantine_farm_donations (
                id bigserial PRIMARY KEY,
                character_id integer NOT NULL
                    REFERENCES public.character_base (id) ON DELETE CASCADE,
                faction smallint NOT NULL,
                item_id integer NOT NULL,
                egg_count integer NOT NULL,
                points integer NOT NULL,
                donated_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_lelantine_farm_donations_faction
                    CHECK (faction IN (0, 1)),
                CONSTRAINT ck_lelantine_farm_donations_egg_count
                    CHECK (egg_count BETWEEN 1 AND 99),
                CONSTRAINT ck_lelantine_farm_donations_points
                    CHECK (points > 0)
            );

            CREATE INDEX IF NOT EXISTS ix_lelantine_farm_donations_character
                ON public.lelantine_farm_donations (
                    character_id,
                    donated_at DESC
                );

            CREATE INDEX IF NOT EXISTS ix_lelantine_farm_donations_faction
                ON public.lelantine_farm_donations (
                    faction,
                    donated_at DESC
                );

            CREATE TABLE IF NOT EXISTS public.lelantine_farm_faction_points (
                faction smallint PRIMARY KEY,
                donated_points bigint NOT NULL DEFAULT 0,
                updated_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_lelantine_farm_faction_points_faction
                    CHECK (faction IN (0, 1)),
                CONSTRAINT ck_lelantine_farm_faction_points_total
                    CHECK (donated_points >= 0)
            );

            INSERT INTO public.lelantine_farm_faction_points (
                faction,
                donated_points
            )
            VALUES (0, 0), (1, 0)
            ON CONFLICT (faction) DO NOTHING;

            CREATE TABLE IF NOT EXISTS public.lelantine_farm_kill_points (
                character_id integer PRIMARY KEY
                    REFERENCES public.character_base (id) ON DELETE CASCADE,
                faction smallint NOT NULL,
                kill_points bigint NOT NULL DEFAULT 0,
                credited_kills bigint NOT NULL DEFAULT 0,
                updated_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_lelantine_farm_kill_points_faction
                    CHECK (faction IN (0, 1)),
                CONSTRAINT ck_lelantine_farm_kill_points_total
                    CHECK (kill_points >= 0 AND credited_kills >= 0)
            );

            CREATE INDEX IF NOT EXISTS ix_lelantine_farm_kill_points_faction
                ON public.lelantine_farm_kill_points (
                    faction,
                    kill_points DESC
                );
            """);
}
