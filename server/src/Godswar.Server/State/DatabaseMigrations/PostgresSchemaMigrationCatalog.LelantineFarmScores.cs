namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Makes the farm's faction score the sum of every same-faction personal
    /// score, and gives the activity one place to read a personal score from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The activity's own rule is that every personal score a fighter earns
    /// counts towards their camp's total, so the faction total is the sum of the
    /// personal scores of that camp's fighters. The first release kept a
    /// separate running total per faction that only donation advanced, which
    /// cannot express that rule once kills also score. The running total is
    /// therefore dropped and the faction total is read from the same ledger the
    /// personal score comes from, so the two can never disagree.
    /// </para>
    /// <para>
    /// <c>lelantine_farm_personal_points</c> is the one definition of a personal
    /// score: every donation row and every credited kill row of one character in
    /// one faction, summed. Both the personal read and the faction read go
    /// through it.
    /// </para>
    /// <para>
    /// The kill ledger's primary key moves from the character alone to the
    /// character <em>and</em> the faction, because a character's credited kills
    /// belong to the camp they were earned for. Keyed by character alone, a
    /// fighter who changed camp would keep one row whose faction no longer
    /// matched the kills being added to it, and the per-faction attribution the
    /// faction total depends on would be lost.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration CreateLelantineFarmPersonalScores() =>
        new(
            "20260926_210_lelantine_farm_personal_scores",
            "Sum the Lelantine Farm faction score from every personal score",
            """
            ALTER TABLE public.lelantine_farm_kill_points
                DROP CONSTRAINT IF EXISTS lelantine_farm_kill_points_pkey;

            ALTER TABLE public.lelantine_farm_kill_points
                ADD CONSTRAINT lelantine_farm_kill_points_pkey
                PRIMARY KEY (character_id, faction);

            CREATE OR REPLACE VIEW public.lelantine_farm_personal_points AS
            SELECT
                ledger.character_id,
                ledger.faction,
                SUM(ledger.points)::bigint AS points
            FROM (
                SELECT
                    donation.character_id,
                    donation.faction,
                    donation.points::bigint AS points
                FROM public.lelantine_farm_donations AS donation
                UNION ALL
                SELECT
                    kill.character_id,
                    kill.faction,
                    kill.kill_points
                FROM public.lelantine_farm_kill_points AS kill
            ) AS ledger
            GROUP BY ledger.character_id, ledger.faction;

            DROP TABLE IF EXISTS public.lelantine_farm_faction_points;
            """);
}
