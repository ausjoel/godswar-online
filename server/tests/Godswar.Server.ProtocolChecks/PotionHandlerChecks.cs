using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PotionHandlerChecks
{
    public const string CheckName = "Live potion handler coordinates both checkpoint writers damage death replay and recovery";
    public static async Task RunAsync()
    {
        await CheckBarrierAsync(killed: false, item: 4000);
        await CheckBarrierAsync(killed: false, item: 4030);
        await CheckBarrierAsync(killed: true, item: 4000);
        await CheckRecoveryAsync(unknown: false, loseOwner: false);
        await CheckRecoveryAsync(unknown: true, loseOwner: false);
        await CheckRecoveryAsync(unknown: false, loseOwner: true);
        CheckZeroRevision();
    }

    private static async Task CheckBarrierAsync(bool killed, uint item)
    {
        var live = Character(item, 2);
        var coordinator = new Coordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PetDurableReceipt? receipt = null;
        var executor = new DelegatingPetDurableCommandExecutor
        {
            ActivateAsync = async (envelope, token) =>
            {
                if (receipt is not null) return PetDurableExecutionResult.Duplicate(receipt);
                Check.True(envelope.Command.PotionContext is not null, "handler supplies trusted calculated-vitals context");
                Check.Equal(1, coordinator.Writes.Count, "before snapshot flushed before DB execution");
                Check.Equal(12L, coordinator.Writes.Single().Revision, "pre-use revision flushed, never reserved revision");
                receipt = Receipt(envelope);
                started.SetResult();
                await release.Task.WaitAsync(token);
                return PetDurableExecutionResult.Committed(receipt);
            }
        };
        await using var fixture = Fixture(live, coordinator, executor, item);
        var operation = Guid.NewGuid();
        var running = fixture.InvokeAsync(Packet(operation));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(live.PendingPotionUse is { HoldsCheckpoints: true }, "hold attached to shared live character");
        Check.Equal(13L, live.VitalsRevision, "reserved DB revision installed before await");
        lock (live.VitalsSync)
        {
            live.CurrentHp = killed ? 0 : live.CurrentHp - 300;
            live.CurrentMp -= 30;
            live.MarkVitalsChanged();
        }
        await fixture.Registry.PersistPlayerVitalsAsync(live.AccountId, live, CancellationToken.None);
        await InvokeHandlerCheckpoint(fixture.Handler, live, force: false);
        Check.Equal(1, coordinator.Writes.Count, "registry and handler routine writes both defer during pending save");
        Check.Equal(0, coordinator.Enqueued.Count, "no damage checkpoint can race reserved settlement revision");
        var forcedRegistry = InvokeRegistryFlush(fixture.Registry, live);
        var forcedHandler = InvokeHandlerCheckpoint(fixture.Handler, live, force: true);
        Check.True(!forcedRegistry.IsCompleted && !forcedHandler.IsCompleted, "both forced flushes wait for resolution");
        var refresh = typeof(GameClientHandler).GetMethod("InstallUpdatedCharacterVitals", BindingFlags.Static | BindingFlags.NonPublic)!;
        var guarded = false;
        try { refresh.Invoke(null, [live, Character(item, 1)]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { guarded = true; }
        Check.True(guarded, "snapshot replacement cannot drop pending checkpoint hold");
        release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(forcedRegistry, forcedHandler).WaitAsync(TimeSpan.FromSeconds(5));
        var hp = killed ? 0 : item == 4000 ? 53900 : 53700;
        var mp = item == 4030 ? 9930 : 9870;
        if (killed) mp = 9870;
        Check.Equal(hp, live.CurrentHp, "settlement preserves concurrent damage and never resurrects");
        Check.Equal(mp, live.CurrentMp, "settlement preserves spell cost and restores only intended resource");
        Check.True(live.PendingPotionUse is null, "known result releases shared hold");
        foreach (var write in coordinator.Writes.Skip(1))
        {
            Check.Equal(hp, write.CurrentHp, "forced flush recaptures reconciled HP, not stale snapshot");
            Check.Equal(mp, write.CurrentMp, "forced flush recaptures reconciled MP");
            Check.Equal(15L, write.Revision, "checkpoint revision follows settlement and intervening damage");
        }
        Check.True(fixture.SavedVitals is null, "coordinator used instead of legacy direct save");
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(!packets.Any(p => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) is 10237 or 10090),
            "potion projection cannot rebuild pets or replay accepted quests");
        var current = Current(fixture.Handler);
        Check.True(ReferenceEquals(live, current), "potion inventory refresh preserves the live combat vitals object");
        Check.Equal(hp, current.CurrentHp, "snapshot reload retains live reconciled health");
        if (!killed)
        {
            lock (current.VitalsSync) { current.CurrentHp -= 500; current.MarkVitalsChanged(); }
            var afterDamage = current.CurrentHp;
            await fixture.InvokeAsync(Packet(operation));
            Check.Equal(afterDamage, Current(fixture.Handler).CurrentHp, "historical duplicate after later damage never heals again");
        }
    }

    private static async Task CheckRecoveryAsync(bool unknown, bool loseOwner)
    {
        var live = Character(4000, 2); var coordinator = new Coordinator();
        var attempts = new List<CommandEnvelope<BagItemActivationCommand>>();
        PetDurableReceipt? receipt = null;
        var executor = new DelegatingPetDurableCommandExecutor
        {
            ActivateAsync = (envelope, _) =>
            {
                attempts.Add(envelope);
                receipt ??= Receipt(envelope);
                if (attempts.Count == 1)
                {
                    lock (live.VitalsSync)
                    {
                        live.CurrentHp -= 300; live.MarkVitalsChanged();
                        if (loseOwner) live.CheckpointOwnerGeneration++;
                    }
                }
                if (unknown || !loseOwner && attempts.Count == 1)
                    return Task.FromException<PetDurableExecutionResult>(new IOException("simulated interrupted commit result"));
                return Task.FromResult(PetDurableExecutionResult.Duplicate(receipt));
            }
        };
        await using var fixture = Fixture(live, coordinator, executor, 4000);
        await fixture.InvokeAsync(Packet(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(5));
        if (unknown || loseOwner)
        {
            Check.True(fixture.Session.IsDisconnected, "unresolved commit or lost owner disconnects for safe reload");
            Check.True(live.PendingPotionUse is { HoldsCheckpoints: true }, "uncertain result retains checkpoint quarantine");
            await fixture.Registry.PersistPlayerVitalsAsync(live.AccountId, live, CancellationToken.None);
            var rejected = false;
            try { await InvokeRegistryFlush(fixture.Registry, live).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (InvalidOperationException) { rejected = true; }
            Check.True(rejected, "forced registry flush fails explicitly instead of hanging or overwriting unknown heal");
            rejected = false;
            try { await InvokeHandlerCheckpoint(fixture.Handler, live, force: true).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (InvalidOperationException) { rejected = true; }
            Check.True(rejected, "teardown handler flush cannot overwrite unresolved heal");
            Check.Equal(1, coordinator.Writes.Count, "only pre-use checkpoint admitted while commit remains unknown");
            Check.Equal(53700, live.CurrentHp, "uncertain or stale-owner receipt cannot add unproven live healing");
        }
        else
        {
            Check.Equal(2, attempts.Count, "interrupted await resolved through one exact-operation retry");
            Check.Equal(attempts[0].OperationId, attempts[1].OperationId, "recovery does not create a second item-use operation");
            Check.Equal(attempts[0].RequestHash, attempts[1].RequestHash, "recovery preserves canonical request");
            Check.Equal(53900, live.CurrentHp, "recovered duplicate heals its still-pending use exactly once");
            Check.True(live.PendingPotionUse is null && !fixture.Session.IsDisconnected, "recovered commit releases hold and remains playable");
        }
    }

    private static void CheckZeroRevision()
    {
        var character = Character(4000, 2); character.VitalsRevision = 0;
        var owner = PlayerOwnershipTestFences.ForCharacter(character.Id);
        character.CheckpointOwnerId = owner.OwnerId; character.CheckpointOwnerGeneration = owner.Generation;
        BagConsumableEffectCatalog.TryResolve(3100, out var effect);
        Check.True(PendingPotionVitals.TryPrepare(character, 4000, effect, owner, out var pending, out var context),
            "injured freshly hydrated character can reserve potion");
        Check.Equal(1L, context!.Value.Before.Revision, "zero revision normalized before positive checkpoint contract");
        Check.Equal(2L, character.VitalsRevision, "normalized reservation uses distinct revision");
        Check.True(pending!.TryResolve(false), "known pre-submit abort releases hold without healing");
        Check.Equal(54000, character.CurrentHp, "pre-submit abort changes no health");
    }

    private static PetDurableHandlerFixture Fixture(GameCharacter live, Coordinator coordinator,
        DelegatingPetDurableCommandExecutor executor, uint item)
    {
        var persisted = Character(item, 1);
        var stats = CharacterSnapshotContractChecks.CreateValidSnapshot().Character!.CalculatedStats with
            { MaxHp = 55000, MaxMp = 10000, CurrentHp = 54200, CurrentMp = 9900 };
        return PetDurableHandlerFixture.Create(live, persisted, [], executor, persistedStats: stats,
            characterCheckpoints: coordinator, itemContent: Content());
    }

    private static GameCharacter Character(uint item, byte stack) => new()
    {
        Id = 7020, AccountId = 44, Name = "PotionHandler", MaxHp = 55000, MaxMp = 10000,
        CurrentHp = 54000, CurrentMp = 9900, VitalsRevision = 12,
        KitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 3,
            (default(CompactItemEntry) with { Id = item, Quality = 1, Grade = 1, Stack = stack }).ToCompactString())
    };
    private static GameplayItemContent Content()
    {
        ItemTemplateDefinition Item(uint id, int skill) => new(id, "consume item", "Potion", "Potion", 0, [],
            1, 200, null, 0, "t.gwo", "1,1", new JsonObject { ["ID"] = id.ToString(), ["Use"] = "1", ["Skill"] = skill.ToString() }.ToJsonString());
        var templates = TestItemContent.Catalog.All.Where(t => t.Id is not (4000 or 4030))
            .Concat([Item(4000, 3100), Item(4030, 3120)]).ToArray();
        return new(PinnedItemTemplateCatalog.Create("potion-handler", templates, [], [], []));
    }
    private static PetDurableReceipt Receipt(CommandEnvelope<BagItemActivationCommand> envelope)
    {
        var context = envelope.Command.PotionContext!.Value;
        var skill = context.ItemTemplateId == 4030 ? 3120 : 3100;
        BagConsumableEffectCatalog.TryResolve(skill, out var effect);
        Check.True(PotionUsePolicy.TryPlan(context.Before, effect, out var plan), "handler provided valid potion context");
        var evidence = new PotionUseEvidence(42, context.ItemTemplateId, 3, skill, plan!.AppliedAmount,
            plan.Before, plan.PersistedVitals, envelope.Ownership);
        return new(CommandFamily.BagItemActivation, PetDurableReceiptStatus.PotionUsed,
            44, 7020, 3, -1, 0, 0, 0, 0, false, false, 0, 1, "42", Guid.NewGuid(), Potion: evidence);
    }
    private static GamePacket Packet(Guid operation)
    {
        var bytes = new byte[16]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 10051);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 3); return new(bytes, operation);
    }
    private static GameCharacter Current(GameClientHandler handler) =>
        (GameCharacter)typeof(GameClientHandler).GetField("_character", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handler)!;
    private static Task InvokeHandlerCheckpoint(GameClientHandler handler, GameCharacter character, bool force) =>
        (Task)typeof(GameClientHandler).GetMethod("PersistVitalsCheckpointAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(handler, [character, force, CancellationToken.None])!;
    private static Task InvokeRegistryFlush(GameSessionRegistry registry, GameCharacter character) =>
        (Task)typeof(GameSessionRegistry).GetMethod("FlushVitalsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(registry, [character.AccountId, character, CancellationToken.None])!;

    private sealed class Coordinator : ICharacterCheckpointCoordinator
    {
        public ConcurrentQueue<CharacterVitalsCheckpoint> Writes { get; } = new();
        public ConcurrentQueue<CharacterVitalsCheckpoint> Enqueued { get; } = new();
        public Task<CharacterCheckpointWriteResult> FlushThroughAsync(CharacterVitalsCheckpoint checkpoint, CancellationToken cancellationToken = default)
        { checkpoint.Validate(); Writes.Enqueue(checkpoint); return Task.FromResult(new CharacterCheckpointWriteResult(CharacterCheckpointWriteStatus.Applied, checkpoint.Revision)); }
        public Task<CharacterCheckpointWriteResult> FlushThroughAsync(CharacterPositionCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CharacterCheckpointWriteResult(CharacterCheckpointWriteStatus.Applied, checkpoint.Revision));
        public CharacterCheckpointEnqueueResult TryEnqueue(CharacterVitalsCheckpoint checkpoint)
        { Enqueued.Enqueue(checkpoint); return new(CharacterCheckpointEnqueueStatus.Accepted, checkpoint.Revision); }
        public CharacterCheckpointEnqueueResult TryEnqueue(CharacterPositionCheckpoint checkpoint) => new(CharacterCheckpointEnqueueStatus.Accepted, checkpoint.Revision);
        public Task<CharacterCheckpointOwnership?> AcquireAsync(int accountId, int characterId, Guid ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterCheckpointOwnership?>(new(new(ownerId, 1), 0, 0));
        public Task<CharacterCheckpointReleaseStatus> ReleaseAsync(int accountId, int characterId, PlayerOwnershipFence owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(CharacterCheckpointReleaseStatus.Released);
        public Task RunAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public CharacterCheckpointRuntimeSnapshot GetSnapshot() => new(CharacterCheckpointRuntimeState.Ready, 100, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, null);
        public void Complete() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
