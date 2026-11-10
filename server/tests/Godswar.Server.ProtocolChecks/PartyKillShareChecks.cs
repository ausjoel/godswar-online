using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// PARTY (r02): party kill-quest credit and equal EXP split.
internal static class PartyKillShareChecks
{
    public const string CheckName = "Party kill sharing (r02)";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const byte SharedMapId = GameDefaults.AthensCapitalMap;

    public static async Task RunAsync()
    {
        try
        {
            CheckSplitAndEligibility();
            CheckShareIdentity();
            CheckRange();
            CheckRateDefaults();
            await CheckCandidatesAsync();
            await CheckDeclinedInviteNoticeAsync();
            await CheckKillerShareAsync();
            await CheckMemberShareAsync();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
    }

    private static void CheckSplitAndEligibility()
    {
        Check.Equal(80, MonsterRewardCatalog.SplitEqually(80, 1), "solo keeps the whole reward");
        Check.Equal(40, MonsterRewardCatalog.SplitEqually(80, 2), "two members split 80 EXP into 40 each");
        Check.Equal(26, MonsterRewardCatalog.SplitEqually(80, 3), "three members round each share down");
        Check.Equal(2, MonsterRewardCatalog.SplitEqually(10, 5), "Talent EXP 10 over five members is 2 each");
        Check.Equal(1, MonsterRewardCatalog.SplitEqually(3, 5), "a positive amount never rounds to nothing");
        Check.Equal(0, MonsterRewardCatalog.SplitEqually(0, 3), "nothing splits to nothing");

        var monster = Monster(tier: 30, x: 100, z: 100);
        var policy = MonsterRewardPolicySnapshot.Default;
        Check.True(MonsterRewardCatalog.EarnsExperience(monster, 40, policy), "level 40 earns from a tier-30 monster");
        Check.True(!MonsterRewardCatalog.EarnsExperience(monster, 51, policy), "the 20-level gap rule still applies to a share");
        Check.True(!MonsterRewardCatalog.EarnsExperience(Monster(tier: 199, x: 0, z: 0), PlayerExperienceCatalog.MaximumLevel, policy),
            "a level-200 member earns no EXP");
        Check.Equal(
            MonsterRewardCatalog.Resolve(monster, 30, policy),
            MonsterRewardCatalog.ResolveStockReward(monster),
            "the split base is the stock tier reward");
    }

    private static void CheckShareIdentity()
    {
        var runtime = Guid.NewGuid();
        var a = MonsterDeathRewardCommandEnvelope.DerivePartyShareRuntimeInstanceId(runtime, 7);
        var again = MonsterDeathRewardCommandEnvelope.DerivePartyShareRuntimeInstanceId(runtime, 7);
        var b = MonsterDeathRewardCommandEnvelope.DerivePartyShareRuntimeInstanceId(runtime, 8);
        Check.True(a == again, "a member's share identity is deterministic (a replay finds its settlement)");
        Check.True(a != b && a != runtime && a != Guid.Empty, "each member settles under its own identity, never the killer's");
        Check.True(
            MonsterDeathRewardCommandEnvelope.TryCreateCommand(runtime, SharedMapId, 5, 1, 1, 40, 5, out var killer) &&
            MonsterDeathRewardCommandEnvelope.TryCreateCommand(a, SharedMapId, 5, 1, 1, 40, 5, out var share) &&
            killer.DeathEventId != share.DeathEventId,
            "the share command is valid and has its own death event id");
    }

    private static void CheckRange()
    {
        Check.True(WorldSectorVisibilityTracker<object>.TryGetCell(100, 100, out var kill), "kill cell");
        Check.True(GameSessionRegistry.IsWithinPartyKillShareRange(kill, 100, 100, 2), "same spot is near");
        Check.True(GameSessionRegistry.IsWithinPartyKillShareRange(kill, 100 + 64, 100 - 64, 2), "two cells away is near");
        Check.True(!GameSessionRegistry.IsWithinPartyKillShareRange(kill, 100 + 96, 100, 2), "three cells away is not near");
        Check.True(!GameSessionRegistry.IsWithinPartyKillShareRange(kill, 100 + 32, 100, 0), "radius 0 is the kill cell only");
    }

    private static void CheckRateDefaults()
    {
        ServerRates.ResetToDefaults();
        Check.Equal(0L, ServerRates.Current.Absolute(ServerRateKeys.PartyKillShare), "RELEASE: party sharing is off by default");
        Check.Equal(2L, ServerRates.Current.Absolute(ServerRateKeys.PartyKillShareCells), "default range is two cells");
    }

    private static async Task CheckCandidatesAsync()
    {
        await using var killerSocket = await RuntimePolicySessionSocket.CreateAsync();
        await using var nearSocket = await RuntimePolicySessionSocket.CreateAsync();
        await using var farSocket = await RuntimePolicySessionSocket.CreateAsync();
        await using var deadSocket = await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry();
        var now = DateTimeOffset.UtcNow;
        var killer = Character(301, "ShareKiller", 100, 100);
        var near = Character(302, "ShareNear", 140, 60);
        var far = Character(303, "ShareFar", 300, 100);
        var dead = Character(304, "ShareDead", 100, 100);
        dead.CurrentHp = 0;
        registry.JoinMap(killerSocket.Session, killer.AccountId, killer, 900_301);
        registry.JoinMap(nearSocket.Session, near.AccountId, near, 900_302);
        registry.JoinMap(farSocket.Session, far.AccountId, far, 900_303);
        registry.JoinMap(deadSocket.Session, dead.AccountId, dead, 900_304);
        foreach (var session in new[] { killerSocket.Session, nearSocket.Session, farSocket.Session, deadSocket.Session })
            registry.RegisterPartyKillShareReceiver(session, static _ => true);

        var damage = Damage(Monster(tier: 30, x: 100, z: 100));
        Check.Equal(0, registry.GetPartyKillShareCandidates(killerSocket.Session, damage, 2).Count,
            "no party means a solo kill");

        foreach (var (session, member) in new[] { (nearSocket.Session, near), (farSocket.Session, far), (deadSocket.Session, dead) })
        {
            Check.Equal((int)PartyOperationStatus.Applied,
                (int)registry.InvitePartyMember(killerSocket.Session, killer.Name, member.Name, now).Status, "invite " + member.Name);
            Check.Equal((int)PartyOperationStatus.Applied,
                (int)registry.AcceptPartyInvite(session, killer.Name, member.Name, now).Status, "accept " + member.Name);
        }

        var candidates = registry.GetPartyKillShareCandidates(killerSocket.Session, damage, 2);
        Check.Equal(1, candidates.Count, "only the alive member near the kill shares it");
        Check.Equal(302, candidates[0].CharacterId, "the near member is the one sharing");
        Check.Equal(2, registry.GetPartyKillShareCandidates(killerSocket.Session, damage, 8).Count,
            "a wider range adds the far member, never the dead one or the killer");

        // A member who has moved to another map no longer shares.
        near.CurrentMap = (byte)(SharedMapId + 1);
        Check.Equal(0, registry.GetPartyKillShareCandidates(killerSocket.Session, damage, 2).Count,
            "a member on another map does not share");
        near.CurrentMap = SharedMapId;
    }

    private static async Task CheckDeclinedInviteNoticeAsync()
    {
        await using var inviterSocket = await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket = await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry();
        var now = DateTimeOffset.UtcNow;
        var inviter = Character(311, "Inviter", 10, 10);
        var target = Character(312, "Decliner", 12, 12);
        registry.JoinMap(inviterSocket.Session, inviter.AccountId, inviter, 900_311);
        registry.JoinMap(targetSocket.Session, target.AccountId, target, 900_312);
        registry.InvitePartyMember(inviterSocket.Session, inviter.Name, target.Name, now);
        var result = registry.RejectPartyInvite(targetSocket.Session, inviter.Name, target.Name, now);
        Check.Equal((int)PartyOperationStatus.Applied, (int)result.Status, "the refusal applies");
        Check.Equal(1, result.Deliveries.Count, "the inviter gets one notice");
        Check.True(ReferenceEquals(result.Deliveries[0].Recipient, inviterSocket.Session), "the notice goes to the inviter");
        var expected = PacketBuilder.PersonalGameLog("Decliner declined your party invitation.");
        Check.True(result.Deliveries[0].Packet.AsSpan().SequenceEqual(expected),
            "the notice is the stock personal game-log line, not a dialog");
        Check.Equal("Your party invitation was declined.", GameSessionRegistry.PartyInviteDeclinedText("Ωmega"),
            "a non-ASCII name falls back to a generic line");
        Check.Equal((int)PartyOperationStatus.InvitationMissing,
            (int)registry.RejectPartyInvite(targetSocket.Session, inviter.Name, target.Name, now).Status,
            "a second refusal of the same invite sends nothing");
    }

    private static async Task CheckKillerShareAsync()
    {
        var killer = Character(321, "SplitKiller", 100, 100);
        killer.Level = 35;
        var fixture = CreateFixture(killer);
        try
        {
            var damage = Damage(Monster(tier: 30, x: 100, z: 100));
            var stock = MonsterRewardCatalog.ResolveStockReward(damage.Monster);
            await InvokeResultAsync(fixture.Handler, "PrepareMonsterKillRewardAsync", damage, 1);
            Check.Equal(stock.Experience, fixture.Store.LastExperience, "solo kill pays the stock reward unchanged");
            await InvokeResultAsync(fixture.Handler, "PrepareMonsterKillRewardAsync", Damage(damage.Monster, revision: 2), 2);
            Check.Equal(stock.Experience / 2, fixture.Store.LastExperience, "the killer keeps one of two equal shares");
            Check.Equal(stock.TalentExperience / 2, fixture.Store.LastTalent, "Talent EXP is split the same way");

            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(ServerRateKeys.MonsterExperience, 3m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            await InvokeResultAsync(fixture.Handler, "PrepareMonsterKillRewardAsync", Damage(damage.Monster, revision: 3), 2);
            Check.Equal(stock.Experience / 2 * 3, fixture.Store.LastExperience, "exp.monster applies once to the killer's share");
        }
        finally
        {
            ServerRates.ResetToDefaults();
            await fixture.Session.DisposeAsync();
        }
    }

    private static async Task CheckMemberShareAsync()
    {
        var objective = StarterQuestObjectives.For(520)[0];
        var member = Character(331, "ShareMember", objective.X, objective.Z);
        member.CurrentMap = (byte)objective.MapId;
        member.Level = 23;
        member.Camp = GameDefaults.SpartaCamp;
        member.Quests.Add(new CharacterQuest { QuestId = 520 });
        var fixture = CreateFixture(member);
        try
        {
            var spawn = new CapturedMonsterSpawn((short)objective.MapId, "party-check", "party-monster",
                objective.Target, 46100, objective.X, objective.Z, new byte[108]);
            var damage = Damage(new MonsterRuntimeSnapshot(spawn, objective.X, objective.Z, objective.X, 0, objective.Z, 0,
                0, 100, false, true, false, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow, null, null,
                MonsterCombatPhase.None, null, 1, 1, Guid.NewGuid()));
            var fact = new PartyKillShareFact(damage, Guid.NewGuid(), 40, 5, 2, "Killer");
            await InvokeAsync(fixture.Handler, "ApplyPartyKillShareAsync", fact, CancellationToken.None);
            Check.Equal(1, StarterQuestObjectives.Counter(member.Quests[0].Progress, 0), "the member's kill quest counts the shared kill");
            Check.Equal(40, fixture.Store.LastExperience, "the member is paid its share");
            Check.Equal(5, fixture.Store.LastTalent, "the member is paid its Talent share");
            Check.Equal(1, fixture.Store.Payouts, "one payout");

            await InvokeAsync(fixture.Handler, "ApplyPartyKillShareAsync", fact, CancellationToken.None);
            Check.Equal(1, StarterQuestObjectives.Counter(member.Quests[0].Progress, 0), "a repeated fact gives no second credit");
            Check.Equal(1, fixture.Store.Payouts, "a repeated fact pays nothing");

            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(ServerRateKeys.MonsterExperience, 2m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            await InvokeAsync(fixture.Handler, "ApplyPartyKillShareAsync",
                fact with { DeathEventId = Guid.NewGuid() }, CancellationToken.None);
            Check.Equal(80, fixture.Store.LastExperience, "exp.monster applies once to the member's share");
            ServerRates.ResetToDefaults();

            member.Level = PlayerExperienceCatalog.MaximumLevel;
            await InvokeAsync(fixture.Handler, "ApplyPartyKillShareAsync",
                fact with { DeathEventId = Guid.NewGuid() }, CancellationToken.None);
            Check.Equal(2, fixture.Store.Payouts, "a level-200 member gets credit but no EXP");
            Check.Equal(3, StarterQuestObjectives.Counter(member.Quests[0].Progress, 0), "credit still counts at level 200");

            member.Level = 23;
            member.CurrentHp = 0;
            await InvokeAsync(fixture.Handler, "ApplyPartyKillShareAsync",
                fact with { DeathEventId = Guid.NewGuid() }, CancellationToken.None);
            Check.Equal(3, StarterQuestObjectives.Counter(member.Quests[0].Progress, 0), "a dead member gets no credit");
            Check.Equal(2, fixture.Store.Payouts, "a dead member gets no EXP");
        }
        finally
        {
            ServerRates.ResetToDefaults();
            await fixture.Session.DisposeAsync();
        }
    }

    private static GameCharacter Character(int id, string name, float x, float z) =>
        new()
        {
            Id = id,
            AccountId = id,
            Name = name,
            Camp = GameDefaults.AthensCamp,
            CurrentMap = SharedMapId,
            PositionX = x,
            PositionZ = z,
            Level = 40,
            CurrentHp = 5_000,
            MaxHp = 5_000,
            CurrentMp = 2_000,
            MaxMp = 2_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static MonsterRuntimeSnapshot Monster(uint tier, float x, float z)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), tier);
        var spawn = new CapturedMonsterSpawn(SharedMapId, "party-check", "party-monster",
            "Party Monster", 46100, x, z, packet);
        return new MonsterRuntimeSnapshot(spawn, x, z, x, 0, z, 0, 0, 100, false, true, false,
            0, 0, 0, 0, 0, DateTimeOffset.UtcNow, null, null, MonsterCombatPhase.None, null, 1, 1, Guid.NewGuid());
    }

    private static MonsterDamageResult Damage(MonsterRuntimeSnapshot monster, ulong revision = 1)
    {
        var dead = monster with { HealthRevision = revision };
        return new MonsterDamageResult(dead.ObjectId, 100, 0, true, dead,
            new MonsterHealthMutation(dead.ObjectId, dead.SpawnGeneration, revision - 1, revision));
    }

    private static (GameClientHandler Handler, ClientSession Session, ShareStore Store) CreateFixture(GameCharacter character)
    {
        var store = new ShareStore();
        var session = new ClientSession(new ScriptedLegacyByteTransport());
        var npcs = NpcContentBaselineV11.LoadDefinitions();
        var handler = new GameClientHandler(session, store, new GameSessionRegistry(store),
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Create(npcs.Select(npc => npc.MapId).Distinct(), npcs));
        typeof(GameClientHandler).GetField("_character", PrivateInstance)!.SetValue(handler, character);
        typeof(GameClientHandler).GetField("_account", PrivateInstance)!.SetValue(handler,
            new AccountIdentity(character.AccountId, "party-check"));
        return (handler, session, store);
    }

    private static async Task InvokeAsync(GameClientHandler handler, string method, params object[] arguments) =>
        await (Task)typeof(GameClientHandler).GetMethod(method, PrivateInstance)!.Invoke(handler, arguments)!;

    private static async Task InvokeResultAsync(GameClientHandler handler, string method, params object[] arguments) =>
        await (Task)typeof(GameClientHandler).GetMethod(method, PrivateInstance)!.Invoke(handler, arguments)!;

    private sealed class ShareStore : GameStoreTestStub
    {
        public int Payouts { get; private set; }
        public int LastExperience { get; private set; }
        public int LastTalent { get; private set; }

        public override Task SaveCharacterQuestStateAsync(int accountId, int characterId,
            IReadOnlyList<CharacterQuest> quests, IReadOnlyList<uint> completedQuestIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
            int accountId, int characterId, int experience, int talentExperience,
            CancellationToken cancellationToken = default)
        {
            Payouts++;
            LastExperience = experience;
            LastTalent = talentExperience;
            // No settlement: the check observes the amount the command carried.
            return Task.FromResult<CharacterProgressionResult?>(null);
        }
    }
}
