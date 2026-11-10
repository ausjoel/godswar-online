namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Restores the Atlantis completion roster constraint with its real
    /// direction: every admitted member appears on the settlement roster.
    /// </summary>
    /// <remarks>
    /// Migration 217 read the two character arrays the wrong way round. It
    /// required <c>character_ids &lt;@ admitted_character_ids</c> - "every
    /// recipient was a registered party member" - but a run's recipients are the
    /// characters still inside it when it ends, and a member who joined the
    /// running instance after its leader committed (an invitation, or a party
    /// window confirmed once the run was sealed) is present without holding one
    /// of the registered seats. A party of two therefore settled
    /// <c>admitted = {19}</c> with <c>characters = {19,25}</c>, which the
    /// constraint rejected, so the whole settlement was refused and the run paid
    /// nobody. It was dropped by hand on the development database to unblock
    /// testing, which left a fresh database unable to pay a party at all.
    /// <para>
    /// The direction the evidence supports is the other one. The command is
    /// <c>admitted_character_ids &lt;@ character_ids</c>: a run may pay more
    /// characters than the party it was registered for, never fewer, because a
    /// registered member who claimed and confirmed admission is inside the run
    /// when it ends and is paid (the store refuses the settlement when a
    /// registered member's row is missing instead). Checked against every stored
    /// row on the development database before this migration was written: 12 of
    /// 12 rows satisfy the new predicate, while 4 of the 12 are exactly the rows
    /// the old predicate rejects.
    /// </para>
    /// <para>
    /// The constraint keeps its name, so the drop is name-targeted and the
    /// migration is replay-safe: re-running it drops and re-adds nothing twice.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration
        CreateAtlantisCompletionRosterConstraint() => new(
        "20261001_218_atlantis_completion_roster_constraint",
        "Require every admitted Atlantis member on the settlement roster",
        """
        ALTER TABLE public.atlantis_completion_rewards
            DROP CONSTRAINT IF EXISTS ck_atlantis_completion_rewards_characters_admitted;

        ALTER TABLE public.atlantis_completion_rewards
            ADD CONSTRAINT ck_atlantis_completion_rewards_characters_admitted
                CHECK (admitted_character_ids <@ character_ids);
        """);
}
