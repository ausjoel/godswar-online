using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rates;

// Like server_rates, these are NOT registered schema migrations: idempotent DDL at start-up, so a binary without this
// feature can still start against a database this build has used. A failure keeps the last good snapshot.
internal static class NpcContentRuntime
{
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.npc_shop_overrides (
            service text NOT NULL,
            position integer NOT NULL,
            item_id integer NOT NULL CHECK (item_id > 0),
            price integer NOT NULL CHECK (price > 0),
            category smallint NOT NULL CHECK (category BETWEEN 0 AND 255),
            quality smallint NOT NULL DEFAULT 1 CHECK (quality BETWEEN 1 AND 20),
            grade smallint NOT NULL DEFAULT 1 CHECK (grade BETWEEN 1 AND 25),
            bound smallint NOT NULL DEFAULT 0 CHECK (bound BETWEEN 0 AND 1),
            stack_marker smallint NOT NULL DEFAULT 1 CHECK (stack_marker BETWEEN 1 AND 255),
            PRIMARY KEY (service, position)
        );

        CREATE TABLE IF NOT EXISTS public.npc_teleport_overrides (
            npc_key text NOT NULL,
            sub_id integer NOT NULL,
            target_map_id smallint NOT NULL CHECK (target_map_id BETWEEN 0 AND 255),
            pos_x real NOT NULL CHECK (pos_x >= -256 AND pos_x < 256),
            pos_z real NOT NULL CHECK (pos_z > -256 AND pos_z <= 256),
            minimum_level integer NOT NULL DEFAULT 1 CHECK (minimum_level BETWEEN 1 AND 255),
            PRIMARY KEY (npc_key, sub_id)
        );

        CREATE TABLE IF NOT EXISTS public.item_activators (
            item_id integer PRIMARY KEY CHECK (item_id > 0),
            skill_id integer NOT NULL CHECK (skill_id > 0),
            cooldown_seconds integer NOT NULL CHECK (cooldown_seconds >= 0)
        );

        CREATE TABLE IF NOT EXISTS public.event_schedule_overrides (
            activity text NOT NULL,
            day_of_week smallint NOT NULL CHECK (day_of_week BETWEEN -1 AND 6),
            start_minute smallint NOT NULL CHECK (start_minute BETWEEN 0 AND 1439),
            duration_minutes smallint NOT NULL CHECK (duration_minutes BETWEEN 1 AND 1440),
            PRIMARY KEY (activity, day_of_week, start_minute)
        );

        CREATE TABLE IF NOT EXISTS public.server_notices (
            id integer PRIMARY KEY CHECK (id > 0),
            message text NOT NULL CHECK (char_length(message) BETWEEN 1 AND 106),
            delivery text NOT NULL CHECK (delivery IN ('banner', 'log', 'board', 'chat')),
            day_mask smallint NOT NULL CHECK (day_mask BETWEEN 1 AND 127),
            start_minute smallint NOT NULL CHECK (start_minute BETWEEN 0 AND 1439),
            end_minute smallint NOT NULL CHECK (end_minute BETWEEN 0 AND 1439),
            repeat_minutes smallint NOT NULL CHECK (repeat_minutes BETWEEN 0 AND 1439),
            first_day date,
            last_day date,
            enabled boolean NOT NULL DEFAULT true
        );

        -- FINALISE (the admin 2026-11-05): editable level bands for transporter/instance gates; no row = compiled band.
        CREATE TABLE IF NOT EXISTS public.level_band_overrides (
            gate text PRIMARY KEY,
            minimum_level smallint CHECK (minimum_level BETWEEN 1 AND 255),
            maximum_level smallint CHECK (maximum_level BETWEEN 1 AND 255),
            note text NOT NULL DEFAULT '',
            CHECK (minimum_level IS NULL OR maximum_level IS NULL OR minimum_level <= maximum_level)
        );

        -- FINALISE2 (the admin F-18): the full gates table = level band + opening mode + per-gate GM rule. Windows stay in
        -- event_schedule_overrides (activity = gate key; Pindus / NiMini / LelantineFarm keep their names).
        ALTER TABLE public.level_band_overrides
            ADD COLUMN IF NOT EXISTS schedule_mode text NOT NULL DEFAULT 'compiled',
            ADD COLUMN IF NOT EXISTS gm_bypass boolean;
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_level_band_overrides_schedule_mode') THEN
                ALTER TABLE public.level_band_overrides ADD CONSTRAINT ck_level_band_overrides_schedule_mode
                    CHECK (schedule_mode IN ('compiled', 'always', 'windows', 'closed'));
            END IF;
        END $$;

        -- FINALISE (the admin 2026-11-05, Elasson): per-map/tier loot gate and random respawn window. map_id -1 = all maps.
        CREATE TABLE IF NOT EXISTS public.drop_tier_overrides (
            map_id smallint NOT NULL CHECK (map_id BETWEEN -1 AND 255),
            tier text NOT NULL CHECK (tier IN ('normal', 'elite', 'boss')),
            any_drop_basis_points integer NOT NULL CHECK (any_drop_basis_points BETWEEN 0 AND 10000),
            note text NOT NULL DEFAULT '',
            PRIMARY KEY (map_id, tier)
        );

        CREATE TABLE IF NOT EXISTS public.respawn_tier_overrides (
            map_id smallint NOT NULL CHECK (map_id BETWEEN -1 AND 255),
            tier text NOT NULL CHECK (tier IN ('normal', 'elite')),
            minimum_seconds integer NOT NULL CHECK (minimum_seconds BETWEEN 15 AND 86400),
            maximum_seconds integer NOT NULL CHECK (maximum_seconds BETWEEN 15 AND 86400),
            note text NOT NULL DEFAULT '',
            PRIMARY KEY (map_id, tier),
            CHECK (minimum_seconds <= maximum_seconds)
        );

        CREATE TABLE IF NOT EXISTS public.notice_settings (
            name text PRIMARY KEY,
            value integer NOT NULL
        );

        -- the admin 2026-11-08: notices may go to the board; 2026-11-10: or to the chat box ('chat'). Widen an older table's
        -- check (an older binary reads 'board' and 'chat' as 'log').
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.server_notices'::regclass
                       AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%delivery%' AND pg_get_constraintdef(oid) NOT LIKE '%chat%') THEN
                EXECUTE (SELECT 'ALTER TABLE public.server_notices DROP CONSTRAINT ' || quote_ident(conname) FROM pg_constraint
                         WHERE conrelid = 'public.server_notices'::regclass AND contype = 'c'
                         AND pg_get_constraintdef(oid) LIKE '%delivery%' LIMIT 1);
                ALTER TABLE public.server_notices ADD CONSTRAINT server_notices_delivery_check
                    CHECK (delivery IN ('banner', 'log', 'board', 'chat'));
            END IF;
        END $$;

        -- the admin 2026-11-10: editable server messages. 'welcome' = one board line per login; off by default.
        CREATE TABLE IF NOT EXISTS public.server_messages (
            name text PRIMARY KEY CHECK (name IN ('welcome')),
            message text NOT NULL DEFAULT '' CHECK (char_length(message) <= 106 AND message ~ '^[ -~]*$'),
            enabled boolean NOT NULL DEFAULT false
        );
        INSERT INTO public.server_messages (name) VALUES ('welcome') ON CONFLICT (name) DO NOTHING;
        -- W3 seasonal events (F-26): one row per season, all switched off; the editor sets on/off and the dates.
        CREATE TABLE IF NOT EXISTS public.seasonal_events (
            event_key text PRIMARY KEY,
            enabled boolean NOT NULL DEFAULT false,
            starts_at timestamptz,
            ends_at timestamptz,
            yearly boolean NOT NULL DEFAULT true,
            updated_at timestamptz NOT NULL DEFAULT now(),
            CHECK (starts_at IS NULL OR ends_at IS NULL OR ends_at > starts_at)
        );
        INSERT INTO public.seasonal_events (event_key) VALUES ('spring_festival'), ('carnival'), ('easter'), ('may_day'),
            ('igg_anniversary'), ('mid_autumn'), ('oriental_cuisine'), ('halloween'), ('thanksgiving'), ('christmas')
            ON CONFLICT (event_key) DO NOTHING;
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'gweditor') THEN
                GRANT SELECT, INSERT, UPDATE, DELETE ON public.seasonal_events TO gweditor;
            END IF;
        END $$;
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'gweditor') THEN
                GRANT SELECT, INSERT, UPDATE, DELETE ON public.npc_shop_overrides, public.npc_teleport_overrides, public.item_activators, public.event_schedule_overrides, public.server_notices, public.notice_settings, public.server_messages, public.level_band_overrides, public.drop_tier_overrides, public.respawn_tier_overrides TO gweditor;
            END IF;
        END $$;
        """;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static NpgsqlDataSource? _dataSource;
    private static bool _ensured;
    private static long _revision;
    private static string _loaded = string.Empty;

    public static RealmCalendar? Calendar { get; set; }

    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        NpcContentOverrides.DescribeHook = DescribeAsync;
        NpcContentOverrides.ReloadHook = ReloadForManagementAsync;
        NpcContentOverrides.DescribeShopHook = DescribeShopAsync;
        NpcContentOverrides.DescribeEventsHook = DescribeEventsAsync;
        NpcContentOverrides.DescribeGatesHook = DescribeGatesAsync; // FINALISE2
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
            await ReloadAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[npc-content] start-up load failed; using compiled content: {error.GetType().Name}: {error.Message}");
        }
    }

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken);
                await ReloadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Console.WriteLine($"[npc-content] reload failed; keeping revision {_revision}: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    private static async Task<string> ReloadForManagementAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken);
        return JsonSerializer.Serialize(new { revision = NpcContentOverrides.Current.Revision });
    }

    private static async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var dataSource = _dataSource ?? throw new InvalidOperationException("The NPC content runtime is not initialised.");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            if (!_ensured)
            {
                await using var ddl = new NpgsqlCommand(SchemaSql, connection);
                await ddl.ExecuteNonQueryAsync(cancellationToken);
                _ensured = true;
            }

            var shops = new Dictionary<string, List<NpcShopOverrideRow>>(StringComparer.Ordinal);
            var fingerprint = new System.Text.StringBuilder();
            await using (var command = new NpgsqlCommand(
                "SELECT service, item_id, price, category, quality, grade, bound, stack_marker FROM public.npc_shop_overrides ORDER BY service, position",
                connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var service = reader.GetString(0);
                    var row = new NpcShopOverrideRow(
                        checked((uint)reader.GetInt32(1)),
                        reader.GetInt32(2),
                        checked((byte)reader.GetInt16(3)),
                        checked((byte)reader.GetInt16(4)),
                        checked((byte)reader.GetInt16(5)),
                        checked((byte)reader.GetInt16(6)),
                        checked((byte)reader.GetInt16(7)));
                    if (!shops.TryGetValue(service, out var list))
                    {
                        shops[service] = list = [];
                    }

                    list.Add(row);
                    fingerprint.Append(service).Append(row).Append('\n');
                }
            }

            // W3 seasons: installed on every poll (a date window opens/closes without an edit).
            await using (var command = new NpgsqlCommand(
                "SELECT string_agg(event_key || ':' || enabled::int || ':' || coalesce(extract(epoch FROM starts_at)::bigint, 0) || ':' || " +
                "coalesce(extract(epoch FROM ends_at)::bigint, 0) || ':' || yearly::int, ',') FROM public.seasonal_events",
                connection))
            {
                var seasons = await command.ExecuteScalarAsync(cancellationToken) as string;
                Godswar.Server.Application.Seasons.SeasonalEvents.Install(
                    Godswar.Server.Application.Seasons.SeasonalEvents.Parse(seasons));
                fingerprint.Append(seasons).Append('\n');
            }

            var teleports = new List<NpcTeleportOverride>();
            await using (var command = new NpgsqlCommand(
                "SELECT npc_key, sub_id, target_map_id, pos_x, pos_z, minimum_level FROM public.npc_teleport_overrides",
                connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var teleport = new NpcTeleportOverride(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetInt16(2),
                        reader.GetFloat(3),
                        reader.GetFloat(4),
                        reader.GetInt32(5));
                    teleports.Add(teleport);
                    fingerprint.Append(teleport).Append('\n');
                }
            }

            var editorAdmins = new List<int>();
            await using (var command = new NpgsqlCommand("SELECT id FROM public.accounts WHERE editor_admin", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    editorAdmins.Add(reader.GetInt32(0));
                    fingerprint.Append("admin").Append(reader.GetInt32(0)).Append('\n');
                }
            }

            var activators = new Dictionary<uint, ItemActivator>();
            await using (var command = new NpgsqlCommand("SELECT item_id, skill_id, cooldown_seconds FROM public.item_activators", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var activator = new ItemActivator(reader.GetInt32(1), reader.GetInt32(2));
                    activators[checked((uint)reader.GetInt32(0))] = activator;
                    fingerprint.Append(reader.GetInt32(0)).Append(activator).Append('\n');
                }
            }

            var windows = new Dictionary<string, List<EventWindow>>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand("SELECT activity, day_of_week, start_minute, duration_minutes FROM public.event_schedule_overrides ORDER BY 1, 2, 3", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var activity = reader.GetString(0);
                    var window = new EventWindow(reader.GetInt16(1), reader.GetInt16(2), reader.GetInt16(3));
                    if (!windows.TryGetValue(activity, out var list))
                    {
                        windows[activity] = list = [];
                    }

                    list.Add(window);
                    fingerprint.Append(activity).Append(window).Append('\n');
                }
            }

            var notices = new List<ServerNotice>();
            await using (var command = new NpgsqlCommand("SELECT id, message, delivery, day_mask, start_minute, end_minute, repeat_minutes, first_day, last_day FROM public.server_notices WHERE enabled ORDER BY id", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var notice = new ServerNotice(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2) == "banner",
                        reader.GetInt16(3),
                        reader.GetInt16(4),
                        reader.GetInt16(5),
                        reader.GetInt16(6),
                        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7),
                        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8),
                        reader.GetString(2) == "board",
                        reader.GetString(2) == "chat");
                    notices.Add(notice);
                    fingerprint.Append(notice).Append('\n');
                }
            }

            var levelBands = new Dictionary<string, LevelBandOverride>(StringComparer.Ordinal);
            var unknownGates = new List<string>(); // FINALISE2
            await using (var command = new NpgsqlCommand("SELECT gate, minimum_level, maximum_level, schedule_mode, gm_bypass FROM public.level_band_overrides ORDER BY gate", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    // FINALISE2: schedule mode and GM rule; an unknown key is ignored (one warning per reload).
                    var band = new LevelBandOverride(
                        reader.IsDBNull(1) ? null : reader.GetInt16(1),
                        reader.IsDBNull(2) ? null : reader.GetInt16(2), // FINALISE2
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetBoolean(4));
                    if (Godswar.Server.Application.Gates.GateCatalog.Find(reader.GetString(0)) is null &&
                        reader.GetString(0) != LevelBandGates.NiMiniLower)
                    {
                        unknownGates.Add(reader.GetString(0));
                    }

                    levelBands[reader.GetString(0)] = band;
                    fingerprint.Append("band").Append(reader.GetString(0)).Append(band).Append('\n');
                }
            }

            var dropTiers = new Dictionary<(short, string), DropTierOverride>();
            await using (var command = new NpgsqlCommand("SELECT map_id, tier, any_drop_basis_points FROM public.drop_tier_overrides ORDER BY 1, 2", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    dropTiers[(reader.GetInt16(0), reader.GetString(1))] = new DropTierOverride(reader.GetInt32(2));
                    fingerprint.Append("drop").Append(reader.GetInt16(0)).Append(reader.GetString(1)).Append(reader.GetInt32(2)).Append('\n');
                }
            }

            var respawnTiers = new Dictionary<(short, string), RespawnTierOverride>();
            await using (var command = new NpgsqlCommand("SELECT map_id, tier, minimum_seconds, maximum_seconds FROM public.respawn_tier_overrides ORDER BY 1, 2", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    respawnTiers[(reader.GetInt16(0), reader.GetString(1))] = new RespawnTierOverride(reader.GetInt32(2), reader.GetInt32(3));
                    fingerprint.Append("respawn").Append(reader.GetInt16(0)).Append(reader.GetString(1)).Append(reader.GetInt32(2)).Append('-').Append(reader.GetInt32(3)).Append('\n');
                }
            }

            var populationMinutes = EventContent.DefaultPopulationMinutes;
            await using (var command = new NpgsqlCommand("SELECT value FROM public.notice_settings WHERE name = 'population_minutes'", connection))
            {
                if (await command.ExecuteScalarAsync(cancellationToken) is int stored)
                {
                    populationMinutes = stored;
                }
            }

            fingerprint.Append("population").Append(populationMinutes).Append('\n');

            string? welcomeText = null; // the admin 2026-11-10
            await using (var command = new NpgsqlCommand("SELECT message FROM public.server_messages WHERE name = 'welcome' AND enabled", connection))
            {
                if (await command.ExecuteScalarAsync(cancellationToken) is string stored)
                {
                    var clean = new string(stored.Where(static c => c is >= ' ' and <= '~').ToArray()).Trim();
                    welcomeText = clean.Length == 0 ? null : clean;
                }
            }

            fingerprint.Append("welcome").Append(welcomeText).Append('\n');

            var loaded = fingerprint.ToString();
            if (loaded == _loaded)
            {
                return;
            }

            _loaded = loaded;
            _revision++;
            NpcContentOverrides.Install(new NpcContentSnapshot(
                _revision,
                shops.ToDictionary(static p => p.Key, static p => (IReadOnlyList<NpcShopOverrideRow>)p.Value, StringComparer.Ordinal),
                teleports,
                editorAdmins,
                activators,
                new EventContent(
                    windows.ToDictionary(static p => p.Key, static p => (IReadOnlyList<EventWindow>)p.Value, StringComparer.Ordinal),
                    notices,
                    populationMinutes,
                    welcomeText),
                levelBands,
                dropTiers,
                respawnTiers));
            if (unknownGates.Count > 0)
            {
                Console.WriteLine($"[npc-content] ignored level_band_overrides rows for unknown gates: {string.Join(", ", unknownGates)}"); // FINALISE2
            }

            Console.WriteLine($"[npc-content] loaded revision {_revision}: {shops.Count} shops, {teleports.Count} teleports, {levelBands.Count} level bands, {dropTiers.Count} drop tiers, {respawnTiers.Count} respawn tiers");
        }
        finally
        {
            Gate.Release();
        }
    }

    // FINALISE2: GET /gate-content (Gates page of the editor).
    private static Task<string> DescribeGatesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Godswar.Server.Application.Gates.GateSchedules.Describe(Calendar, DateTimeOffset.UtcNow));

    private static Task<string> DescribeEventsAsync(CancellationToken cancellationToken)
    {
        var rates = ServerRates.Current;
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            revision = NpcContentOverrides.Current.Revision,
            realm_time = Calendar?.ToRealmTime(DateTimeOffset.UtcNow).ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            time_zone = Calendar?.TimeZoneId,
            population_default = EventContent.DefaultPopulationMinutes,
            window_minutes = rates.Absolute(ServerRateKeys.BattlefieldWindowMinutes),
            shift_hours = rates.Absolute(ServerRateKeys.BattlefieldShiftHours),
            activities = new object[]
            {
                new
                {
                    key = EventContent.Pindus,
                    name = "Pindus Mountains battlefield",
                    always_open = rates.Absolute(ServerRateKeys.PindusAlwaysOpen) == 1,
                    compiled = BattlefieldSchedulePolicy.DescribeCompiled(BattlefieldDestinationKind.Pindus).ToArray()
                },
                new
                {
                    key = EventContent.NiMini,
                    name = "Ni Mini Valley",
                    always_open = rates.Absolute(ServerRateKeys.NiMiniAlwaysOpen) == 1,
                    compiled = BattlefieldSchedulePolicy.DescribeCompiled(BattlefieldDestinationKind.NiMiniLower).ToArray()
                }
            }
        }));
    }

    // One shop at a time: the management port limits a response to 64 KiB.
    private static Task<string> DescribeShopAsync(string name, CancellationToken cancellationToken)
    {
        IReadOnlyList<object> rows = [];
        if (name is PacketBuilder.MallAthensService or PacketBuilder.MallSpartaService)
            return Task.FromResult(JsonSerializer.Serialize(PacketBuilder.DescribeCompiledMall(
                name == PacketBuilder.MallAthensService ? GameDefaults.AthensCamp : GameDefaults.SpartaCamp)));
        try
        {
            if (Enum.TryParse<CapitalNpcServiceKind>(name, out var service) && CapitalNpcServiceProtocol.IsShop(service))
            {
                rows = PacketBuilder.DescribeCompiledCapitalShop(service);
            }
        }
        catch (ArgumentException)
        {
            // A malformed captured stream has nothing to edit.
        }

        return Task.FromResult(JsonSerializer.Serialize(rows));
    }

    // Everything the editor needs, from the same code the game uses: compiled shop lists, teleporter buttons, which NPC runs which shop.
    private static async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var dataSource = _dataSource ?? throw new InvalidOperationException("The NPC content runtime is not initialised.");
        var shops = Enum.GetValues<CapitalNpcServiceKind>()
            .Where(static service => CapitalNpcServiceProtocol.IsShop(service))
            .Select(static service => service.ToString())
            .ToArray().Concat([PacketBuilder.MallAthensService, PacketBuilder.MallSpartaService, PacketBuilder.MallAdminService]);

        var npcs = new List<object>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(
            """
            SELECT d.map_id, d.npc_key, d.template_key, d.object_id, d.interaction_id, d.pos_x, d.pos_z
            FROM public.npc_spawn_definitions d
            JOIN public.npc_content_publication p ON p.family = 'npcs' AND p.revision = d.revision
            ORDER BY d.map_id, d.npc_key
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(1);
                var interaction = Convert.ToUInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
                // Published interaction ids can sit one away from the captured ids the protocol names.
                var isService = CapitalNpcServiceProtocol.TryResolve(key, interaction, out var service) ||
                    CapitalNpcServiceProtocol.TryResolve(key, interaction - 1, out service) ||
                    CapitalNpcServiceProtocol.TryResolve(key, interaction + 1, out service);
                npcs.Add(new
                {
                    map = (int)reader.GetInt16(0),
                    key,
                    template = reader.GetString(2),
                    x = reader.GetFloat(5),
                    z = reader.GetFloat(6),
                    service = isService ? service.ToString() : null,
                    shop = isService && CapitalNpcServiceProtocol.IsShop(service),
                    transporter = TransporterProtocol.Destinations.Any(d => d.NpcKey == key) ||
                        BattlefieldTransporterProtocol.IsEditableNpc(key) ||
                        key is DuelArenaCapturedLayout.GatekeeperNpcKey or DuelArenaCapturedLayout.WardNpcKey
                });
            }
        }

        var snapshot = NpcContentOverrides.Current;
        return JsonSerializer.Serialize(new
        {
            revision = snapshot.Revision,
            shops,
            npcs,
            teleports = TransporterProtocol.Destinations.Select(static d => new
            {
                npc = d.NpcKey,
                sub = d.SubId,
                name = d.DisplayName,
                source_map = (int)d.SourceMapId,
                target_map = (int)d.TargetMapId,
                level = d.MinimumLevel,
                x = (float?)null,
                z = (float?)null
            }).Concat(BattlefieldTransporterProtocol.EditableButtons().Select(static b => new
            {
                npc = b.NpcKey,
                sub = b.SubId,
                name = b.Destination.DisplayName,
                source_map = b.Destination.SourceMapId,
                target_map = b.Destination.TargetMapId,
                level = b.Destination.MinimumLevel,
                x = (float?)b.Destination.TargetX,
                z = (float?)b.Destination.TargetZ
            })).Concat(new[]
            {
                new
                {
                    npc = DuelArenaCapturedLayout.GatekeeperNpcKey,
                    sub = DuelArenaCapturedTransportProtocol.TravelSubId,
                    name = "Into the arena (normally a random entry point)",
                    source_map = (int)DuelArenaCapturedLayout.MapId,
                    target_map = (int)DuelArenaCapturedLayout.MapId,
                    level = 1,
                    x = (float?)null,
                    z = (float?)null
                },
                new
                {
                    npc = DuelArenaCapturedLayout.WardNpcKey,
                    sub = DuelArenaCapturedTransportProtocol.TravelSubId,
                    name = "Back to the arena lobby",
                    source_map = (int)DuelArenaCapturedLayout.MapId,
                    target_map = (int)DuelArenaCapturedLayout.MapId,
                    level = 1,
                    x = (float?)DuelArenaCapturedLayout.UpperArrivalX,
                    z = (float?)DuelArenaCapturedLayout.UpperArrivalZ
                }
            })
        });
    }
}
