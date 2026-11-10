using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAtlantisCompletionRewardChecks
{
    private static async Task<CompletionFixture> CreateFixtureAsync(NpgsqlDataSource source, int count,
        int? finisherCount = null)
    {
        var members = new List<AtlantisCompletionMember>();
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var reservation = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < count; index++)
        {
            var token = Guid.NewGuid().ToString("N")[..16];
            int accountId;
            await using (var account = new NpgsqlCommand(
                "INSERT INTO public.accounts(username,password) VALUES(@name,'') RETURNING id;",
                connection, transaction))
            {
                account.Parameters.AddWithValue("name", $"atl_reward_{token}");
                accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
            }
            int characterId;
            await using (var character = new NpgsqlCommand(
                """
                INSERT INTO public.character_base(account_id,server_id,name,gender,camp,profession,
                    fighter_job_lv,fighter_job_exp,"SkillExp","SkillPoint",belief,"Map","Pos_X","Pos_Z",
                    "MaxHP","curHP","MaxMP","curMP",medusa_honor_points,medusa_reward_revision)
                VALUES(@account,1,@name,'male',@camp,0,160,0,0,10,1,0,136,-150,1500,1500,177,177,@honor,7)
                RETURNING id;
                """, connection, transaction))
            {
                character.Parameters.AddWithValue("account", accountId);
                character.Parameters.AddWithValue("name", $"AtlReward{token}");
                character.Parameters.AddWithValue("camp", (short)(index % 2));
                character.Parameters.AddWithValue("honor", 100 + index);
                characterId = Convert.ToInt32(await character.ExecuteScalarAsync());
            }
            members.Add(new(accountId, characterId));
            await using var admission = new NpgsqlCommand(
                """
                INSERT INTO public.legacy_instance_daily_entries(realm_id,realm_day,instance_kind,
                    character_id,reservation_id,claimed_at,admitted_at)
                VALUES(1,@day,1,@character,@reservation,@claimed,@admitted);
                """, connection, transaction);
            admission.Parameters.AddWithValue("day", DateOnly.FromDateTime(started.UtcDateTime));
            admission.Parameters.AddWithValue("character", characterId);
            admission.Parameters.AddWithValue("reservation", reservation);
            admission.Parameters.AddWithValue("claimed", started.AddSeconds(-1));
            admission.Parameters.AddWithValue("admitted", started.AddSeconds(1));
            Check.Equal(1, await admission.ExecuteNonQueryAsync(), "fixture persists each successful Atlantis admission");
        }
        await transaction.CommitAsync();
        var request = new AtlantisCompletionRewardRequest(WorldInstanceId.New(), RealmId.Tempest,
            reservation, started, started.AddMinutes(10), 850, members,
            members.Take(finisherCount ?? count).ToArray());
        return new(request);
    }

    private static AtlantisCompletionRewardRequest CopyRequest(AtlantisCompletionRewardRequest request,
        WorldInstanceId? instance = null, RealmId? realm = null, Guid? reservation = null,
        DateTimeOffset? completed = null, IReadOnlyCollection<AtlantisCompletionMember>? admitted = null,
        IReadOnlyCollection<AtlantisCompletionMember>? finishers = null) =>
        new(instance ?? request.WorldInstanceId, realm ?? request.RealmId,
            reservation ?? request.AdmissionReservationId, request.StartedAtUtc,
            completed ?? request.CompletedAtUtc, 850,
            admitted ?? request.AdmittedMembers.ToArray(), finishers ?? request.FrozenMembers.ToArray());

    private static async Task<CharacterRewardState[]> ReadCharactersAsync(NpgsqlDataSource source,
        CompletionFixture fixture)
    {
        await using var command = source.CreateCommand(
            """
            SELECT id,account_id,camp,medusa_honor_points,medusa_reward_revision,selected_title_id
            FROM public.character_base WHERE id=ANY(@ids) ORDER BY id;
            """);
        command.Parameters.AddWithValue("ids", fixture.Request.AdmittedCharacterIds.ToArray());
        var rows = new List<CharacterRewardState>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt16(2), reader.GetInt32(3),
                reader.GetInt64(4), reader.GetInt32(5)));
        }
        return rows.ToArray();
    }

    private static async Task<RewardRowCounts> ReadRewardCountsAsync(NpgsqlDataSource source,
        CompletionFixture fixture)
    {
        await using var command = source.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM public.atlantis_completion_rewards WHERE admission_reservation_id=@reservation),
                (SELECT count(*) FROM public.atlantis_completion_reward_members WHERE character_id=ANY(@ids)),
                (SELECT count(*) FROM public.atlantis_character_title_ownership WHERE character_id=ANY(@ids));
            """);
        command.Parameters.AddWithValue("reservation", fixture.Request.AdmissionReservationId);
        command.Parameters.AddWithValue("ids", fixture.Request.AdmittedCharacterIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "completion evidence counts are readable");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task AssertNoRewardAsync(NpgsqlDataSource source, CompletionFixture fixture,
        CharacterRewardState[] expected, string reason)
    {
        Check.True((await ReadCharactersAsync(source, fixture)).SequenceEqual(expected),
            $"{reason}: every member's balance, revision, and selected title stay unchanged");
        Check.True(await ReadRewardCountsAsync(source, fixture) == new RewardRowCounts(0, 0, 0),
            $"{reason}: no header, member receipt, or title ownership is committed");
    }

    private static async Task UpdateFixtureAsync(NpgsqlDataSource source, CompletionFixture fixture,
        string sql, int memberIndex = 0)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("character", fixture.Request.AdmittedMembers[memberIndex].CharacterId);
        command.Parameters.AddWithValue("reservation", fixture.Request.AdmissionReservationId);
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "mutate exactly one disposable completion fixture row");
    }

    private sealed record CompletionFixture(AtlantisCompletionRewardRequest Request);
    private readonly record struct CharacterRewardState(int CharacterId, int AccountId, short Camp,
        int Honor, long Revision, int SelectedTitle);
    private readonly record struct RewardRowCounts(long Headers, long Members, long Titles);
}
