namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateExchangePointBalances() => new(
        "20260908_144_exchange_point_balances",
        "Persist Point Exchanger point and medal balances",
        """
        ALTER TABLE character_base
            ADD COLUMN IF NOT EXISTS exchange_point integer
                NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS exchange_medal integer
                NOT NULL DEFAULT 0;

        DO $exchange_point_guards$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_character_exchange_point_nonnegative'
            ) THEN
                ALTER TABLE character_base
                    ADD CONSTRAINT ck_character_exchange_point_nonnegative
                    CHECK (exchange_point >= 0);
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_character_exchange_medal_nonnegative'
            ) THEN
                ALTER TABLE character_base
                    ADD CONSTRAINT ck_character_exchange_medal_nonnegative
                    CHECK (exchange_medal >= 0);
            END IF;
        END
        $exchange_point_guards$;

        -- The Point Exchanger charges three balances. Honor reuses the Medusa
        -- completion reward column; point and medal are new. The shared wallet
        -- ledger must accept their codes or every purchase rolls back.
        ALTER TABLE public.character_currency_ledger
            DROP CONSTRAINT IF EXISTS
                ck_character_currency_ledger_currency;
        ALTER TABLE public.character_currency_ledger
            ADD CONSTRAINT ck_character_currency_ledger_currency CHECK (
                currency_code IN (
                    'silver', 'gold', 'binding_gold',
                    'honor', 'exchange_point', 'exchange_medal'
                )
            );
        """);
}
