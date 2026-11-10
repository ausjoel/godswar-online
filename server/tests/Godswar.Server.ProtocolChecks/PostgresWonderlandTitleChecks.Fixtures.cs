using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    private static async Task<WonderlandTitleRequest> CreateAsync(NpgsqlDataSource source, int count = 2, int? eligibleCount = null)
    {
        var members = new List<WonderlandTitleMember>();
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < count; index++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..16];
            await using var account = new NpgsqlCommand(
                "INSERT INTO public.accounts(username,password) VALUES(@name,'') RETURNING id;", connection, transaction);
            account.Parameters.AddWithValue("name", "wonder_title_" + suffix);
            var accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
            await using var character = new NpgsqlCommand("""
                INSERT INTO public.character_base(account_id,server_id,name,gender,camp,profession,
                    fighter_job_lv,fighter_job_exp,"SkillExp","SkillPoint",belief,"Map","Pos_X","Pos_Z",
                    "MaxHP","curHP","MaxMP","curMP",medusa_honor_points,medusa_reward_revision,selected_title_id,
                    "Money","Stone","BindingGold")
                VALUES(@account,1,@name,'male',0,0,120,0,0,10,1,0,136,-150,1500,1500,177,177,1234,7,@selected,
                    10000,10,0) RETURNING id;
                """, connection, transaction);
            character.Parameters.AddWithValue("account", accountId);
            character.Parameters.AddWithValue("name", "Wonder" + suffix);
            character.Parameters.AddWithValue("selected", index % 2 == 0 ? 0 : 5009);
            var characterId = Convert.ToInt32(await character.ExecuteScalarAsync());
            var owner = await PlayerOwnershipTestFences.InstallAsync(connection, transaction, accountId, characterId);
            members.Add(new(accountId, characterId, owner));
        }
        await transaction.CommitAsync();
        var request = new WonderlandTitleRequest(WorldInstanceId.New(), RealmId.Tempest, Guid.NewGuid(),
            started, started.AddMinutes(2), 2, members, members.Take(eligibleCount ?? count).ToArray());
        await InsertAdmissionsAsync(source, request);
        return request;
    }

    private static async Task InsertAdmissionsAsync(NpgsqlDataSource source, WonderlandTitleRequest request)
    {
        foreach (var member in request.AdmittedMembers)
        {
            await using var command = source.CreateCommand("""
                INSERT INTO public.legacy_instance_daily_entries(realm_id,realm_day,instance_kind,
                    character_id,reservation_id,claimed_at,admitted_at)
                VALUES(1,@day,2,@character,@reservation,@claimed,@admitted);
                """);
            command.Parameters.AddWithValue("day", DateOnly.FromDateTime(request.StartedAtUtc.UtcDateTime));
            command.Parameters.AddWithValue("character", member.CharacterId);
            command.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
            command.Parameters.AddWithValue("claimed", request.StartedAtUtc.AddSeconds(-1));
            command.Parameters.AddWithValue("admitted", request.StartedAtUtc.AddSeconds(1));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static WonderlandTitleRequest Copy(WonderlandTitleRequest request, int? island = null,
        WorldInstanceId? instance = null, RealmId? realm = null, Guid? reservation = null,
        DateTimeOffset? started = null, DateTimeOffset? cleared = null,
        IReadOnlyCollection<WonderlandTitleMember>? admitted = null, IReadOnlyCollection<WonderlandTitleMember>? eligible = null) =>
        new(instance ?? request.WorldInstanceId, realm ?? request.RealmId, reservation ?? request.AdmissionReservationId,
            started ?? request.StartedAtUtc, cleared ?? request.ClearedAtUtc, island ?? request.IslandNumber,
            admitted ?? request.AdmittedMembers, eligible ?? request.FrozenMembers);

    private static async Task<string> StateAsync(NpgsqlDataSource source, WonderlandTitleRequest request)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object('characters',
                (SELECT jsonb_agg(jsonb_build_array(id,medusa_honor_points,medusa_reward_revision,selected_title_id) ORDER BY id)
                 FROM public.character_base WHERE id=ANY(@ids)),
                'owners',(SELECT jsonb_agg(to_jsonb(t) ORDER BY character_id,title_id)
                    FROM public.wonderland_character_title_ownership t WHERE character_id=ANY(@ids)),
                'milestones',(SELECT count(*) FROM public.wonderland_title_milestones WHERE world_instance_id=@instance),
                'members',(SELECT count(*) FROM public.wonderland_title_members WHERE world_instance_id=@instance),
                'runs',(SELECT count(*) FROM public.wonderland_title_runs WHERE world_instance_id=@instance))::text;
            """);
        command.Parameters.AddWithValue("ids", request.AdmittedCharacterIds.ToArray());
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task MutateAsync(NpgsqlDataSource source, WonderlandTitleRequest request, string sql)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("character", request.FrozenMembers[0].CharacterId);
        command.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "change exactly one isolated Wonderland title fixture row");
    }
}
