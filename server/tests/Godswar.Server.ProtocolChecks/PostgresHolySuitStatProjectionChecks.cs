using System.Text.RegularExpressions;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresHolySuitStatProjectionChecks
{
    public const string CheckName = "PostgreSQL Holy Suit per-item base stats and Divinium percentages";
    private const string ConnectionVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly string[] Boosted = ["max_hp", "max_mp", "physical_attack", "physical_defense",
        "magic_attack", "magic_defense", "hit", "dodge", "status_hit", "status_resistance",
        "damage_absorb", "critical", "critical_resistance", "physical_flat_absorption", "magic_flat_absorption"];
    private static readonly string[] Unchanged = ["physical_damage_bonus", "magic_damage_bonus", "hp_recovery",
        "mp_recovery", "cure_bonus", "be_cure_bonus", "ignore_physical_defense", "ignore_magic_defense"];

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException($"{CheckName} ({ConnectionVariable} is not set)");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (!Regex.IsMatch(settings.Database ?? "", @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b09_[a-z0-9_]{1,40})$",
                RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Holy Suit stat checks require a disposable b03/b09 database.");
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var published = await PostgresItemTemplateContentBootstrapper.LoadAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var balance = await PostgresHolySpiritBalanceSnapshotReader.LoadAsync(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await CheckCurveAsync(connection, transaction);
            var token = Guid.NewGuid().ToString("N");
            var revision = (token + token).ToUpperInvariant();
            await SeedContentAsync(connection, transaction, published.Revision.Sha256, revision);
            var (accountId, characterId) = await SeedCharacterAsync(connection, transaction, token[..12]);
            async Task<Dictionary<string, int>> Read() => await ReadAsync(connection, transaction,
                accountId, characterId, revision, balance);

            var baseline = await Read();
            Check.Equal(235, baseline["physical_attack"], "base attack202 plus separate Class Suit33");
            Check.Equal(213, baseline["physical_defense"], "base defense202 plus separate appended11");
            Check.Equal(1202, baseline["max_hp"], "character baseHP1000 remains separate from equipment202");
            foreach (var (code, bonus) in new[] { (710, 70), (801, 71), (810, 90), (811, 0), (900, 0), (-1, 0) })
            {
                await SetCodesAsync(connection, transaction, characterId, code);
                var actual = await Read();
                AssertDifference(baseline, actual, 2 * (101 * bonus / 100), $"code{code}");
            }
            // Two101 base values at71% must add142, rather than rounding their
            // combined143.42 bonus or multiplying the appended/class bonuses.
            await SetCodesAsync(connection, transaction, characterId, 801);
            var twoItems = await Read();
            Check.Equal(377, twoItems["physical_attack"], "per-item truncation precedes aggregation and Class Suit addition");

            await ExecuteAsync(connection, transaction, """
                UPDATE character_items SET holy_suit_code=0 WHERE user_id=@characterId;
                INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,holy_suit_code)
                VALUES(@characterId,1,9,1035,2,1,810);
                """, characterId);
            AssertDifference(baseline, await Read(), 0, "Divinium in the bag");
            await ExecuteAsync(connection, transaction, """
                UPDATE character_items SET item_location=1,slot_index=10
                WHERE user_id=@characterId AND item_location=0 AND slot_index=10;
                """, characterId);
            var oneBase = await Read();
            await SetCodesAsync(connection, transaction, characterId, 810);
            AssertDifference(oneBase, await Read(), 90, "one equipped Divinium item and two bag items");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static void AssertDifference(Dictionary<string, int> before, Dictionary<string, int> after,
        int difference, string label)
    {
        foreach (var name in Boosted)
            Check.Equal(before[name] + difference, after[name], $"{label}: exact base-only {name}");
        foreach (var name in Unchanged)
            Check.Equal(before[name], after[name], $"{label}: excluded {name} stays unchanged");
    }

    private static async Task CheckCurveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var codes = new[] { 0, -1, 1, 100, 711, 800, 811, 901, int.MinValue, int.MaxValue }
            .Concat(Enumerable.Range(1, 8).SelectMany(t => Enumerable.Range(1, 10).Select(l => t * 100 + l))).ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT code, public.holy_suit_progression_points(code) FROM unnest(@codes) code;
            """, connection, transaction);
        command.Parameters.AddWithValue("codes", codes);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var code = reader.GetInt32(0);
            Check.Equal(HolySuitProgressionPolicy.GetBonusPercent(code), reader.GetInt32(1),
                $"SQL percentage matches C# for code{code}");
            Check.Equal(HolySuitProgressionPolicy.GetEffectPoints(code), reader.GetInt32(1),
                $"SQL effect points match C# for code{code}");
            count++;
        }
        Check.Equal(codes.Length, count, "every valid and invalid curve fixture was evaluated");
    }

    private static async Task SeedContentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string originalRevision, string revision)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_revisions(revision,entry_count,source,manifest_version,
                attribute_count,equipment_rank_count,holy_suit_effect_count)
            SELECT @revision,2,'holy-suit-stat-test',2,attribute_count,equipment_rank_count,holy_suit_effect_count
            FROM item_template_content_revisions WHERE revision=@original;
            INSERT INTO item_template_content_definitions(revision,id,kind,name_key,display_name,
                equipment_slot,class_ids,min_level,max_level,hand,skill_flag,texture,icon,stats)
            SELECT @revision,id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                hand,skill_flag,texture,icon,
                jsonb_build_object('MaxHP','17,101','MaxMP','17,101','Attack','17,101','Defence','17,101',
                    'MagicAk','17,101','MagicRec','17,101','Hit','17,101','Miss','17,101',
                    'State','17,101','StateImmunity','17,101','InjureImbibe','17,101',
                    'FuryAddAk','17,101','FuryAddRec','17,101','PhysicalDamage','0.07','MagicDamage','0.09',
                    'PhysicalDamageAbsorb','11','MagicDamageAbsorb','13','HPRestore','19','MPRestore','23',
                    'Cure','0.13','AcceptCure','0.17','IgnorePhyPer','29','IgnoreMagPer','31')
            FROM item_template_content_definitions WHERE revision=@original AND id IN(1035,2007);
            INSERT INTO item_attribute_content_definitions
            SELECT @revision,id,name_key,stat_type,distribution,percent,max_level,level_values,stats
            FROM item_attribute_content_definitions WHERE revision=@original;
            INSERT INTO equipment_rank_content_definitions
            SELECT @revision,rank_kind,rank_level,required_score,aura_effect,source
            FROM equipment_rank_content_definitions WHERE revision=@original;
            INSERT INTO holy_suit_effect_content_definitions
            SELECT @revision,effect_key,stat_type,unlock_points,effect_value,source
            FROM holy_suit_effect_content_definitions WHERE revision=@original;
            UPDATE item_template_content_publication SET revision=@revision,published_at=now() WHERE family='items';
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("original", originalRevision);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int AccountId, int CharacterId)> SeedCharacterAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string token)
    {
        await using var account = new NpgsqlCommand("INSERT INTO accounts(username,password) VALUES(@name,'') RETURNING id;",
            connection, transaction);
        account.Parameters.AddWithValue("name", "b09hsstat_" + token);
        var accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        await using var character = new NpgsqlCommand("""
            INSERT INTO character_base(account_id,server_id,name,profession,fighter_job_lv,"MaxHP","MaxMP",holy_suit_points)
            VALUES(@accountId,1,@name,0,160,1000,500,0) RETURNING id;
            """, connection, transaction);
        character.Parameters.AddWithValue("accountId", accountId);
        character.Parameters.AddWithValue("name", "HSStat" + token);
        var characterId = Convert.ToInt32(await character.ExecuteScalarAsync());
        await ExecuteAsync(connection, transaction, """
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,
                attribute1,class_attribute1,holy_suit_code)
            VALUES(@characterId,0,10,1035,2,1,NULL,200,0),(@characterId,0,11,2007,2,1,10,NULL,0);
            """, characterId);
        return (accountId, characterId);
    }

    private static Task SetCodesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int characterId, int code) => ExecuteAsync(connection, transaction,
            "UPDATE character_items SET holy_suit_code=@code WHERE user_id=@characterId AND item_location=0;",
            characterId, code);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, int characterId, int? code = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        if (code.HasValue) command.Parameters.AddWithValue("code", code.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, int>> ReadAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int accountId, int characterId, string revision, HolySpiritBalanceSnapshot balance)
    {
        await using var command = new NpgsqlCommand(PostgresCharacterRuntimeItemProjectionSql.CalculatedStatsForCharacter,
            connection, transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemContentRevision", revision);
        command.Parameters.AddWithValue(PostgresGameplayContentBinding.ParameterName, NpgsqlDbType.Varchar, DBNull.Value);
        command.Parameters.AddWithValue(PostgresPetLearnedSkillContentBinding.ParameterName, NpgsqlDbType.Varchar, DBNull.Value);
        PostgresHolySpiritBalanceBinding.AddParameters(command, balance);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "actual full character-stat SQL returns the fixture character");
        return Boosted.Concat(Unchanged).ToDictionary(name => name, name => reader.GetInt32(reader.GetOrdinal(name)));
    }
}
