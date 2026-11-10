namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // Lelantine Farm rounds: the Saturday window, its settlement and the rewards each scorer is owed.
    // Undo: Development/farm-dogs/farm-rounds-undo.sql (drops these four tables and the migration row).
    private static PostgresSchemaMigration CreateLelantineFarmRounds() => new(
        "20261103_236_lelantine_farm_rounds",
        "Lelantine Farm rounds, settlement results, claimable rewards and score archives",
        """
        CREATE TABLE public.lelantine_farm_rounds (
            round_start timestamptz PRIMARY KEY,
            round_end timestamptz NOT NULL,
            started_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            sparta_points bigint,
            athens_points bigint,
            winner smallint,
            settled_at timestamptz
        );
        CREATE TABLE public.lelantine_farm_round_rewards (
            round_start timestamptz NOT NULL REFERENCES public.lelantine_farm_rounds(round_start),
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            faction smallint NOT NULL,
            score bigint NOT NULL CHECK (score > 0),
            outcome smallint NOT NULL CHECK (outcome IN (0, 1, 2)),
            rank_overall integer NOT NULL,
            pet_experience integer NOT NULL CHECK (pet_experience >= 0),
            character_experience integer NOT NULL CHECK (character_experience >= 0),
            talent_points integer NOT NULL CHECK (talent_points >= 0),
            created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            claimed_at timestamptz,
            PRIMARY KEY (round_start, character_id)
        );
        CREATE INDEX lelantine_farm_round_rewards_unclaimed
            ON public.lelantine_farm_round_rewards (character_id) WHERE claimed_at IS NULL;
        CREATE TABLE public.lelantine_farm_donations_archive (
            LIKE public.lelantine_farm_donations INCLUDING DEFAULTS,
            archived_round timestamptz NOT NULL
        );
        CREATE TABLE public.lelantine_farm_kill_points_archive (
            LIKE public.lelantine_farm_kill_points INCLUDING DEFAULTS,
            archived_round timestamptz NOT NULL
        );
        """);
}
