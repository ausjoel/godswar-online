namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetPresenceAuditOperation() => new(
            "20260728_015_pet_presence_audit_operation",
            "Extend the authoritative pet audit vocabulary with carried-pet selection",
            """
            ALTER TABLE public.pet_operation_audit
                ADD CONSTRAINT ck_pet_operation_audit_operation_v2
                CHECK (
                    operation IN (
                        'owner_merge',
                        'pet_merge',
                        'rebirth',
                        'soul_contract',
                        'take',
                        'summon',
                        'dismiss',
                        'reveal_growth',
                        'seal',
                        'unseal'
                    )
                )
                NOT VALID;

            ALTER TABLE public.pet_operation_audit
                VALIDATE CONSTRAINT
                    ck_pet_operation_audit_operation_v2;

            ALTER TABLE public.pet_operation_audit
                DROP CONSTRAINT
                    pet_operation_audit_operation_check;

            ALTER TABLE public.pet_operation_audit
                RENAME CONSTRAINT
                    ck_pet_operation_audit_operation_v2
                TO pet_operation_audit_operation_check;
            """);

    /// <summary>
    /// Adds the discard verb, so a pet the player destroys outright is
    /// distinguishable in the ledger from one merely stowed away.
    /// </summary>
    /// <remarks>
    /// The pet row itself is gone after a discard, which is why this matters:
    /// the audit entry is the only durable record that the pet ever existed and
    /// how it ended. <c>pet_id</c> is <c>ON DELETE SET NULL</c>, so the row
    /// survives the delete with its <c>pet_id_snapshot</c> intact.
    /// <para>
    /// The vocabulary is restated in full rather than appended to, because the
    /// constraint is replaced wholesale. This list is the union every earlier
    /// revision contributed - v2 carried the presence verbs, then growth, level,
    /// basic-savvy, appearance, bind and Pet Manager utility each widened it -
    /// so dropping any of them here would silently reject an unrelated pet
    /// operation later. It is the v8 set plus the new verb, and must stay in
    /// step with it.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration
        CreatePetPresenceDeleteOperation() => new(
            "20260924_167_pet_delete_audit_operation",
            "Record a permanent pet discard in the authoritative pet audit",
            """
            ALTER TABLE public.pet_operation_audit
                ADD CONSTRAINT ck_pet_operation_audit_operation_v9
                CHECK (
                    operation IN (
                        'owner_merge',
                        'pet_merge',
                        'rebirth',
                        'soul_contract',
                        'take',
                        'summon',
                        'dismiss',
                        'reveal_growth',
                        'reset_basic_savvy',
                        'change_appearance',
                        'bind',
                        'seal',
                        'unseal',
                        'hatch',
                        'level_up',
                        'check_growth',
                        'claim_pet_call',
                        'claim_merge',
                        'change_gender',
                        'delete'
                    )
                )
                NOT VALID;

            ALTER TABLE public.pet_operation_audit
                VALIDATE CONSTRAINT
                    ck_pet_operation_audit_operation_v9;

            ALTER TABLE public.pet_operation_audit
                DROP CONSTRAINT
                    pet_operation_audit_operation_check;

            ALTER TABLE public.pet_operation_audit
                RENAME CONSTRAINT
                    ck_pet_operation_audit_operation_v9
                TO pet_operation_audit_operation_check;
            """);
}
