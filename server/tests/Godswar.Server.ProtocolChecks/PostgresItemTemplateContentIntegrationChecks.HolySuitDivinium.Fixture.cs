using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task<int> CreateHolySuitOwnedIdentityFixtureAsync(NpgsqlDataSource source,
        bool includeDivinium = false)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var account = new NpgsqlCommand("INSERT INTO accounts(username,password) VALUES(@name,'') RETURNING id;", connection, transaction);
        account.Parameters.AddWithValue("name", "b09hs_" + token);
        var accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        await using var character = new NpgsqlCommand("""
            INSERT INTO character_base(account_id,server_id,name,camp,profession,fighter_job_lv)
            VALUES(@account,1,@name,1,1,80) RETURNING id;
            """, connection, transaction);
        character.Parameters.AddWithValue("account", accountId);
        character.Parameters.AddWithValue("name", "HS" + token);
        var characterId = Convert.ToInt32(await character.ExecuteScalarAsync());
        await using var items = new NpgsqlCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack,item_exp,holy_suit_code)
            VALUES(@character,1,0,1007,2,3,1,1,1234,501),
                  (@character,1,1,1007,2,3,1,1,5678,601),
                  (@character,1,2,1007,2,3,1,1,9876,710),
                  (@character,1,3,9014,0,1,1,17,0,0),
                  (@character,1,4,9015,0,1,0,23,0,0),
                  (@character,1,5,9016,0,1,1,31,0,0);
            """, connection, transaction);
        items.Parameters.AddWithValue("character", characterId);
        Check.Equal(6, await items.ExecuteNonQueryAsync(), "prepare owned material stacks and 5xx/6xx/7xx equipment");
        if (includeDivinium)
        {
            await using var divinium = new NpgsqlCommand("""
                INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack,item_exp,holy_suit_code)
                VALUES(@character,1,6,9017,0,1,0,41,0,0),
                      (@character,1,7,1007,2,3,1,1,4321,810);
                """, connection, transaction);
            divinium.Parameters.AddWithValue("character", characterId);
            Check.Equal(2, await divinium.ExecuteNonQueryAsync(), "prepare an owned Divinium stack and terminal-tier equipment");
        }
        await transaction.CommitAsync();
        return characterId;
    }

    private static async Task<string> ReadHolySuitOwnedIdentityFingerprintAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_agg(to_jsonb(item) ORDER BY slot_index)::text FROM character_items item WHERE user_id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetHolySuitMutableAppearanceFromRevisionAsync(NpgsqlDataSource source, string revision,
        bool includeDivinium = false)
    {
        await using var command = source.CreateCommand("""
            UPDATE item_templates mutable SET display_name=definition.display_name,texture=definition.texture,
                icon=definition.icon,stats=definition.stats
            FROM item_template_content_definitions definition
            WHERE definition.revision=@revision AND definition.id=mutable.id
                AND mutable.id BETWEEN 9014 AND @lastId;
            """);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("lastId", includeDivinium ? 9017 : 9016);
        Check.Equal(includeDivinium ? 4 : 3, await command.ExecuteNonQueryAsync(),
            "restore the exact reviewed material appearance rows");
    }

    private static async Task<string> ReadHolySuitMutableAppearanceAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_agg(to_jsonb(item) ORDER BY id)::text FROM item_templates item WHERE id BETWEEN 9014 AND 9017;
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadHolySuitCurrentPointerAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT revision FROM item_template_content_publication WHERE family='items';");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertHolySuitMutableMatchesPublicationAsync(NpgsqlDataSource source, string revision)
    {
        await using var command = source.CreateCommand("""
            SELECT count(*)::integer FROM item_templates mutable
            JOIN item_template_content_definitions definition ON definition.revision=@revision AND definition.id=mutable.id
            WHERE mutable.id BETWEEN 9014 AND 9017 AND
                ROW(mutable.display_name,mutable.texture,mutable.icon,mutable.stats) IS NOT DISTINCT FROM
                ROW(definition.display_name,definition.texture,definition.icon,definition.stats);
            """);
        command.Parameters.AddWithValue("revision", revision);
        Check.Equal(4, Convert.ToInt32(await command.ExecuteScalarAsync()),
            "owned item foreign-key identities agree with the published renamed and Divinium wares");
    }
}
