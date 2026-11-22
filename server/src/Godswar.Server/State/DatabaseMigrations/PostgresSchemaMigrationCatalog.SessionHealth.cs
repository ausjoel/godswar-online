namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // W6A (the admin 2026-11-13): "login health checking for every player".
    // One row per connection that sent a login packet, written once when the connection ends
    // (Operations/Health/SessionHealthRecorder). endpoint 'login' = port 6999 (password check, server list,
    // redirect); endpoint 'game' = port 7000 (handshake, character screen, world entry, how the session ended).
    //  * network: the address cut to a.b.c.0 (IPv6: first 48 bits). No full address, no MAC, no payload.
    //  * stages: {"stage": milliseconds since the connection opened}; keys are code constants.
    //  * quarantined: bad item or pet rows world entry hid from the client so the login could go on.
    //  * No foreign keys on purpose: a row outlives an account wipe and can never block one.
    //  * Bounded: the server deletes rows older than 30 days (GODSWAR_SESSION_HEALTH_RETENTION_DAYS) and keeps at
    //    most 200,000 rows.
    // Read by the editor page "Player health" (grant SELECT to the editor role: editor/sql/w6a-editor-grants.sql).
    // Undo (nothing references the table):
    //   DROP TABLE public.session_health; DELETE FROM public.schema_migrations WHERE migration_id = '20261113_242_session_health';
    private static PostgresSchemaMigration CreateSessionHealth() => new(
        "20261113_242_session_health",
        "Per-connection login and session health records (bounded, 30 days)",
        """
        CREATE TABLE public.session_health (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            endpoint text NOT NULL CHECK (endpoint IN ('login', 'game')),
            realm_id integer NOT NULL DEFAULT 1,
            started_at timestamptz NOT NULL,
            duration_ms bigint NOT NULL DEFAULT 0 CHECK (duration_ms >= 0),
            account_id integer NULL,
            character_id integer NULL,
            client_build bigint NULL,
            network text NULL CHECK (length(network) <= 64),
            outcome text NOT NULL CHECK (length(outcome) BETWEEN 1 AND 32),
            reason text NULL CHECK (length(reason) <= 64),
            error_type text NULL CHECK (length(error_type) <= 96),
            error_text text NULL CHECK (length(error_text) <= 300),
            transport_error text NULL CHECK (length(transport_error) <= 96),
            last_opcode integer NOT NULL DEFAULT 0,
            last_send_opcode integer NOT NULL DEFAULT 0,
            stages jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(stages) = 'object'),
            quarantined text NULL CHECK (length(quarantined) <= 300)
        );
        CREATE INDEX ix_session_health_started ON public.session_health (started_at);
        CREATE INDEX ix_session_health_account ON public.session_health (account_id, started_at);
        COMMENT ON TABLE public.session_health IS
            'W6A: one row per login-port or game-port connection that sent a login packet. Written by the game '
            'server when the connection ends; pruned after 30 days. network is a truncated address.';
        """);
}
