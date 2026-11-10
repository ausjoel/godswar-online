namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // ROUND-NPCSUCCESSOR (issue #27): the explicit contract for an operator-published NPC dialogue revision that
    // succeeds the reviewed server release. The dialogue publisher accepts a published revision that is not a
    // reviewed baseline only when it is registered here, its predecessor chain reaches the reviewed release, and
    // its rows keep every route, profile and menu of that release (texts may differ). Rows are immutable.
    // Undo (only before any successor is registered): DROP TABLE public.npc_dialogue_successor_releases;
    // DROP FUNCTION public.reject_npc_dialogue_successor_mutation(); then delete this migration row.
    private static PostgresSchemaMigration CreateNpcDialogueSuccessors() => new(
        "20261106_238_npc_dialogue_successors",
        "Register operator-published NPC dialogue successor releases",
        """
        CREATE TABLE public.npc_dialogue_successor_releases (
            revision varchar(64) PRIMARY KEY
                REFERENCES public.npc_dialogue_revisions (revision) ON DELETE RESTRICT,
            predecessor_revision varchar(64) NOT NULL
                REFERENCES public.npc_dialogue_revisions (revision) ON DELETE RESTRICT,
            publisher varchar(64) NOT NULL,
            registered_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_npc_dialogue_successor_not_self
                CHECK (revision <> predecessor_revision),
            CONSTRAINT ck_npc_dialogue_successor_publisher
                CHECK (publisher = 'gwadmin-web-npc-text-v1')
        );

        CREATE FUNCTION public.reject_npc_dialogue_successor_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_npc_dialogue_successor_mutation$
        BEGIN
            RAISE EXCEPTION
                'NPC dialogue successor registrations are immutable; register a new revision instead.';
        END;
        $reject_npc_dialogue_successor_mutation$;

        CREATE TRIGGER trg_npc_dialogue_successor_releases_immutable
        BEFORE UPDATE OR DELETE ON public.npc_dialogue_successor_releases
        FOR EACH ROW EXECUTE FUNCTION public.reject_npc_dialogue_successor_mutation();
        """);
}
