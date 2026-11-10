namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateRestoreItemViewAuthority() => new(
        "20260907_142_restore_item_view_authority",
        "Restore immutable item view dependencies replaced by relational seed bootstrap",
        """
        DO $restore_item_view_authority$
        DECLARE
            view_name text;
            prior_definition text;
            next_definition text;
        BEGIN
            FOREACH view_name IN ARRAY ARRAY[
                'item_allowed_attributes',
                'character_equipment_attributes']
            LOOP
                prior_definition := pg_get_viewdef(
                    format('public.%I', view_name)::regclass, true);
                next_definition := regexp_replace(
                    prior_definition,
                    '\m(public\.)?item_templates\M',
                    'public.official_item_template_content', 'g');
                next_definition := regexp_replace(
                    next_definition,
                    '\m(public\.)?item_attribute_templates\M',
                    'public.official_item_attribute_content', 'g');
                IF next_definition IS DISTINCT FROM prior_definition THEN
                    -- CREATE OR REPLACE preserves the public column signature,
                    -- owner and grants; only these two relation sources change.
                    EXECUTE format('CREATE OR REPLACE VIEW public.%I AS %s',
                        view_name, next_definition);
                END IF;
            END LOOP;

            IF EXISTS (
                SELECT 1
                FROM pg_rewrite rewrite
                JOIN pg_class view_class ON view_class.oid = rewrite.ev_class
                JOIN pg_namespace view_namespace
                  ON view_namespace.oid = view_class.relnamespace
                JOIN pg_depend dependency
                  ON dependency.classid = 'pg_rewrite'::regclass
                 AND dependency.objid = rewrite.oid
                WHERE view_namespace.nspname = 'public'
                  AND view_class.relname IN (
                      'item_allowed_attributes', 'character_equipment_attributes')
                  AND dependency.refobjid = ANY(ARRAY[
                      'public.item_templates'::regclass,
                      'public.item_attribute_templates'::regclass]::oid[])
            ) THEN
                RAISE EXCEPTION 'Reviewed item views still use mutable content';
            END IF;
        END
        $restore_item_view_authority$;
        """);
}
