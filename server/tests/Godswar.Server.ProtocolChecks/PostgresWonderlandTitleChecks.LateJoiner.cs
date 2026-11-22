using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Regression for the 5 Nov 2026 party run (instance b87f263a...): two characters entered on the
/// party's reservation, islands 1-3 settled, then a third character confirmed the party window and
/// joined on a reservation of its own. Islands 4-5 then retried ~9,800 times with "Legacy-instance
/// admissions did not match the reserved party members exactly" and never paid titles or medals.
/// This drives the real retry loop against the real PostgreSQL stores in that order.
/// </summary>
internal static partial class PostgresWonderlandTitleChecks
{
    public const string LateJoinerCheckName =
        "PostgreSQL Wonderland titles and medals settle for a member who joined after the run was sealed";

    public static async Task RunLateJoinerAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new CheckSkippedException(LateJoinerCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!System.Text.RegularExpressions.Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$"))
                throw new CheckSkippedException(LateJoinerCheckName + " requires a disposable database");
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        // Schema only: the full seed is not needed (it fails on a fresh database at pet item 10023, unrelated).
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.WonderlandMedals, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            await CheckLateJoinerSettlesAsync(source);
            await CheckLateJoinerFencesAsync(source);
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
    }

    private static async Task CheckLateJoinerSettlesAsync(NpgsqlDataSource source)
    {
        var members = await CreateMembersAsync(source, 3);
        var party = members.Take(2).ToArray();
        var joiner = members[2];
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        started = new DateTimeOffset(started.UtcTicks - started.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var instance = WorldInstanceId.New();
        var partyReservation = Guid.NewGuid();
        var joinReservation = Guid.NewGuid();
        // The party's own claim: claimed before the start, admitted at entry (as live: claimed_at 14:53:05.70).
        foreach (var member in party)
            await InsertEntryAsync(source, member.CharacterId, partyReservation, started.AddSeconds(-1), started.AddSeconds(1));

        var registry = new GameSessionRegistry();
        registry.ConfigureWonderlandTitles(new PostgresWonderlandTitleStore(source),
            new PostgresLegacyInstanceDailyEntryClaimStore(source));
        registry.ConfigureWonderlandMedals(new PostgresWonderlandMedalStore(source));

        // Island 1 clears with the registered party only; this binds wonderland_title_runs.
        var island1 = new WonderlandTitleRequest(instance, RealmId.Tempest, partyReservation, started,
            started.AddMinutes(2), 1, party, party, [partyReservation]);
        Check.True(registry.QueueWonderlandTitleMilestone(island1), "island 1 queued");
        await SettleQueueAsync(registry, started.AddMinutes(3));
        Check.True(!registry.HasPendingWonderlandTitles(instance), "island 1 settles for the registered party");

        // The third member confirms the party window after the seal and claims a reservation of his own
        // (live: claimed_at 15:03:27, ten minutes after the start), admitted at the transfer.
        await InsertEntryAsync(source, joiner.CharacterId, joinReservation, started.AddMinutes(10), started.AddMinutes(10).AddSeconds(1));

        // Island 4 clears with all three inside, exactly as GameSessionRegistry.WonderlandRun builds it.
        var island4 = LateJoinerRequest(instance, partyReservation, started, started.AddMinutes(15), 4, party, joiner, joinReservation);
        Check.True(registry.QueueWonderlandTitleMilestone(island4), "island 4 queued");
        var errors = await SettleQueueAsync(registry, started.AddMinutes(16));
        Check.True(!registry.HasPendingWonderlandTitles(instance),
            "island 4 settles with a late joiner on its own reservation; retry log: " + errors);
        await SettleQueueAsync(registry, started.AddMinutes(17)); // medals are paid by the following tick

        await using (var titles = source.CreateCommand("""
            SELECT character_id FROM public.wonderland_title_members
            WHERE world_instance_id=@instance AND island_number=4 ORDER BY character_id;
            """))
        {
            titles.Parameters.AddWithValue("instance", instance.Value);
            var paid = new List<int>();
            await using var reader = await titles.ExecuteReaderAsync();
            while (await reader.ReadAsync()) paid.Add(reader.GetInt32(0));
            Check.True(paid.SequenceEqual(members.Select(member => member.CharacterId).Order()),
                "all three finishers, including the late joiner, own the island-4 title");
        }
        Check.Equal(3, await CountAsync(source, instance, 4, "wonderland_medal_awards", "island"),
            "all three finishers are paid island-4 medals once");
        Check.Equal(2, await CountAsync(source, instance, 1, "wonderland_medal_awards", "island"),
            "the late joiner is not back-paid for an island cleared before he joined");
        Check.Equal(90, await MedalsAsync(source, joiner.CharacterId), "the late joiner's balance moved by island 4 only");

        // The run identity is stable while the roster grows: an earlier island's evidence still replays as a duplicate.
        var replay = await new PostgresWonderlandTitleStore(source).SettleAsync(island1);
        CheckStatus(WonderlandTitleStatus.Duplicate, replay.Status, "island 1 replays as a duplicate after the roster grew");
        // And a later island with the same three settles too (live island 5).
        var island5 = LateJoinerRequest(instance, partyReservation, started, started.AddMinutes(20), 5, party, joiner, joinReservation);
        var five = await new PostgresWonderlandTitleStore(source).SettleAsync(island5);
        CheckStatus(WonderlandTitleStatus.Applied, five.Status, "island 5 settles for all three");
        Check.Equal(3, five.Members.Count, "island 5 pays three members");
    }

    private static async Task CheckLateJoinerFencesAsync(NpgsqlDataSource source)
    {
        var members = await CreateMembersAsync(source, 3);
        var party = members.Take(2).ToArray();
        var joiner = members[2];
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        started = new DateTimeOffset(started.UtcTicks - started.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var partyReservation = Guid.NewGuid();
        var joinReservation = Guid.NewGuid();
        foreach (var member in party)
            await InsertEntryAsync(source, member.CharacterId, partyReservation, started.AddSeconds(-1), started.AddSeconds(1));
        var store = new PostgresWonderlandTitleStore(source);

        // A joiner whose claim is not recorded as admitted is not entitled.
        await InsertEntryAsync(source, joiner.CharacterId, joinReservation, started.AddMinutes(10), null);
        var unadmitted = LateJoinerRequest(WorldInstanceId.New(), partyReservation, started, started.AddMinutes(15), 2,
            party, joiner, joinReservation);
        CheckStatus(WonderlandTitleStatus.AdmissionConflict, (await store.SettleAsync(unadmitted)).Status,
            "an unadmitted late reservation is refused");

        // A joiner admitted only after the island cleared is not entitled to that island.
        await using (var admit = source.CreateCommand(
            "UPDATE public.legacy_instance_daily_entries SET admitted_at=@at WHERE reservation_id=@reservation;"))
        {
            admit.Parameters.AddWithValue("at", started.AddMinutes(10).AddSeconds(1));
            admit.Parameters.AddWithValue("reservation", joinReservation);
            await admit.ExecuteNonQueryAsync();
        }
        var early = LateJoinerRequest(WorldInstanceId.New(), partyReservation, started, started.AddMinutes(5), 2,
            party, joiner, joinReservation);
        CheckStatus(WonderlandTitleStatus.AdmissionConflict, (await store.SettleAsync(early)).Status,
            "a late reservation claimed after the island cleared is refused");

        // The party reservation keeps its original fence: claimed before the start.
        var lateParty = LateJoinerRequest(WorldInstanceId.New(), partyReservation, started.AddMinutes(-5).AddSeconds(-30),
            started.AddMinutes(15), 2, party, joiner, joinReservation);
        CheckStatus(WonderlandTitleStatus.AdmissionConflict, (await store.SettleAsync(lateParty)).Status,
            "the party's own reservation must still predate the run start");

        // A joiner who claims the party reservation's character ids under the wrong reservation is refused.
        var swapped = LateJoinerRequest(WorldInstanceId.New(), partyReservation, started, started.AddMinutes(15), 2,
            [party[0], joiner], party[1], joinReservation);
        CheckStatus(WonderlandTitleStatus.AdmissionConflict, (await store.SettleAsync(swapped)).Status,
            "each admitted character must hold its admission on its own reservation");
    }

    private static void CheckStatus(WonderlandTitleStatus expected, WonderlandTitleStatus actual, string message) =>
        Check.True(expected == actual, $"{message} (expected {expected}, got {actual})");

    private static WonderlandTitleRequest LateJoinerRequest(WorldInstanceId instance, Guid partyReservation,
        DateTimeOffset started, DateTimeOffset cleared, int island, IReadOnlyCollection<WonderlandTitleMember> party,
        WonderlandTitleMember joiner, Guid joinReservation)
    {
        WonderlandTitleMember[] admitted = [.. party, joiner];
        return new(instance, RealmId.Tempest, partyReservation, started, cleared, island, admitted, admitted,
            [partyReservation, joinReservation],
            new Dictionary<int, Guid> { [joiner.CharacterId] = joinReservation });
    }

    private static async Task<string> SettleQueueAsync(GameSessionRegistry registry, DateTimeOffset now)
    {
        var previous = Console.Error;
        var capture = new StringWriter();
        Console.SetError(capture);
        try
        {
            await registry.RetryWonderlandTitlesAsync(now, CancellationToken.None);
        }
        finally
        {
            Console.SetError(previous);
        }
        var text = capture.ToString().Trim();
        if (text.Length > 0) previous.WriteLine(text);
        return text;
    }

    private static async Task<WonderlandTitleMember[]> CreateMembersAsync(NpgsqlDataSource source, int count)
    {
        var members = new List<WonderlandTitleMember>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < count; index++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..16];
            await using var account = new NpgsqlCommand(
                "INSERT INTO public.accounts(username,password) VALUES(@name,'') RETURNING id;", connection, transaction);
            account.Parameters.AddWithValue("name", "wonder_late_" + suffix);
            var accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
            await using var character = new NpgsqlCommand("""
                INSERT INTO public.character_base(account_id,server_id,name,gender,camp,profession,
                    fighter_job_lv,fighter_job_exp,"SkillExp","SkillPoint",belief,"Map","Pos_X","Pos_Z",
                    "MaxHP","curHP","MaxMP","curMP",medusa_honor_points,medusa_reward_revision,selected_title_id,
                    "Money","Stone","BindingGold")
                VALUES(@account,1,@name,'male',0,0,120,0,0,10,1,0,136,-150,1500,1500,177,177,1234,7,0,
                    10000,10,0) RETURNING id;
                """, connection, transaction);
            character.Parameters.AddWithValue("account", accountId);
            character.Parameters.AddWithValue("name", "Late" + suffix);
            var characterId = Convert.ToInt32(await character.ExecuteScalarAsync());
            var owner = await PlayerOwnershipTestFences.InstallAsync(connection, transaction, accountId, characterId);
            members.Add(new(accountId, characterId, owner));
        }
        await transaction.CommitAsync();
        return [.. members.OrderBy(member => member.CharacterId)];
    }

    private static async Task InsertEntryAsync(NpgsqlDataSource source, int characterId, Guid reservation,
        DateTimeOffset claimed, DateTimeOffset? admitted)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO public.legacy_instance_daily_entries(realm_id,realm_day,instance_kind,
                character_id,reservation_id,claimed_at,admitted_at)
            VALUES(1,@day,2,@character,@reservation,@claimed,@admitted);
            """);
        command.Parameters.AddWithValue("day", DateOnly.FromDateTime(claimed.UtcDateTime));
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("reservation", reservation);
        command.Parameters.AddWithValue("claimed", claimed);
        command.Parameters.AddWithValue("admitted", admitted is { } at ? at : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlDataSource source, WorldInstanceId instance, int island,
        string table, string islandColumn)
    {
        await using var command = source.CreateCommand(
            $"SELECT count(*) FROM public.{table} WHERE world_instance_id=@instance AND {islandColumn}=@island;");
        command.Parameters.AddWithValue("instance", instance.Value);
        command.Parameters.AddWithValue("island", (short)island);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> MedalsAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand(
            "SELECT COALESCE(exchange_medal,0) FROM public.character_base WHERE id=@id;");
        command.Parameters.AddWithValue("id", characterId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
