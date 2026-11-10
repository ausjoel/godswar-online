using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Godswar.Server.State;
using Godswar.Server.Packets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterNameChecks
{
    public const string ProtocolName = "Character name rejection uses native registered-name prompt";
    public const string DatabaseName = "PostgreSQL active names reject collisions without numbering and release deleted names";
    public static Task RunProtocolAsync()
    {
        var packet = PacketBuilder.CreateRoleNameUnavailable();
        Check.Equal(12, packet.Length, "native creation result size retained");
        Check.Equal((ushort)10164, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)), "native login-operation result opcode");
        Check.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)), "verified client result5 maps to NameRegistered prompt");
        Check.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(PacketBuilder.CreateRoleSuccess().AsSpan(8)), "success response unchanged");
        return Task.CompletedTask;
    }
    public static async Task RunDatabaseAsync()
    {
        var connection = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connection)) throw new CheckSkippedException("Disposable PostgreSQL connection not configured");
        await using var source = NpgsqlDataSource.Create(connection);
        await using (var guard = source.CreateCommand("SELECT current_database()"))
            Check.True(Regex.IsMatch((string)(await guard.ExecuteScalarAsync())!, "^godswar_b12_[a-z0-9_]+$"), "name checks refuse every non-B12 database");
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var store = new PostgresGameStore(connection);
        await store.EnsureSeedDataAsync();
        var token = Guid.NewGuid().ToString("N")[..12];
        var accounts = new List<GameAccount>();
        for (var i = 0; i < 6; i++) accounts.Add(await store.LoginOrCreateAccountAsync($"b12names{token}{i}", string.Empty));
        var name = "Name" + Guid.NewGuid().ToString("N")[..28];
        GameCharacter Intent(string value) => new() { Name = value, Profession = 0, Camp = GameDefaults.SpartaCamp };
        var first = await store.CreateCharacterAsync(accounts[0].Id, Intent(name));
        var duplicate = Intent(name.ToUpperInvariant());
        try { await store.CreateCharacterAsync(accounts[1].Id, duplicate); throw new InvalidOperationException("Duplicate accepted"); }
        catch (CharacterNameUnavailableException) { }
        Check.Equal(name.ToUpperInvariant(), duplicate.Name, "32-character collision never gets truncated suffix or random replacement");
        await using (var count = source.CreateCommand("SELECT count(*) FROM character_base WHERE account_id=@account"))
        {
            count.Parameters.AddWithValue("account", accounts[1].Id);
            Check.Equal(0L, (long)(await count.ExecuteScalarAsync())!, "failed collision creates no character");
        }
        Check.True(await store.DeleteCharacterAsync(accounts[0].Id, name), "fixture character retires through normal lifecycle");
        await using (var retire = source.CreateCommand("UPDATE accounts SET status=1 WHERE id=@account"))
        { retire.Parameters.AddWithValue("account", accounts[0].Id); await retire.ExecuteNonQueryAsync(); }
        var reused = await store.CreateCharacterAsync(accounts[1].Id, Intent(name));
        Check.Equal(name, reused.Name, "retired account/deleted character does not reserve the original name");
        try
        {
            await using var restore = source.CreateCommand("UPDATE character_base SET lifecycle_state='active',deleted_at=NULL,restore_until=NULL,purge_after=NULL WHERE id=@id");
            restore.Parameters.AddWithValue("id", first.Id);await restore.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Restore stole reused name");
        }
        catch (PostgresException error) when (error.SqlState==PostgresErrorCodes.UniqueViolation) { }
        var bannedName = "Ban" + token;
        await store.CreateCharacterAsync(accounts[2].Id, Intent(bannedName));
        await using (var ban = source.CreateCommand("UPDATE accounts SET status=1 WHERE id=@account"))
        { ban.Parameters.AddWithValue("account", accounts[2].Id);await ban.ExecuteNonQueryAsync(); }
        try { await store.CreateCharacterAsync(accounts[3].Id, Intent(bannedName));throw new InvalidOperationException("Banned active name stolen"); }
        catch (CharacterNameUnavailableException) { }
        var raceName="Race"+token;
        async Task<bool> Race(int account)
        {
            try {var created=await store.CreateCharacterAsync(account,Intent(raceName));Check.Equal(raceName,created.Name,"race winner retains requested name");return true;}
            catch(CharacterNameUnavailableException){return false;}
        }
        var race=await Task.WhenAll(Race(accounts[4].Id),Race(accounts[5].Id));
        Check.Equal(1,race.Count(v=>v),"simultaneous requests produce one exact name and one rejection, never a numbered second character");
    }
}
