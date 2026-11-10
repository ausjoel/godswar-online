using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task<WonderlandHandlerFixture> CreateWonderlandHandlerFixtureAsync(
        int partySize = 1, IReadOnlySet<int>? failedFollowers = null)
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        var party = await CreateAtlantisOpalFixtureAsync(daily, null, failedFollowers, partySize);
        foreach (var character in party.Characters)
        {
            character.Level = 120;
            character.MedusaHonorPoints = 1234;
            character.MedusaRewardRevision = 7;
            character.AddOwnedTitle(5009);
            character.SelectedTitleId = character == party.Leader.Character ? 0u : 5009u;
        }
        party.Leader.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
        var titles = new WonderlandHandlerTitleStore(party.Characters);
        party.Leader.Registry.ConfigureWonderlandTitles(titles, daily);
        party.Leader.Registry.RegisterAuthoritativeInstanceTransitionSink(party.Leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(party.Leader.Handler, command, token));
        return new(party, daily, titles);
    }

    private static async Task<WorldInstanceRuntime> EnterWonderlandHandlerAsync(WonderlandHandlerFixture fixture)
    {
        var leader = fixture.Party.Leader;
        var source = GetSourceInstanceId(leader);
        await InvokeAsync(leader.Handler, CreateActionPacket(InstanceCallerProtocol.WonderlandRootSubId));
        await InvokeAsync(leader.Handler, CreateActionPacket(InstanceCallerProtocol.WonderlandRootSubId,
            InstanceCallerProtocol.WonderlandEnterSubId));
        var id = GetSourceInstanceId(leader);
        Check.True(id != source && leader.Character.CurrentMap == 207,
            "the proved Wonderland menu enters an exact map207 dungeon through the real handler");
        var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
            .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(leader.Registry)!;
        Check.True(directory.TryFind(id, out var runtime), "Wonderland entry creates its runtime");
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var run) && run.PublicationPending &&
            run.State == WonderlandRunState.Active && run.CurrentIsland == 1 &&
            run.Deadline - run.StartedAt == TimeSpan.FromMinutes(40) && runtime.Map.SnapshotMonsters().Count == 0,
            "entry starts the forty-minute clock but no monster is exposed before admission sealing");
        await CompleteWonderlandReadinessAsync(fixture.Party);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out run) && !run.PublicationPending &&
            run.RequiredMonstersRemaining == 1 && runtime.Map.SnapshotMonsters().Count(monster => monster.IsAlive) == 17,
            "the first world tick publishes Alpha Demon and sixteen captured supports after admissions are sealed");
        return runtime;
    }

    private static async Task CompleteWonderlandReadinessAsync(AtlantisOpalFixture party)
    {
        if (party.Leader.Character.CurrentMap == 207)
            await CompleteAtlantisSceneReadinessAsync(party.Leader.Handler);
        foreach (var follower in party.Followers.Where(member => member.Character.CurrentMap == 207))
            await CompleteAtlantisSceneReadinessAsync(follower.Handler);
    }

    private static DateTimeOffset WonderlandNow(WorldInstanceRuntime runtime)
    {
        runtime.Map.TryGetWonderlandSnapshot(out var run);
        var wall = DateTimeOffset.UtcNow;
        return wall > run.LastObservedAt ? wall : run.LastObservedAt.AddTicks(1);
    }

    private static MonsterDamageResult KillWonderlandHandlerMonster(WonderlandHandlerFixture fixture,
        WorldInstanceRuntime runtime, uint objectId)
    {
        var monster = runtime.Map.SnapshotMonsters().Single(value => value.ObjectId == objectId);
        var now = WonderlandNow(runtime);
        Check.True(runtime.Map.TryApplyMonsterDamageGuarded(objectId, monster.CurrentHealth,
                fixture.Party.Leader.Character.Id, monster.SpawnGeneration, monster.HealthRevision, now,
                out var damage) && damage.Killed,
            "Wonderland fixture commits a real lethal authoritative monster health mutation");
        fixture.Party.Leader.Registry.RecordWonderlandMonsterKillCommitted(runtime, damage, now);
        return damage;
    }

    private static async Task ClearWonderlandHandlerIslandAsync(WonderlandHandlerFixture fixture,
        WorldInstanceRuntime runtime)
    {
        runtime.Map.TryGetWonderlandSnapshot(out var run);
        foreach (var required in run.ActiveSpawns.Where(value => value.RequiredForProgression))
            KillWonderlandHandlerMonster(fixture, runtime, required.ObjectId);
        await fixture.Party.Leader.Registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
    }

    private static async Task ClickWonderlandPortalAsync(InstanceCallerFixture actor, int island)
    {
        var teleporter = WonderlandTraversalPolicy.GetTeleporter(island);
        actor.Character.PositionX = teleporter.X;
        actor.Character.PositionZ = teleporter.Z;
        actor.Registry.UpdateCharacter(actor.Session, actor.Character, advanceWorldRevision: false);
        var refresh = FindHandlerMethod("RefreshNearbyWorldObjectsAsync").Invoke(actor.Handler,
            ["WonderlandPortalTest", CancellationToken.None]) as Task ??
            throw new InvalidOperationException("Expected the actual NPC visibility refresh task.");
        await refresh;
        var bytes = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 48);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),
            WonderlandTraversalPolicy.FirstTeleporterObjectId + (uint)island - 1);
        await InvokeAsync(actor.Handler, new GamePacket(bytes));
        Check.True(actor.ReadPackets().Last().SequenceEqual(PacketBuilder.NpcDialogOpenAck(
                teleporter.InteractionId, 57, teleporter.NpcKey)),
            "click opens the captured Teleport dialogue before any travel");
        await InvokeAsync(actor.Handler, CreateWonderlandTransportAction(teleporter.InteractionId, 57));
    }

    private sealed class WonderlandSaturdayClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed record WonderlandHandlerFixture(AtlantisOpalFixture Party,
        ScriptedLegacyInstanceDailyEntryStore Daily, WonderlandHandlerTitleStore Titles) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Party.Leader.Registry.UnregisterAuthoritativeInstanceTransitionSink(Party.Leader.Session);
            await Party.DisposeAsync();
        }
    }

    private sealed class WonderlandHandlerTitleStore(IReadOnlyList<GameCharacter> characters) : IWonderlandTitleStore
    {
        private readonly Dictionary<string, WonderlandTitleReceipt> _receipts = [];
        private readonly Dictionary<int, long> _revisions = characters.ToDictionary(c => c.Id, c => c.MedusaRewardRevision);
        public List<WonderlandTitleRequest> Requests { get; } = [];
        public List<WonderlandTitleReceipt> AppliedReceipts { get; } = [];
        public int FailuresRemaining { get; set; }

        public Task<WonderlandTitleReceipt> SettleAsync(WonderlandTitleRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromException<WonderlandTitleReceipt>(new IOException("scripted title store unavailable"));
            }
            if (_receipts.TryGetValue(request.RequestHash, out var previous))
                return Task.FromResult(previous with { Status = WonderlandTitleStatus.Duplicate });
            var members = request.FrozenMembers.Select(member =>
            {
                var character = characters.Single(value => value.Id == member.CharacterId);
                return new WonderlandTitleReceiptMember(member.AccountId, member.CharacterId,
                    character.MedusaHonorPoints, character.SelectedTitleId, ++_revisions[character.Id],
                    !character.OwnedTitleIds.Contains(request.Award.TitleId));
            }).ToArray();
            var receipt = new WonderlandTitleReceipt(WonderlandTitleStatus.Applied, request.WorldInstanceId,
                request.Award, members);
            _receipts.Add(request.RequestHash, receipt);
            AppliedReceipts.Add(receipt);
            return Task.FromResult(receipt);
        }
    }
}
