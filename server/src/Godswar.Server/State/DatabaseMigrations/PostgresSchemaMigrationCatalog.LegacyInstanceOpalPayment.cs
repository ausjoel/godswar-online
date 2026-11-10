namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateLegacyInstanceOpalPayment() => new(
        "20260901_135_legacy_instance_opal_payment",
        "Publish stock Opal 3932 and persist compensated Atlantis retries",
        """
        INSERT INTO public.item_templates (
            id, kind, name_key, display_name, equipment_slot, class_ids,
            min_level, max_level, hand, skill_flag, texture, icon, stats)
        VALUES (
            3932,
            'consume item',
            'Earphone3932',
            'Opal',
            0,
            ARRAY[]::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            './Localization/en_us/UI/Texture/Icon.gwo',
            '540,900',
            jsonb_build_object(
                'ID', '3932',
                'Type', 'consume item',
                'Texture',
                    './Localization/en_us/UI/Texture/Icon.gwo',
                'Icon', '540,900',
                'Random', '0',
                'Distribution', '0,0',
                'Money', '0',
                'Overlap', '99'))
        ON CONFLICT (id) DO UPDATE
        SET kind = EXCLUDED.kind,
            name_key = EXCLUDED.name_key,
            display_name = EXCLUDED.display_name,
            equipment_slot = EXCLUDED.equipment_slot,
            class_ids = EXCLUDED.class_ids,
            min_level = EXCLUDED.min_level,
            max_level = EXCLUDED.max_level,
            hand = EXCLUDED.hand,
            skill_flag = EXCLUDED.skill_flag,
            texture = EXCLUDED.texture,
            icon = EXCLUDED.icon,
            stats = EXCLUDED.stats;

        CREATE TABLE public.legacy_instance_opal_payments (
            reservation_id uuid NOT NULL,
            realm_id smallint NOT NULL CHECK (realm_id > 0),
            account_id integer NOT NULL,
            character_id integer NOT NULL,
            item_instance_id bigint NOT NULL,
            original_kitbag_slot smallint NOT NULL
                CHECK (original_kitbag_slot BETWEEN 0 AND 95),
            quantity smallint NOT NULL CHECK (quantity = 1),
            before_state jsonb NOT NULL,
            after_state jsonb,
            before_compact_state text NOT NULL,
            after_compact_state text NOT NULL,
            charge_inventory_revision bigint NOT NULL
                CHECK (charge_inventory_revision > 0),
            charge_owner_id uuid NOT NULL,
            charge_owner_generation bigint NOT NULL
                CHECK (charge_owner_generation > 0),
            refund_inventory_revision bigint,
            refund_item_instance_id bigint,
            refund_kitbag_slot smallint,
            payment_status varchar(16) NOT NULL,
            charged_at timestamptz NOT NULL,
            admitted_at timestamptz,
            settled_at timestamptz,
            PRIMARY KEY (reservation_id, character_id),
            CONSTRAINT fk_legacy_instance_opal_payment_economy
                FOREIGN KEY (character_id, account_id)
                REFERENCES public.character_economy_baseline (
                    character_id, account_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_legacy_instance_opal_payment_template
                CHECK (((before_state ->> 'prop_id')::integer = 3932)
                    IS TRUE),
            CONSTRAINT ck_legacy_instance_opal_payment_identity CHECK ((
                (before_state ->> 'id')::bigint = item_instance_id
                AND (before_state ->> 'user_id')::integer = character_id
                AND (before_state ->> 'item_location')::smallint = 1
                AND (before_state ->> 'slot_index')::smallint =
                    original_kitbag_slot
                AND (before_state ->> 'stack')::smallint > 0) IS TRUE),
            CONSTRAINT ck_legacy_instance_opal_payment_states CHECK ((
                jsonb_typeof(before_state) = 'object'
                AND (
                    after_state IS NULL
                    OR jsonb_typeof(after_state) = 'object')
                AND (
                    ((before_state ->> 'stack')::smallint = 1
                        AND after_state IS NULL)
                    OR (
                        (before_state ->> 'stack')::smallint > 1
                        AND after_state IS NOT NULL
                        AND (after_state ->> 'id')::bigint =
                            item_instance_id
                        AND (after_state ->> 'user_id')::integer =
                            character_id
                        AND (after_state ->> 'item_location')::smallint = 1
                        AND (after_state ->> 'slot_index')::smallint =
                            original_kitbag_slot
                        AND (after_state ->> 'prop_id')::integer = 3932
                        AND (after_state ->> 'stack')::smallint =
                            (before_state ->> 'stack')::smallint - 1
                        AND (after_state - 'stack' - 'updated_at') =
                            (before_state - 'stack' - 'updated_at'))))
                IS TRUE),
            CONSTRAINT ck_legacy_instance_opal_payment_status CHECK (
                payment_status IN ('pending', 'committed', 'refunded')),
            CONSTRAINT ck_legacy_instance_opal_payment_settlement CHECK ((
                (payment_status = 'pending'
                    AND settled_at IS NULL
                    AND refund_inventory_revision IS NULL
                    AND refund_item_instance_id IS NULL
                    AND refund_kitbag_slot IS NULL)
                OR (payment_status = 'committed'
                    AND settled_at IS NOT NULL
                    AND admitted_at IS NOT NULL
                    AND refund_inventory_revision IS NULL
                    AND refund_item_instance_id IS NULL
                    AND refund_kitbag_slot IS NULL)
                OR (payment_status = 'refunded'
                    AND settled_at IS NOT NULL
                    AND admitted_at IS NULL
                    AND refund_inventory_revision >
                        charge_inventory_revision
                    AND refund_item_instance_id > 0
                    AND refund_kitbag_slot BETWEEN 0 AND 95)) IS TRUE)
        );

        CREATE INDEX ix_legacy_instance_opal_payments_pending
            ON public.legacy_instance_opal_payments (
                charged_at, reservation_id, character_id)
            WHERE payment_status = 'pending';
        """);
}
