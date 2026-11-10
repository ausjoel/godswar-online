using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class DonatorStatProjectionChecks
{
    public const string CheckName =
        "Donator calculated-stat authority and live refresh";

    public static async Task RunAsync()
    {
        CheckSqlAuthority();
        CheckHealthProjectionModes();
        await CheckLiveDonatorRefreshAsync();
    }

    private static void CheckSqlAuthority()
    {
        var sql = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        var compactSql = string.Concat(
            sql.Where(static character => !char.IsWhiteSpace(character)));
        Check.True(
            sql.Contains("account.donator_tier", StringComparison.Ordinal) &&
            sql.Contains(
                "account.donator_expires_at",
                StringComparison.Ordinal),
            "calculated stats use the renamed donator account columns");
        Check.True(
            sql.Contains(
                "account.donator_expires_at > now()",
                StringComparison.Ordinal) &&
            compactSql.Contains(
                "COALESCE(donator_benefit.maximum_health_bonus_basis_points,0)",
                StringComparison.Ordinal) &&
            compactSql.Contains(
                "COALESCE(donator_benefit.attack_bonus_basis_points,0)",
                StringComparison.Ordinal),
            "expired donator tiers contribute zero calculated-stat bonus");

        var expectedBenefits = new[]
        {
            "(1, 200, 100)",
            "(2, 400, 200)",
            "(3, 600, 300)",
            "(4, 800, 400)",
            "(5, 1000, 500)"
        };
        Check.True(
            expectedBenefits.All(value =>
                sql.Contains(value, StringComparison.Ordinal)),
            "all five donator HP and attack benefit tiers are in SQL");
        Check.True(
            sql.Contains(
                "donator_stats.physical_attack AS physical_attack",
                StringComparison.Ordinal) &&
            sql.Contains(
                "donator_stats.magic_attack AS magic_attack",
                StringComparison.Ordinal),
            "donator attack scaling applies to physical and magic attack");
    }

    private static void CheckHealthProjectionModes()
    {
        var character = CreateCharacter();
        var projected = CreateStats(maximumHealth: 200);

        CharacterCalculatedStatsProjectionApplier.Apply(
            character,
            projected,
            CharacterHealthProjectionMode.PreservePercentage);
        Check.True(
            character.MaxHp == 200 && character.CurrentHp == 100,
            "percentage projection preserves half health while max HP rises");

        character.CurrentHp = 75;
        CharacterCalculatedStatsProjectionApplier.Apply(
            character,
            CreateStats(maximumHealth: 100),
            CharacterHealthProjectionMode.PreserveAbsolute);
        Check.True(
            character.MaxHp == 100 && character.CurrentHp == 75,
            "ordinary stat refresh preserves absolute live health");

        character.CurrentHp = 0;
        CharacterCalculatedStatsProjectionApplier.Apply(
            character,
            projected,
            CharacterHealthProjectionMode.PreservePercentage);
        Check.Equal(
            0,
            character.CurrentHp,
            "donator projection does not revive a dead character");
    }

    private static async Task CheckLiveDonatorRefreshAsync()
    {
        var store = new ProjectionStore(CreateStats(
            maximumHealth: 110,
            maximumMana: 200,
            physicalAttack: 105,
            magicAttack: 210));
        await using var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var registry = new GameSessionRegistry(store: store);
        var character = CreateCharacter();
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));

        var octagram = new ExperienceBoostState(
        [
            new ActiveExperienceBoost(
                ExperienceStatusIds.OctagramPatron,
                ExperienceBoostKinds.Donator,
                2_500,
                (int)DonatorTier.OctagramPatron,
                null,
                "donator:octagrampatron")
        ]);
        Check.True(
            await registry.RefreshExperienceStatusesAndPublishAsync(
                session,
                octagram,
                "donator-check",
                CancellationToken.None),
            "adding a donator tier publishes its status");
        Check.True(
            store.ReadCount == 1 &&
            character.MaxHp == 110 &&
            character.CurrentHp == 55 &&
            character.MaxMp == 200 &&
            character.CurrentMp == 75 &&
            character.CalculatedStats?.PhysicalAttack == 105 &&
            character.CalculatedStats.MagicAttack == 210,
            "adding a donator tier refreshes exact stats, preserves HP " +
                "percentage, and preserves current MP");
        var ecs = registry.GetPlayerMapEcsDiagnostics(session);
        Check.True(
            ecs?.Vitals.MaximumHp == 110 &&
            ecs.Vitals.CurrentHp == 55 &&
            ecs.CalculatedStats.PhysicalAttack == 105 &&
            ecs.CalculatedStats.MagicAttack == 210,
            "donator refresh replaces the live map ECS projection");
        await WaitForWriteCountAsync(transport, expected: 2);
        var firstFrames = DecryptFrames(transport.WrittenBytes);
        Check.True(
            firstFrames.Count == 2 &&
            ReadOpcode(firstFrames[0]) == 0x27B7 &&
            ReadOpcode(firstFrames[1]) == 0x27B6 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                firstFrames[1].AsSpan(104)) == 55 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                firstFrames[1].AsSpan(108)) == 75 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                firstFrames[1].AsSpan(144)) == 110 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                firstFrames[1].AsSpan(160)) == 105 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                firstFrames[1].AsSpan(168)) == 210,
            "donator refresh forces paired 10167/10166 with current stats");

        await registry.RefreshExperienceStatusesAndPublishAsync(
            session,
            octagram,
            "same-donator-check",
            CancellationToken.None);
        Check.Equal(
            1,
            store.ReadCount,
            "unchanged donator identity does not re-read calculated stats");

        var battlePass = new ActiveExperienceBoost(
            ExperienceStatusIds.PremiumBattlePass,
            ExperienceBoostKinds.BattlePass,
            500,
            1,
            DateTimeOffset.UtcNow.AddDays(1),
            "entitlement:battle_pass");
        await registry.RefreshExperienceStatusesAndPublishAsync(
            session,
            new ExperienceBoostState(
                [octagram.ActiveBoosts[0], battlePass]),
            "battle-pass-only-check",
            CancellationToken.None);
        Check.Equal(
            1,
            store.ReadCount,
            "a Battle Pass-only status change does not refresh donor stats");

        store.Projection = CreateStats(
            maximumHealth: 102,
            maximumMana: 150,
            physicalAttack: 101,
            magicAttack: 202);
        var kijin = new ExperienceBoostState(
        [
            new ActiveExperienceBoost(
                ExperienceStatusIds.KijinPatron,
                ExperienceBoostKinds.Donator,
                500,
                (int)DonatorTier.KijinPatron,
                null,
                "donator:kijinpatron")
        ]);
        Check.True(
            await registry.RefreshExperienceStatusesAndPublishAsync(
                session,
                kijin,
                "donator-tier-change-check",
                CancellationToken.None),
            "changing a donator tier publishes its replacement status");
        Check.True(
            store.ReadCount == 2 &&
            character.MaxHp == 102 &&
            character.CurrentHp == 51 &&
            character.MaxMp == 150 &&
            character.CurrentMp == 75 &&
            character.CalculatedStats?.PhysicalAttack == 101 &&
            character.CalculatedStats.MagicAttack == 202,
            "changing a donator tier refreshes exact stats and preserves vitals");

        store.Projection = CreateStats(
            maximumHealth: 100,
            physicalAttack: 100,
            magicAttack: 200);
        Check.True(
            await registry.RefreshExperienceStatusesAndPublishAsync(
                session,
                ExperienceBoostState.Empty,
                "donator-removal-check",
                CancellationToken.None),
            "removing a donator tier publishes its status removal");
        Check.True(
            store.ReadCount == 3 &&
            character.MaxHp == 100 &&
            character.CurrentHp == 50 &&
            character.MaxMp == 100 &&
            character.CurrentMp == 75 &&
            character.CalculatedStats?.PhysicalAttack == 100 &&
            character.CalculatedStats.MagicAttack == 200,
            "removing a donator tier refreshes exact stats and preserves HP percentage");

        character.CurrentHp = 0;
        store.Projection = CreateStats(
            maximumHealth: 110,
            physicalAttack: 105,
            magicAttack: 210);
        await registry.RefreshExperienceStatusesAndPublishAsync(
            session,
            octagram,
            "dead-donator-check",
            CancellationToken.None);
        Check.True(
            store.ReadCount == 4 && character.CurrentHp == 0,
            "live donor refresh preserves death at zero HP");

        await WaitForWriteCountAsync(transport, expected: 10);
        registry.Remove(session);
    }

    private static GameCharacter CreateCharacter() => new()
    {
        Id = 901,
        AccountId = 902,
        Name = "DonatorProjection",
        CurrentMap = GameDefaults.AthensCapitalMap,
        MaxHp = 100,
        CurrentHp = 50,
        MaxMp = 100,
        CurrentMp = 75,
        CalculatedStats = CreateStats(maximumHealth: 100)
    };

    private static CharacterStats CreateStats(
        int maximumHealth,
        int maximumMana = 100,
        int physicalAttack = 100,
        int magicAttack = 200) => new()
    {
        CharacterId = 901,
        AccountId = 902,
        Name = "DonatorProjection",
        MaxHp = maximumHealth,
        CurrentHp = maximumHealth,
        MaxMp = maximumMana,
        CurrentMp = 75,
        PhysicalAttack = physicalAttack,
        MagicAttack = magicAttack
    };

    private static async Task WaitForWriteCountAsync(
        ScriptedLegacyByteTransport transport,
        int expected)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        while (transport.WriteCount < expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static IReadOnlyList<byte[]> DecryptFrames(byte[] encrypted)
    {
        var clear = (byte[])encrypted.Clone();
        new PacketCipher().Transform(clear);
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < clear.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset));
            Check.True(
                length >= 4 && offset <= clear.Length - length,
                "donator status egress contains complete packet frames");
            frames.Add(clear.AsSpan(offset, length).ToArray());
            offset += length;
        }

        return frames;
    }

    private static ushort ReadOpcode(byte[] frame) =>
        BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2));

    private sealed class ProjectionStore(CharacterStats projection) :
        GameStoreTestStub
    {
        private int _readCount;

        public CharacterStats Projection { get; set; } = projection;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            return Task.FromResult<CharacterStats?>(Projection);
        }
    }
}
