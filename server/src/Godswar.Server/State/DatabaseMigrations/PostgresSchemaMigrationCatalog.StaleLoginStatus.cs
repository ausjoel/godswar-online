namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // W14: one-off clean-up of stale legacy login flags. Before W14 the login server (6999) set
    // accounts.login_status = 1 on every accepted password check, and only a game-session end cleared it, so an
    // account that logged in but never reached the game port stayed "online" for good (live had one, id 46). From
    // W14 the game session sets the flag (GameClientHandler.LoginWorldEntry), so no new ones appear; this clears the
    // ones already there. It runs once, at the server start that applies it.
    // It never touches a player who may really be in the game:
    //  * login_presence_token IS NULL: a token means a coordinated player lease owns the flag;
    //  * no character of the account holds a checkpoint owner (a world-entered character always does);
    //  * the last login is more than 10 minutes old (a character-screen session on another process, if any, is
    //    at worst shown offline until its next login; nothing is written to its characters).
    // Undo: the data needs none (a cleared flag is set again by the next game login). ROLLBACK to a build
    // without this migration (W13 or older) fails closed at start ("Migration history is ahead of this
    // server") unless the row goes first:
    //   DELETE FROM public.schema_migrations WHERE migration_id = '20261122_243_clear_stale_login_status';
    private static PostgresSchemaMigration CreateClearStaleLoginStatus() => new(
        "20261122_243_clear_stale_login_status",
        "Clear token-less legacy login flags left by logins that never entered the game",
        """
        UPDATE public.accounts AS a
        SET login_status = 0,
            last_logout_time = now()
        WHERE a.login_status <> 0
          AND a.login_presence_token IS NULL
          AND a.last_login_time < now() - interval '10 minutes'
          AND NOT EXISTS (
              SELECT 1
              FROM public.character_base AS c
              WHERE c.account_id = a.id
                AND c.checkpoint_owner_id IS NOT NULL);
        """);
}
