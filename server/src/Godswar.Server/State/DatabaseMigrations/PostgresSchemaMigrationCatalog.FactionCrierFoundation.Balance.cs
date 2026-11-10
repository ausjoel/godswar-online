namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string FactionCrierBalanceSql =
        """
        CREATE TABLE public.faction_crier_balance_revisions (
            revision bigint PRIMARY KEY CHECK (revision >= 0),
            server_utc_offset_minutes smallint NOT NULL
                CHECK (server_utc_offset_minutes BETWEEN -840 AND 840),
            minimum_level smallint NOT NULL
                CHECK (minimum_level = 20),
            weekly_reclaim_gold_cost integer NOT NULL
                CHECK (weekly_reclaim_gold_cost BETWEEN 0 AND 2147483647),
            renewal_gold_cost integer NOT NULL
                CHECK (renewal_gold_cost BETWEEN 0 AND 2147483647),
            tier_count smallint NOT NULL CHECK (tier_count = 7),
            option_count smallint NOT NULL CHECK (option_count = 25),
            created_at timestamptz NOT NULL
                DEFAULT transaction_timestamp(),
            created_by varchar(128) NOT NULL
                CHECK (length(btrim(created_by)) BETWEEN 1 AND 128),
            sealed_at timestamptz,
            CONSTRAINT ck_faction_crier_balance_revision_times CHECK (
                sealed_at IS NULL OR sealed_at >= created_at
            )
        );

        CREATE TABLE public.faction_crier_balance_tiers (
            balance_revision bigint NOT NULL,
            minimum_level smallint NOT NULL,
            maximum_level smallint NOT NULL,
            base_experience integer NOT NULL,
            base_talent_points integer NOT NULL,
            triple_silver_cost integer NOT NULL,
            all_six_silver_cost integer NOT NULL,
            CONSTRAINT pk_faction_crier_balance_tiers
                PRIMARY KEY (balance_revision, minimum_level),
            CONSTRAINT uq_faction_crier_balance_tier_maximum
                UNIQUE (balance_revision, maximum_level),
            CONSTRAINT fk_faction_crier_balance_tier_revision
                FOREIGN KEY (balance_revision)
                REFERENCES public.faction_crier_balance_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_faction_crier_balance_tier_levels CHECK (
                minimum_level BETWEEN 1 AND 200
                AND maximum_level BETWEEN minimum_level AND 200
            ),
            CONSTRAINT ck_faction_crier_balance_tier_stock_ranges CHECK (
                (minimum_level, maximum_level) IN (
                    (20, 39),
                    (40, 59),
                    (60, 79),
                    (80, 99),
                    (100, 120),
                    (121, 130),
                    (131, 200)
                )
            ),
            CONSTRAINT ck_faction_crier_balance_tier_rewards CHECK (
                base_experience > 0
                AND base_talent_points > 0
                AND triple_silver_cost BETWEEN 0 AND 2147483647
                AND all_six_silver_cost BETWEEN 0 AND 2147483647
            )
        );

        CREATE TABLE public.faction_crier_balance_options (
            balance_revision bigint NOT NULL,
            sub_id smallint NOT NULL,
            currency_code varchar(16) NOT NULL,
            cost integer NOT NULL,
            multiplier smallint NOT NULL,
            reward_kind varchar(32) NOT NULL,
            CONSTRAINT pk_faction_crier_balance_options
                PRIMARY KEY (balance_revision, sub_id),
            CONSTRAINT fk_faction_crier_balance_option_revision
                FOREIGN KEY (balance_revision)
                REFERENCES public.faction_crier_balance_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_faction_crier_balance_option_sub_id
                CHECK (sub_id BETWEEN 110 AND 134),
            CONSTRAINT ck_faction_crier_balance_option_currency CHECK (
                currency_code IN ('silver', 'binding_gold', 'gold')
            ),
            CONSTRAINT ck_faction_crier_balance_option_values CHECK (
                cost BETWEEN 0 AND 2147483647
                AND multiplier BETWEEN 1 AND 100
            ),
            CONSTRAINT ck_faction_crier_balance_option_reward CHECK (
                reward_kind IN (
                    'experience',
                    'talent_points',
                    'experience_and_talent_points'
                )
            ),
            CONSTRAINT ck_faction_crier_balance_option_stock_shape CHECK (
                (sub_id IN (110, 120)
                    AND currency_code = 'silver'
                    AND reward_kind = 'experience')
                OR (sub_id IN (111, 121)
                    AND currency_code = 'silver'
                    AND reward_kind = 'talent_points')
                OR (sub_id IN (112, 116, 122, 126)
                    AND currency_code = 'binding_gold'
                    AND reward_kind = 'experience')
                OR (sub_id IN (113, 117, 123, 127)
                    AND currency_code = 'binding_gold'
                    AND reward_kind = 'talent_points')
                OR (sub_id IN (114, 118, 124, 128)
                    AND currency_code = 'gold'
                    AND reward_kind = 'experience')
                OR (sub_id IN (115, 119, 125, 129)
                    AND currency_code = 'gold'
                    AND reward_kind = 'talent_points')
                OR (sub_id = 130
                    AND currency_code = 'silver'
                    AND reward_kind =
                        'experience_and_talent_points')
                OR (sub_id IN (131, 133)
                    AND currency_code = 'binding_gold'
                    AND reward_kind =
                        'experience_and_talent_points')
                OR (sub_id IN (132, 134)
                    AND currency_code = 'gold'
                    AND reward_kind =
                        'experience_and_talent_points')
            )
        );

        CREATE OR REPLACE FUNCTION
            public.reject_faction_crier_balance_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_faction_crier_balance_mutation$
        BEGIN
            IF TG_TABLE_NAME = 'faction_crier_balance_revisions'
               AND TG_OP = 'UPDATE'
               AND OLD.sealed_at IS NULL
               AND NEW.sealed_at IS NOT NULL
               AND NEW.revision = OLD.revision
               AND NEW.server_utc_offset_minutes =
                    OLD.server_utc_offset_minutes
               AND NEW.minimum_level = OLD.minimum_level
               AND NEW.weekly_reclaim_gold_cost =
                    OLD.weekly_reclaim_gold_cost
               AND NEW.renewal_gold_cost = OLD.renewal_gold_cost
               AND NEW.tier_count = OLD.tier_count
               AND NEW.option_count = OLD.option_count
               AND NEW.created_at = OLD.created_at
               AND NEW.created_by = OLD.created_by THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION
                'Faction Crier balance revisions are immutable.'
                USING ERRCODE = '55000';
        END;
        $reject_faction_crier_balance_mutation$;

        CREATE TRIGGER trg_faction_crier_balance_revisions_immutable
        BEFORE UPDATE OR DELETE
        ON public.faction_crier_balance_revisions
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_faction_crier_balance_mutation();
        CREATE TRIGGER trg_faction_crier_balance_revisions_no_truncate
        BEFORE TRUNCATE ON public.faction_crier_balance_revisions
        FOR EACH STATEMENT EXECUTE FUNCTION
            public.reject_faction_crier_balance_mutation();

        CREATE TRIGGER trg_faction_crier_balance_tiers_immutable
        BEFORE UPDATE OR DELETE
        ON public.faction_crier_balance_tiers
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_faction_crier_balance_mutation();
        CREATE TRIGGER trg_faction_crier_balance_tiers_no_truncate
        BEFORE TRUNCATE ON public.faction_crier_balance_tiers
        FOR EACH STATEMENT EXECUTE FUNCTION
            public.reject_faction_crier_balance_mutation();

        CREATE TRIGGER trg_faction_crier_balance_options_immutable
        BEFORE UPDATE OR DELETE
        ON public.faction_crier_balance_options
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_faction_crier_balance_mutation();
        CREATE TRIGGER trg_faction_crier_balance_options_no_truncate
        BEFORE TRUNCATE ON public.faction_crier_balance_options
        FOR EACH STATEMENT EXECUTE FUNCTION
            public.reject_faction_crier_balance_mutation();

        CREATE OR REPLACE FUNCTION
            public.reject_sealed_faction_crier_balance_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_sealed_faction_crier_balance_insert$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.faction_crier_balance_revisions revision
                WHERE revision.revision = NEW.balance_revision
                  AND revision.sealed_at IS NOT NULL
            ) THEN
                RAISE EXCEPTION
                    'Published Faction Crier balance children are immutable.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        $reject_sealed_faction_crier_balance_insert$;

        CREATE TRIGGER trg_faction_crier_balance_tiers_insert_guard
        BEFORE INSERT ON public.faction_crier_balance_tiers
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_sealed_faction_crier_balance_insert();
        CREATE TRIGGER trg_faction_crier_balance_options_insert_guard
        BEFORE INSERT ON public.faction_crier_balance_options
        FOR EACH ROW EXECUTE FUNCTION
            public.reject_sealed_faction_crier_balance_insert();

        CREATE TABLE public.faction_crier_balance_settings (
            setting_id smallint PRIMARY KEY DEFAULT 1
                CHECK (setting_id = 1),
            revision bigint NOT NULL,
            updated_at timestamptz NOT NULL
                DEFAULT transaction_timestamp(),
            updated_by varchar(128) NOT NULL
                CHECK (length(btrim(updated_by)) BETWEEN 1 AND 128),
            CONSTRAINT fk_faction_crier_balance_setting_revision
                FOREIGN KEY (revision)
                REFERENCES public.faction_crier_balance_revisions (revision)
                ON DELETE RESTRICT
        );

        CREATE OR REPLACE FUNCTION
            public.validate_faction_crier_balance_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $validate_faction_crier_balance_publication$
        DECLARE
            expected_minimum smallint;
            expected_tiers smallint;
            expected_options smallint;
            actual_tiers integer;
            actual_options integer;
            has_tier_gap boolean;
            has_reward_overflow boolean;
        BEGIN
            IF TG_OP = 'UPDATE'
               AND NEW.revision <> OLD.revision + 1 THEN
                RAISE EXCEPTION
                    'Faction Crier balance revisions must advance exactly once.';
            END IF;

            SELECT minimum_level, tier_count, option_count
              INTO expected_minimum, expected_tiers, expected_options
            FROM public.faction_crier_balance_revisions
            WHERE revision = NEW.revision
            FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION
                    'Unknown Faction Crier balance revision %.',
                    NEW.revision;
            END IF;

            SELECT count(*)::integer
              INTO actual_tiers
            FROM public.faction_crier_balance_tiers
            WHERE balance_revision = NEW.revision;
            SELECT count(*)::integer
              INTO actual_options
            FROM public.faction_crier_balance_options
            WHERE balance_revision = NEW.revision;
            SELECT EXISTS (
                SELECT 1
                FROM (
                    SELECT
                        minimum_level,
                        maximum_level,
                        lag(maximum_level) OVER (
                            ORDER BY minimum_level
                        ) AS previous_maximum
                    FROM public.faction_crier_balance_tiers
                    WHERE balance_revision = NEW.revision
                ) ordered
                WHERE (
                    previous_maximum IS NULL
                    AND minimum_level <> expected_minimum
                ) OR (
                    previous_maximum IS NOT NULL
                    AND minimum_level <> previous_maximum + 1
                )
            ) OR NOT EXISTS (
                SELECT 1
                FROM public.faction_crier_balance_tiers
                WHERE balance_revision = NEW.revision
                  AND maximum_level = 200
            ) INTO has_tier_gap;

            SELECT EXISTS (
                SELECT 1
                FROM public.faction_crier_balance_tiers tier
                CROSS JOIN public.faction_crier_balance_options option
                WHERE tier.balance_revision = NEW.revision
                  AND option.balance_revision = NEW.revision
                  AND (
                    (
                        option.reward_kind IN (
                            'experience',
                            'experience_and_talent_points'
                        )
                        AND tier.base_experience::bigint *
                            option.multiplier::bigint > 2147483647
                    ) OR (
                        option.reward_kind IN (
                            'talent_points',
                            'experience_and_talent_points'
                        )
                        AND tier.base_talent_points::bigint *
                            option.multiplier::bigint > 2147483647
                    )
                  )
            ) INTO has_reward_overflow;

            IF actual_tiers <> expected_tiers
               OR actual_options <> expected_options
               OR has_tier_gap
               OR has_reward_overflow THEN
                RAISE EXCEPTION
                    'Faction Crier balance revision % is incomplete or unsafe.',
                    NEW.revision;
            END IF;

            UPDATE public.faction_crier_balance_revisions
            SET sealed_at = transaction_timestamp()
            WHERE revision = NEW.revision
              AND sealed_at IS NULL;
            NEW.updated_at := transaction_timestamp();
            RETURN NEW;
        END;
        $validate_faction_crier_balance_publication$;

        CREATE TRIGGER trg_faction_crier_balance_settings_publish
        BEFORE INSERT OR UPDATE
        ON public.faction_crier_balance_settings
        FOR EACH ROW EXECUTE FUNCTION
            public.validate_faction_crier_balance_publication();

        CREATE OR REPLACE FUNCTION
            public.reject_faction_crier_balance_settings_delete()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_faction_crier_balance_settings_delete$
        BEGIN
            RAISE EXCEPTION
                'The Faction Crier balance publication cannot be deleted.'
                USING ERRCODE = '55000';
        END;
        $reject_faction_crier_balance_settings_delete$;

        CREATE TRIGGER trg_faction_crier_balance_settings_no_delete
        BEFORE DELETE OR TRUNCATE
        ON public.faction_crier_balance_settings
        FOR EACH STATEMENT EXECUTE FUNCTION
            public.reject_faction_crier_balance_settings_delete();

        INSERT INTO public.faction_crier_balance_revisions (
            revision,
            server_utc_offset_minutes,
            minimum_level,
            weekly_reclaim_gold_cost,
            renewal_gold_cost,
            tier_count,
            option_count,
            created_by
        ) VALUES (0, -480, 20, 230, 105, 7, 25, 'migration-100');

        INSERT INTO public.faction_crier_balance_tiers (
            balance_revision,
            minimum_level,
            maximum_level,
            base_experience,
            base_talent_points,
            triple_silver_cost,
            all_six_silver_cost
        ) VALUES
            (0, 20, 39, 23500, 5, 15000, 40000),
            (0, 40, 59, 45800, 7, 35000, 120000),
            (0, 60, 79, 116000, 14, 60000, 180000),
            (0, 80, 99, 158000, 21, 80000, 240000),
            (0, 100, 120, 218000, 32, 120000, 360000),
            (0, 121, 130, 230000, 35, 140000, 400000),
            (0, 131, 200, 250000, 38, 140000, 400000);

        INSERT INTO public.faction_crier_balance_options (
            balance_revision,
            sub_id,
            currency_code,
            cost,
            multiplier,
            reward_kind
        ) VALUES
            (0, 110, 'silver', 0, 6, 'experience'),
            (0, 111, 'silver', 0, 6, 'talent_points'),
            (0, 112, 'binding_gold', 312, 9, 'experience'),
            (0, 113, 'binding_gold', 312, 9, 'talent_points'),
            (0, 114, 'gold', 312, 9, 'experience'),
            (0, 115, 'gold', 312, 9, 'talent_points'),
            (0, 116, 'binding_gold', 459, 12, 'experience'),
            (0, 117, 'binding_gold', 459, 12, 'talent_points'),
            (0, 118, 'gold', 459, 12, 'experience'),
            (0, 119, 'gold', 459, 12, 'talent_points'),
            (0, 120, 'silver', 0, 6, 'experience'),
            (0, 121, 'silver', 0, 6, 'talent_points'),
            (0, 122, 'binding_gold', 312, 9, 'experience'),
            (0, 123, 'binding_gold', 312, 9, 'talent_points'),
            (0, 124, 'gold', 312, 9, 'experience'),
            (0, 125, 'gold', 312, 9, 'talent_points'),
            (0, 126, 'binding_gold', 459, 12, 'experience'),
            (0, 127, 'binding_gold', 459, 12, 'talent_points'),
            (0, 128, 'gold', 459, 12, 'experience'),
            (0, 129, 'gold', 459, 12, 'talent_points'),
            (0, 130, 'silver', 0, 12,
                'experience_and_talent_points'),
            (0, 131, 'binding_gold', 936, 18,
                'experience_and_talent_points'),
            (0, 132, 'gold', 936, 18,
                'experience_and_talent_points'),
            (0, 133, 'binding_gold', 1377, 24,
                'experience_and_talent_points'),
            (0, 134, 'gold', 1377, 24,
                'experience_and_talent_points');

        INSERT INTO public.faction_crier_balance_settings (
            setting_id, revision, updated_by
        ) VALUES (1, 0, 'migration-100');

        COMMENT ON TABLE public.faction_crier_balance_settings IS
            'CAS publication pointer; workers pin this balance revision at startup.';
        COMMENT ON COLUMN
            public.faction_crier_balance_revisions.server_utc_offset_minutes IS
            'Fixed realm-calendar UTC offset used for daily and weekly claim keys.';
        """;
}
