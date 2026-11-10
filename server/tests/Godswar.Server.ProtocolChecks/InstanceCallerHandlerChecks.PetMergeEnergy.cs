using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string PetMergeEnergyCheckName =
        "Admitted Wonderland Atlantis and Medusa preserve pet Merge energy until departure";

    public static async Task RunPetMergeEnergyAsync()
    {
        foreach (var (root, choice, scene, map) in new[]
        {
            (InstanceCallerProtocol.WonderlandRootSubId, InstanceCallerProtocol.WonderlandEnterSubId, 227, 207),
            (InstanceCallerProtocol.AtlantisRootSubId, InstanceCallerProtocol.AtlantisEnterSubId, 224, 205),
            (InstanceCallerProtocol.MedusaRootSubId, InstanceCallerProtocol.NormalDifficultySubId, 223, 204)
        })
        {
            await using var fixture = await CreateFixtureAsync(120, true);
            var probe = new MergeEnergyProbe();
            SetHandlerField(fixture.Handler, "_petDurableCommands", probe);
            fixture.Handler.InstanceEntryClock = new ManualTimeProvider();
            fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
            var source = GetSourceInstanceId(fixture);
            Check.True(await fixture.Handler.AdvancePetOwnerMergeEnergyOnceAsync(0, CancellationToken.None),
                "ordinary world energy tick retains its lifecycle");
            Check.Equal(1, probe.Drains.Count, "ordinary world consumes one Merge energy tick");
            await OpenCountdownAsync(fixture, root, choice);
            await InvokeAsync(fixture.Handler, CreateRepetitionResponse(scene, 0, true), confirmEntry: false);
            await AwaitCountdownAsync(fixture.Handler);
            Check.Equal(map, (int)fixture.Character.CurrentMap, "real native instance admission completed");
            Check.True(fixture.Registry.IsPetOwnerMergeEnergyProtected(fixture.Session),
                "actual admitted member is protected by its bound encounter");
            for (var tick = 0; tick < 4; tick++)
                Check.True(await fixture.Handler.AdvancePetOwnerMergeEnergyOnceAsync(0, CancellationToken.None),
                    "protected interval keeps the lifecycle running for eventual departure");
            Check.Equal(1, probe.Drains.Count, "protected intervals never call the durable drain store");

            // Terminal encounter state does not revoke protection before egress.
            var instance = GetSourceInstanceId(fixture);
            var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
                .GetProperty("WorldInstances", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(fixture.Registry)!;
            Check.True(directory.TryFind(instance, out var runtime), "entered runtime is available");
            if (map == 207) runtime.Map.CancelWonderland(DateTimeOffset.UtcNow);
            if (map == 205) runtime.Map.TryCancelAtlantisEncounter(DateTimeOffset.UtcNow, out _);
            Check.True(await fixture.Handler.AdvancePetOwnerMergeEnergyOnceAsync(0, CancellationToken.None),
                "terminal waiting period remains protected");
            Check.Equal(1, probe.Drains.Count, "terminal egress wait does not consume Merge energy");

            fixture.Character.CurrentMap = fixture.SourceMapId;
            fixture.Registry.JoinWorldInstance(fixture.Session, fixture.Character.AccountId, fixture.Character,
                WorldObjectIds.ForPlayer(fixture.Character.Id), source);
            Check.True(!fixture.Registry.IsPetOwnerMergeEnergyProtected(fixture.Session),
                "committed departure returns to normal drain policy");
            Check.True(await fixture.Handler.AdvancePetOwnerMergeEnergyOnceAsync(0, CancellationToken.None),
                "first ordinary-map tick resumes Merge drain");
            Check.Equal(2, probe.Drains.Count, "departure charges one tick without catching up protected intervals");
            Check.True(probe.Drains.All(drain => drain.Amount == 1 &&
                drain.Subject.CharacterId == fixture.Character.Id && drain.Ownership.IsValid),
                "the durable call retains its original subject ownership fence and one-point amount");
            Check.True(!await fixture.Handler.AdvancePetOwnerMergeEnergyOnceAsync(-1, CancellationToken.None),
                "stale timer generation cannot drain after transition");
            Check.Equal(2, probe.Drains.Count, "stale generation has no durable side effects");
        }
        await CheckUnadmittedMergeEnergyAsync();
    }

    private static async Task CheckUnadmittedMergeEnergyAsync()
    {
        await using var fixture = await CreateFixtureAsync(120, true);
        foreach (byte map in new byte[] { 200, 204, 205, 207 })
        {
            var unbound = await fixture.Registry.CreateLocalWorldInstanceAsync(RealmId.Tempest,
                new(map), InstanceKind.Dungeon, 1);
            var unboundRuntime = unbound.Runtime ??
                throw new InvalidOperationException("Unbound exact dungeon fixture creation failed.");
            fixture.Character.CurrentMap = map;
            fixture.Registry.JoinWorldInstance(fixture.Session, fixture.Character.AccountId, fixture.Character,
                WorldObjectIds.ForPlayer(fixture.Character.Id), unboundRuntime.InstanceId);
            Check.True(!fixture.Registry.IsPetOwnerMergeEnergyProtected(fixture.Session),
                "exact membership in a matching unbound dungeon does not grant an energy exemption");
        }
        var created = await fixture.Registry.CreateLocalWorldInstanceAsync(RealmId.Tempest,
            new(205), InstanceKind.Dungeon, 2);
        var runtime = created.Runtime ?? throw new InvalidOperationException("Atlantis energy fixture creation failed.");
        var ownership = GameHandlerOwnershipTestFences.Bind(fixture.Registry, fixture.Session,
            fixture.Character.AccountId, fixture.Character);
        var reservation = Guid.NewGuid();
        Check.True(fixture.Registry.TryStartAtlantisEncounter(runtime.InstanceId, 3,
            [(fixture.Character.Id, 120)], runtime.Descriptor.CreatedAt, reservation,
            [new(fixture.Session, fixture.Character.AccountId, fixture.Character.Id, fixture.Character.Name,
                120, fixture.Character.Profession, RealmId.Tempest, GetSourceInstanceId(fixture), 207, ownership)]),
            "the admission fixture starts a real Atlantis encounter");
        fixture.Character.CurrentMap = 205;
        fixture.Registry.JoinWorldInstance(fixture.Session, fixture.Character.AccountId, fixture.Character,
            WorldObjectIds.ForPlayer(fixture.Character.Id), runtime.InstanceId);
        Check.True(!fixture.Registry.IsPetOwnerMergeEnergyProtected(fixture.Session),
            "mere membership in a real run cannot substitute for recorded admission");
        fixture.Registry.RecordAtlantisRewardAdmissions(reservation, [fixture.Character.Id]);
        Check.True(fixture.Registry.IsPetOwnerMergeEnergyProtected(fixture.Session),
            "recorded admission activates protection for the same exact member");
        await using var replacement = new ClientSession(new FactionCrierCaptureTransport());
        GameHandlerOwnershipTestFences.Bind(fixture.Registry, replacement, fixture.Character.AccountId, fixture.Character);
        Check.True(!fixture.Registry.IsPetOwnerMergeEnergyProtected(fixture.Session),
            "stale ownership cannot borrow an earlier admission's exemption");
        fixture.Registry.Remove(replacement);
    }

    private sealed class MergeEnergyProbe : DelegatingPetDurableCommandExecutor, IPetOwnerMergeLifecycleStore
    {
        public List<(CommandSubject Subject, PlayerOwnershipFence Ownership, int Amount)> Drains { get; } = [];
        public Task<PetOwnerMergeLifecycleResult> DrainEnergyAsync(CommandSubject subject,
            PlayerOwnershipFence ownership, int energyPoints, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Drains.Add((subject, ownership, energyPoints));
            return Task.FromResult(new PetOwnerMergeLifecycleResult(PetOwnerMergeLifecycleStatus.EnergyChanged,
                1, 100 - Drains.Count, 100, Drains.Count, true, false));
        }
        public Task<PetOwnerMergeLifecycleResult> RestoreEnergyAsync(CommandSubject subject,
            PlayerOwnershipFence ownership, int energyPoints, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The drain policy must not recharge an active Merge.");
        public Task<PetOwnerMergeLifecycleResult> EndAsync(CommandSubject subject,
            PlayerOwnershipFence ownership, PetOwnerMergeEndReason reason, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Protected Merge must not be ended.");
    }
}
