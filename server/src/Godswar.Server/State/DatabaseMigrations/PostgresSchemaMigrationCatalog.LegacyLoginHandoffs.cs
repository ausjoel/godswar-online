namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // ROUND-SHAREDHANDOFF (issue #34): verified password-login admissions shared by every process of a realm set,
    // used only when GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=postgres. One row per (account, realm, address); the
    // game worker deletes it when it admits the login, so a record is used at most once. Expiry uses the database
    // clock and is bounded to 15 minutes by a check.
    // Undo: DROP TABLE public.legacy_game_login_handoffs; then delete this migration row.
    private static PostgresSchemaMigration CreateLegacyLoginHandoffs() => new(
        "20261106_239_legacy_game_login_handoffs",
        "Shared one-time legacy game login handoffs across server processes",
        """
        CREATE TABLE public.legacy_game_login_handoffs (
            account_id bigint NOT NULL,
            realm_id integer NOT NULL CHECK (realm_id > 0),
            address varchar(45) NOT NULL CHECK (btrim(address) <> ''),
            issued_at timestamptz NOT NULL DEFAULT now(),
            expires_at timestamptz NOT NULL,
            PRIMARY KEY (account_id, realm_id, address),
            CONSTRAINT ck_legacy_game_login_handoffs_lifetime
                CHECK (expires_at > issued_at AND expires_at <= issued_at + interval '15 minutes')
        );

        CREATE INDEX ix_legacy_game_login_handoffs_expires
            ON public.legacy_game_login_handoffs (expires_at);
        """);
}
