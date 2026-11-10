using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAtlantisCompletionRewardChecks
{
    private static async Task CheckAdministrativeTitleEntitlementsAsync(NpgsqlDataSource source, PostgresGameStore store)
    {
        await using (var catalog = source.CreateCommand("SELECT count(*) FROM public.editor_title_catalog;"))
            Check.Equal(90L, (long)(await catalog.ExecuteScalarAsync())!, "all reviewed native title definitions seeded");
        var fixture = await CreateFixtureAsync(source, 1);
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request)).Succeeded,
            "grant test starts from a genuinely earned Atlantis title");
        await GrantSelectionMedusaTitleAsync(source, fixture);
        var request = await CreateTitleSelectionRequestAsync(source, fixture, 1000);
        var counts = await ReadRewardCountsAsync(source, fixture);
        await using (var grant = source.CreateCommand("""
            INSERT INTO public.character_title_grants(character_id,title_id,granted_by,note)
            VALUES(@character,1000,'protocol-check','native title grant'),
                  (@character,5011,'protocol-check','also earned naturally');
            """))
        { grant.Parameters.AddWithValue("character", request.Subject.CharacterId); await grant.ExecuteNonQueryAsync(); }
        var selected = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request);
        Check.True(selected.Status == CharacterTitleSelectionStatus.Applied && selected.SelectedTitleId == 1000 &&
            selected.OwnedTitleIds.SequenceEqual(new uint[] { 1000, 5011, 5014 }),
            "admin entitlement authorizes native selection and deduplicates the also-earned title");
        await AssertSelectedTitleSnapshotAsync(source, store, fixture, selected);
        var wire = PacketBuilder.MedusaDesignationInfo(selected.SelectedTitleId, selected.OwnedTitleIds);
        Check.Equal(48, wire.Length, "three deduplicated titles fit the native list framing");
        Check.Equal(1000U, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(4)), "native list retains selected admin title");
        Check.Equal(counts, await ReadRewardCountsAsync(source, fixture), "admin grant/selection forges no completion or reward evidence");
        var invalid = false;
        try
        {
            await using var grant = source.CreateCommand("INSERT INTO public.character_title_grants(character_id,title_id,granted_by) VALUES(@character,9876543,'protocol-check');");
            grant.Parameters.AddWithValue("character", request.Subject.CharacterId); await grant.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { invalid = true; }
        Check.True(invalid, "unknown native title identity cannot be granted");
        await using (var revoke = source.CreateCommand("DELETE FROM public.character_title_grants WHERE character_id=@character AND title_id=5011;"))
        { revoke.Parameters.AddWithValue("character", request.Subject.CharacterId); await revoke.ExecuteNonQueryAsync(); }
        var earned = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request with { TitleId = 5011 });
        Check.True(earned.Succeeded && earned.OwnedTitleIds.SequenceEqual(new uint[] { 1000, 5011, 5014 }),
            "revoking admin grant cannot remove independently earned Medusa ownership");
        await using (var revoke = source.CreateCommand("DELETE FROM public.character_title_grants WHERE character_id=@character AND title_id=1000;"))
        { revoke.Parameters.AddWithValue("character", request.Subject.CharacterId); await revoke.ExecuteNonQueryAsync(); }
        var refused = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request);
        Check.True(refused.Status == CharacterTitleSelectionStatus.TitleNotOwned,
            "revoked grant-only title can no longer be equipped");
        var current = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request with { TitleId = 5011 });
        Check.True(current.Succeeded && current.OwnedTitleIds.SequenceEqual(new uint[] { 5011, 5014 }),
            "earned ownership and selection survive administrative grant removal");
        await AssertSelectedTitleSnapshotAsync(source, store, fixture, current);
        Check.Equal(counts, await ReadRewardCountsAsync(source, fixture), "grant revocation leaves all earned reward evidence intact");
        uint[] nativeIds;
        await using (var definitions = source.CreateCommand("SELECT array_agg(title_id ORDER BY title_id) FROM public.editor_title_catalog;"))
            nativeIds = ((int[])(await definitions.ExecuteScalarAsync())!).Select(id => checked((uint)id)).ToArray();
        await using (var all = source.CreateCommand("""
            INSERT INTO public.character_title_grants(character_id,title_id,granted_by)
            SELECT @character,title_id,'protocol-check' FROM public.editor_title_catalog;
            """))
        { all.Parameters.AddWithValue("character", request.Subject.CharacterId); await all.ExecuteNonQueryAsync(); }
        var expectedAll = nativeIds.Concat(new uint[] {5011,5014}).Distinct().Order().ToArray();
        var full = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request with {TitleId=6000});
        Check.True(full.Succeeded && full.OwnedTitleIds.SequenceEqual(expectedAll),
            "all ninety native catalogue titles coexist with earned ownership beyond the old64 bound");
        await AssertSelectedTitleSnapshotAsync(source, store, fixture, full);
        var fullPacket = PacketBuilder.MedusaDesignationInfo(full.SelectedTitleId, full.OwnedTitleIds);
        Check.Equal(12+12*expectedAll.Length, fullPacket.Length, "complete native title list retains exact bounded framing");
        Check.Equal(expectedAll.Length, BinaryPrimitives.ReadInt32LittleEndian(fullPacket.AsSpan(8)), "native count includes every known entitlement");
        await using (var revoke = source.CreateCommand("DELETE FROM public.character_title_grants WHERE character_id=@character AND title_id=6000;"))
        { revoke.Parameters.AddWithValue("character", request.Subject.CharacterId); await revoke.ExecuteNonQueryAsync(); }
        var cleared = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request with {TitleId=0});
        Check.True(cleared.Status == CharacterTitleSelectionStatus.Unchanged && cleared.SelectedTitleId == 0 &&
            cleared.RewardRevision == full.RewardRevision+1 && !cleared.OwnedTitleIds.Contains(6000U),
            "revoking the selected grant-only title clears display and advances revision once atomically");
        await AssertSelectedTitleSnapshotAsync(source, store, fixture, cleared);
        Check.Equal(counts, await ReadRewardCountsAsync(source, fixture), "bulk admin grants and selected-title revoke never fabricate earned reward history");
    }
}
