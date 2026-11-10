namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The permanent quest experience appraisal behind the stock quest window's
    /// third tab (经验加成 / "Increase EXP gain", button 我要鉴定 / "Appraisal").
    /// </summary>
    /// <remarks>
    /// The client sends C2S 10093 as a six-byte frame whose two payload bytes were
    /// zero in every captured click, and the reference server answers an
    /// eight-byte frame carrying a single 32-bit value (also zero in every
    /// capture). The bonus is permanent, so one row per character is enough: the
    /// read is a single lookup and the grant is one idempotent insert.
    /// </remarks>
    private static PostgresSchemaMigration CreateQuestExperienceAppraisal() =>
        new(
            "20260924_166_quest_experience_appraisal",
            "Persist the permanent quest experience appraisal bonus",
            """
            CREATE TABLE public.character_quest_appraisal (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                bonus_basis_points integer NOT NULL
                    CHECK (bonus_basis_points > 0),
                appraised_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (character_id)
            );

            COMMENT ON TABLE public.character_quest_appraisal IS
                'Authoritative quest experience appraisal: the row exists only after the character passed the appraisal, and every quest hand-in then pays bonus_basis_points extra experience.';
            """);
}
