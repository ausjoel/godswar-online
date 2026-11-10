namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string OnlineAwardGuardSql =
        """
        CREATE FUNCTION public.online_award_balance_canonical(
            target_revision bigint)
        RETURNS text
        LANGUAGE sql
        STABLE
        AS $online_award_balance_canonical$
            SELECT 'online-award-balance-v1' || E'\n' || COALESCE(
                string_agg(
                    format(
                        'reward:%s,%s,%s,%s,%s,%s',
                        reward_order, item_id, quantity, item_quality,
                        bound, stack_cap) || E'\n',
                    '' ORDER BY reward_order),
                '')
            FROM public.online_award_balance_entries
            WHERE revision = target_revision;
        $online_award_balance_canonical$;

        CREATE FUNCTION public.guard_online_award_balance_revision()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_online_award_balance_revision$
        DECLARE
            actual_count integer;
            actual_sha256 varchar(64);
            actual_total integer;
            actual_slots integer;
            minimum_order integer;
            maximum_order integer;
            distinct_identities integer;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Online Award revisions are append-only.'
                    USING ERRCODE = '55000';
            END IF;

            IF OLD.sealed_at IS NULL AND NEW.sealed_at IS NOT NULL
               AND (NEW.revision, NEW.sha256, NEW.entry_count,
                    NEW.source, NEW.created_by, NEW.created_at)
                   IS NOT DISTINCT FROM
                   (OLD.revision, OLD.sha256, OLD.entry_count,
                    OLD.source, OLD.created_by, OLD.created_at) THEN
                SELECT count(*)::integer,
                       COALESCE(sum(quantity), 0)::integer,
                       COALESCE(sum(
                           (quantity + stack_cap - 1) / stack_cap), 0)::integer,
                       min(reward_order)::integer,
                       max(reward_order)::integer,
                       count(DISTINCT (
                           item_id, item_quality, bound))::integer
                  INTO actual_count, actual_total, actual_slots,
                       minimum_order, maximum_order, distinct_identities
                FROM public.online_award_balance_entries
                WHERE revision = NEW.revision;
                SELECT upper(encode(sha256(convert_to(
                           public.online_award_balance_canonical(NEW.revision),
                           'UTF8')), 'hex'))
                  INTO actual_sha256;
                IF actual_count <> NEW.entry_count
                   OR actual_total NOT BETWEEN 1 AND 127
                   OR actual_slots NOT BETWEEN 1 AND 96
                   OR minimum_order <> 0
                   OR maximum_order <> actual_count - 1
                   OR distinct_identities <> actual_count
                   OR actual_sha256 <> NEW.sha256 THEN
                    RAISE EXCEPTION
                        'Online Award revision % has an invalid count, quantity, or hash.',
                        NEW.revision
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END IF;

            RAISE EXCEPTION 'Online Award revisions are immutable after insert.'
                USING ERRCODE = '55000';
        END;
        $guard_online_award_balance_revision$;

        CREATE FUNCTION public.guard_online_award_balance_entry()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_online_award_balance_entry$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                PERFORM 1
                FROM public.online_award_balance_revisions revision
                WHERE revision.revision = NEW.revision
                  AND revision.sealed_at IS NULL
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Online Award children require an unsealed revision.'
                        USING ERRCODE = '55000';
                END IF;
            ELSE
                RAISE EXCEPTION 'Online Award reward rows are append-only.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        $guard_online_award_balance_entry$;

        CREATE FUNCTION public.guard_online_award_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_online_award_publication$
        DECLARE
            release_sha256 varchar(64);
            release_sealed_at timestamptz;
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NEW.publication_version <> 1 THEN
                    RAISE EXCEPTION
                        'Online Award publication must start at version one.'
                        USING ERRCODE = '23514';
                END IF;
            ELSIF NEW.publication_version <> OLD.publication_version + 1
               OR NEW.revision <> OLD.revision + 1
               OR NEW.balance_sha256 = OLD.balance_sha256
               OR NEW.updated_at < OLD.updated_at THEN
                RAISE EXCEPTION
                    'Online Award publication requires the exact CAS successor.'
                    USING ERRCODE = '23514';
            END IF;

            UPDATE public.online_award_balance_revisions
            SET sealed_at = transaction_timestamp()
            WHERE revision = NEW.revision
              AND sha256 = NEW.balance_sha256
              AND sealed_at IS NULL;

            SELECT sha256, sealed_at
              INTO release_sha256, release_sealed_at
            FROM public.online_award_balance_revisions
            WHERE revision = NEW.revision;
            IF release_sealed_at IS NULL
               OR release_sha256 <> NEW.balance_sha256 THEN
                RAISE EXCEPTION
                    'Online Award publication requires one sealed exact revision.'
                    USING ERRCODE = '23514';
            END IF;
            NEW.updated_at := transaction_timestamp();
            RETURN NEW;
        END;
        $guard_online_award_publication$;

        CREATE FUNCTION public.audit_online_award_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $audit_online_award_publication$
        BEGIN
            INSERT INTO public.online_award_publication_audit (
                publication_version, previous_revision, revision,
                previous_sha256, balance_sha256, changed_at, changed_by)
            VALUES (
                NEW.publication_version,
                CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.revision END,
                NEW.revision,
                CASE WHEN TG_OP = 'INSERT'
                    THEN NULL ELSE OLD.balance_sha256 END,
                NEW.balance_sha256, NEW.updated_at, NEW.updated_by);
            RETURN NEW;
        END;
        $audit_online_award_publication$;

        CREATE FUNCTION public.guard_online_award_publication_audit()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_online_award_publication_audit$
        BEGIN
            IF TG_OP = 'INSERT' AND EXISTS (
                SELECT 1
                FROM public.online_award_balance_publication publication
                WHERE publication.family = 'online-award'
                  AND publication.publication_version =
                      NEW.publication_version
                  AND publication.revision = NEW.revision
                  AND publication.balance_sha256 = NEW.balance_sha256
                  AND publication.updated_at = NEW.changed_at
                  AND publication.updated_by = NEW.changed_by
            ) AND (
                NEW.previous_revision IS NULL
                OR EXISTS (
                    SELECT 1
                    FROM public.online_award_publication_audit prior
                    WHERE prior.publication_version =
                        NEW.publication_version - 1
                      AND prior.revision = NEW.previous_revision
                      AND prior.balance_sha256 = NEW.previous_sha256
                )
            ) THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION
                'Online Award publication audit is trigger-owned and append-only.'
                USING ERRCODE = '55000';
        END;
        $guard_online_award_publication_audit$;

        CREATE FUNCTION public.reject_online_award_immutable_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_online_award_immutable_mutation$
        BEGIN
            RAISE EXCEPTION '% is append-only', TG_TABLE_NAME
                USING ERRCODE = '55000';
        END;
        $reject_online_award_immutable_mutation$;

        CREATE FUNCTION public.guard_online_award_claim_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_online_award_claim_insert$
        DECLARE
            expected_deltas jsonb;
        BEGIN
            SELECT jsonb_agg(jsonb_build_object(
                       'itemId', item_id,
                       'itemQuality', item_quality,
                       'bound', bound,
                       'quantity', quantity)
                       ORDER BY reward_order)
              INTO expected_deltas
            FROM public.online_award_balance_entries
            WHERE revision = NEW.balance_revision;
            IF expected_deltas IS NULL
               OR NEW.item_deltas <> expected_deltas THEN
                RAISE EXCEPTION
                    'Online Award settlement deltas must equal its balance revision.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $guard_online_award_claim_insert$;

        CREATE TRIGGER trg_online_award_revision_guard
            BEFORE UPDATE OR DELETE
            ON public.online_award_balance_revisions
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_online_award_balance_revision();
        CREATE TRIGGER trg_online_award_entry_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON public.online_award_balance_entries
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_online_award_balance_entry();
        CREATE TRIGGER trg_online_award_revision_no_truncate
            BEFORE TRUNCATE
            ON public.online_award_balance_revisions
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_online_award_immutable_mutation();
        CREATE TRIGGER trg_online_award_entry_no_truncate
            BEFORE TRUNCATE
            ON public.online_award_balance_entries
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_online_award_immutable_mutation();
        CREATE TRIGGER trg_online_award_publication_guard
            BEFORE INSERT OR UPDATE
            ON public.online_award_balance_publication
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_online_award_publication();
        CREATE TRIGGER trg_online_award_publication_audit
            AFTER INSERT OR UPDATE
            ON public.online_award_balance_publication
            FOR EACH ROW EXECUTE FUNCTION
                public.audit_online_award_publication();
        CREATE TRIGGER trg_online_award_publication_audit_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON public.online_award_publication_audit
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_online_award_publication_audit();
        CREATE TRIGGER trg_online_award_publication_audit_no_truncate
            BEFORE TRUNCATE
            ON public.online_award_publication_audit
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_online_award_immutable_mutation();
        CREATE TRIGGER trg_online_award_publication_no_delete
            BEFORE DELETE OR TRUNCATE
            ON public.online_award_balance_publication
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_online_award_immutable_mutation();
        CREATE TRIGGER trg_online_award_claims_immutable
            BEFORE UPDATE OR DELETE OR TRUNCATE
            ON public.online_award_claim_settlements
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_online_award_immutable_mutation();
        CREATE TRIGGER trg_online_award_claim_insert_guard
            BEFORE INSERT
            ON public.online_award_claim_settlements
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_online_award_claim_insert();
        """;
}
