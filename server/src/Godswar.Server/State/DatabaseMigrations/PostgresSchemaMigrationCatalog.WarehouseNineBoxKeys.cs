namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // W4A (wave 4 package A, the admin 2026-11-12 "Can we unlock warehouse spaces?"): OPTIONAL policy successor that lets
    // Storage Box Keys open all nine warehouse boxes again. Revision 3 (migration 111, live) stops at 160 cells (SB-4)
    // and reserves SB-5..SB-9; revision 4 republishes the audited nine-box levels of revision 2 (migration 110):
    // SB-5..SB-9 cost 4, 5, 6, 7, 8 keys (36 keys from one box to nine). Same canonical text as revision 2, so the
    // same SHA-256 (warehouse_expansion_policy_canonical has no revision in it). The client already draws SB-1..SB-9
    // and the database CHECKs already allow 360 cells (migration 110); workers pin the policy at start-up, so it takes
    // effect at the restart of the release that applies it.
    //
    // NOT REGISTERED in PostgresSchemaMigrationCatalog.All on purpose: it is a game-design change for the admin / the lead to
    // decide. To ship it: add `CreateWarehouseNineBoxKeys() // W4A optional` after CreateAccountNameHolds() in
    // PostgresSchemaMigrationCatalog.cs, raise the catalog count check by one and append its id to
    // PostgresMigrationFoundationChecks.ExpectedIds.cs. Undo: publish a revision 5 with the four levels of revision 3
    // (policy revisions are append-only; capacity never shrinks, characters keep boxes already opened).
    private static PostgresSchemaMigration CreateWarehouseNineBoxKeys() =>
        new(
            "20261112_241_warehouse_nine_box_storage_keys",
            "Let Storage Box Keys open warehouse boxes SB-5 to SB-9 (policy revision 4)",
            """
            INSERT INTO public.warehouse_expansion_policy_revisions (
                revision, sha256, level_count, source, created_by)
            VALUES (
                4,
                '417B28788C5BFA91341E2E7818C999002FD79B4B46988BC5DB2793C5F7BD37C8',
                9,
                'w4a-nine-box-storage-keys-v4',
                'server-w4a');

            INSERT INTO public.warehouse_expansion_policy_levels (
                revision, capacity, key_cost, key_item_id)
            VALUES
                (4,  40, 0, 4102),
                (4,  80, 1, 4102),
                (4, 120, 2, 4102),
                (4, 160, 3, 4102),
                (4, 200, 4, 4102),
                (4, 240, 5, 4102),
                (4, 280, 6, 4102),
                (4, 320, 7, 4102),
                (4, 360, 8, 4102);

            DO $publish_warehouse_policy_v4$
            BEGIN
                UPDATE public.warehouse_expansion_policy_publication
                SET revision = 4,
                    policy_sha256 =
                        '417B28788C5BFA91341E2E7818C999002FD79B4B46988BC5DB2793C5F7BD37C8',
                    publication_version = 4,
                    updated_by = 'server-w4a'
                WHERE family = 'warehouse-expansion'
                  AND revision = 3
                  AND policy_sha256 =
                      '05E00F650DCAA45FA72926AAE57A55656E0CEFB9F3410D8DEC47DDEB33BC9EBC'
                  AND publication_version = 3;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Warehouse policy publication is not the expected v3 predecessor.'
                        USING ERRCODE = '55000';
                END IF;
            END;
            $publish_warehouse_policy_v4$;
            """);
}
