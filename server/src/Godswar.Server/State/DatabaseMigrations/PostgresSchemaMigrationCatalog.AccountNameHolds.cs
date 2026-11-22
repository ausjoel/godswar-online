namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // W4A (wave 4 package A, the admin 2026-11-12): account soft delete and user-name holds.
    //  * account_username_holds: a user name that may not be registered again (the old name of a renamed account,
    //    the name of a deleted account, or one reserved by hand). Keyed by lower(name): first-login registration
    //    (PostgresAccountStore.TryCreateAccountWithCredentialAsync), the sign-up site and the editor all refuse a
    //    held name. No foreign key: a hold outlives the purge of its account on purpose.
    //  * account_deletions: an account deleted in the editor stays for 30 days (accounts.status = 2, so login and
    //    world entry refuse it like a ban) with a Restore button; the VPS timer gw-account-purge purges it after
    //    purge_after (DB backup first). ON DELETE CASCADE: a purge or hard delete of the account removes the row.
    // Additive only; nothing reads these tables unless the row exists.
    // Undo: DROP TABLE public.account_deletions, public.account_username_holds; then delete this migration row.
    private static PostgresSchemaMigration CreateAccountNameHolds() => new(
        "20261112_240_account_name_holds",
        "User-name holds (rename / delete) and 30-day account soft delete",
        """
        CREATE TABLE public.account_username_holds (
            username_key varchar(32) PRIMARY KEY,
            username varchar(32) NOT NULL,
            account_id integer,
            reason varchar(16) NOT NULL,
            held_at timestamptz NOT NULL DEFAULT now(),
            note varchar(200) NOT NULL DEFAULT '',
            CONSTRAINT ck_account_username_holds_key CHECK (username_key = lower(username)),
            CONSTRAINT ck_account_username_holds_reason CHECK (reason IN ('renamed', 'deleted', 'reserved'))
        );

        CREATE INDEX ix_account_username_holds_account
            ON public.account_username_holds (account_id);

        CREATE TABLE public.account_deletions (
            account_id integer PRIMARY KEY REFERENCES public.accounts(id) ON DELETE CASCADE,
            username varchar(32) NOT NULL,
            previous_status smallint NOT NULL,
            deleted_at timestamptz NOT NULL DEFAULT now(),
            purge_after timestamptz NOT NULL,
            deleted_by varchar(64) NOT NULL DEFAULT '',
            CONSTRAINT ck_account_deletions_window CHECK (purge_after > deleted_at),
            CONSTRAINT ck_account_deletions_previous_status CHECK (previous_status <> 2)
        );

        CREATE INDEX ix_account_deletions_purge_after
            ON public.account_deletions (purge_after);
        """);
}
