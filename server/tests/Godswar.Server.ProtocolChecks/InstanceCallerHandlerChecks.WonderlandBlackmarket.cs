using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandBlackmarketCheckName =
        "Wonderland Blackmarket native paid choices preserve wallet, life, and relocation authority";

    public static async Task RunWonderlandBlackmarketAsync()
    {
        foreach (var function in new[] { 59, 62, 63 })
        {
            await CheckWonderlandBlackmarketSuccessAsync(function, clearedIslands: 0);
            await CheckWonderlandBlackmarketSuccessAsync(function);
            await CheckWonderlandBlackmarketInsufficientAsync(function);
        }
        await CheckWonderlandBlackmarketSuccessAsync(59, ambiguousCommit: true);
        foreach (var fault in new[] { "registration", "charge-life", "wallet-life" })
            await CheckWonderlandBlackmarketCompensationAsync(fault);
    }

    private static async Task CheckWonderlandBlackmarketSuccessAsync(int function, bool ambiguousCommit = false,
        int clearedIslands = 2)
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var leader = fixture.Party.Leader;
        for (var island = 0; island < clearedIslands; island++)
            await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var store = InstallBlackmarketHandlerStore(leader);
        if (ambiguousCommit)
            store.AfterCharge = () =>
            {
                store.AfterCharge = null;
                throw new IOException("The debit committed before its response was lost.");
            };
        var target = WonderlandTerrainPolicy.GetIsland(clearedIslands + 1).Entrance;
        leader.Character.PositionX = target.X;
        leader.Character.PositionZ = target.Z;
        leader.Character.CurrentHp = 0;
        leader.Character.CurrentMp = 0;
        leader.Registry.UpdateCharacter(leader.Session, leader.Character, advanceWorldRevision: false);
        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448));
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        var first = WonderlandTerrainPolicy.GetIsland(1).Entrance;
        Check.True(leader.Character.PositionX == first.X && leader.Character.PositionZ == first.Z &&
            runtime.Map.TryGetWonderlandSnapshot(out var revivedRun) && revivedRun.CompletedIslands == clearedIslands,
            "death revives beside the entrance NPC while retaining the original completed islands");
        var revivedHp = leader.Character.CurrentHp;
        var revivedMp = leader.Character.CurrentMp;
        var before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5221));
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5221, function));
        var cost = function switch { 59 => 5000, 62 => 6000, _ => 8000 };
        var expectedAttempts = ambiguousCommit ? 2 : 1;
        var request = store.Charges.First();
        Check.True(request.Cost == cost && request.Function == function && request.TargetIsland == clearedIslands + 1 &&
            request.AdmissionReservationId == fixture.Daily.Claims.Single().ReservationId &&
            request.Subject.CharacterId == leader.Character.Id && request.Subject.AccountId == leader.Character.AccountId &&
            request.Instance == runtime.InstanceId && request.OperationId != Guid.Empty && request.Ownership.IsValid &&
            store.Charges.Count == expectedAttempts && store.DebitCount == 1 &&
            store.Charges.All(attempt => attempt == request) &&
            store.Refunds.Count == 0 && store.Reads == 1 && leader.Character.Silver == 50000 - cost &&
            leader.Character.PositionX == target.X && leader.Character.PositionZ == target.Z &&
            GetSourceInstanceId(leader) == runtime.InstanceId &&
            leader.ReadPackets().Skip(before).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
            $"native service {function} debits once and travels once, including an exact replay after an ambiguous commit");
        Check.True(leader.Character.CurrentHp == (function == 59 ? revivedHp : leader.Character.MaxHp) &&
            leader.Character.CurrentMp == (function == 63 ? leader.Character.MaxMp : revivedMp),
            "Speedy preserves vitals, full-HP restores only HP, and full-HP/MP restores both");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5221, function));
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5221));
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5221, function));
        Check.True(store.Charges.Count == expectedAttempts && store.DebitCount == 1,
            "repeated action and a stale entrance click during travel cannot produce another debit");
    }

    private static async Task CheckWonderlandBlackmarketInsufficientAsync(int function)
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var leader = fixture.Party.Leader;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var store = InstallBlackmarketHandlerStore(leader, silver: 4999);
        await MoveToWonderlandTransportAsync(leader, 5221, 165, -219);
        var hp = leader.Character.CurrentHp;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5221));
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5221, function));
        Check.True(store.Charges.Count == 1 && store.Refunds.Count == 0 && store.Reads == 0 &&
            leader.Character.Silver == 4999 && leader.Character.CurrentHp == hp &&
            leader.Character.PositionX == 165 && leader.Character.PositionZ == -219 &&
            leader.ReadPackets().Last().SequenceEqual(PacketBuilder.CapturedNpcFunctionActionResponse(5221, 59, [0, 141])),
            "insufficient funds preserve position/vitals and emit the native silver explanation without a refund or projection");
    }

    private static async Task CheckWonderlandBlackmarketCompensationAsync(string fault)
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var leader = fixture.Party.Leader;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var store = InstallBlackmarketHandlerStore(leader);
        leader.Character.CurrentHp = 100;
        leader.Character.CurrentMp = 17;
        await MoveToWonderlandTransportAsync(leader, 5221, 165, -219);
        Action newLife = () =>
        {
            leader.Registry.AdvancePlayerLifeRevision(leader.Session);
            leader.Character.CurrentHp = 2;
            leader.Character.CurrentMp = 3;
        };
        if (fault == "registration")
            store.AfterCharge = () => SetHandlerField(leader.Handler, "_registered", false);
        else if (fault == "charge-life") store.AfterCharge = newLife;
        else store.BeforeRead = newLife;
        var before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5221));
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5221, 63));
        Check.True(store.Charges.Count == 1 && store.Refunds.Count == 1 &&
            store.Refunds.Single().OperationId == store.Charges.Single().OperationId &&
            leader.Character.Silver == 50000 && leader.Character.PositionX == 165 && leader.Character.PositionZ == -219 &&
            leader.Character.CurrentHp == (fault == "registration" ? 100 : 2) &&
            leader.Character.CurrentMp == (fault == "registration" ? 17 : 3) &&
            leader.ReadPackets().Skip(before).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            $"{fault} between debit and relocation refunds the exact charge and never grants healing or travel");
    }

    private static BlackmarketHandlerStore InstallBlackmarketHandlerStore(InstanceCallerFixture leader, int silver = 50000)
    {
        var store = new BlackmarketHandlerStore(silver);
        leader.Character.Silver = silver;
        leader.Registry.ConfigureWonderlandBlackmarket(store);
        SetHandlerField<ICharacterSnapshotReader>(leader.Handler, "_characterSnapshots", store);
        return store;
    }

    private sealed class BlackmarketHandlerStore(int silver) : IWonderlandBlackmarketStore, ICharacterSnapshotReader
    {
        public List<WonderlandBlackmarketRequest> Charges { get; } = [];
        public List<WonderlandBlackmarketRequest> Refunds { get; } = [];
        public int DebitCount { get; private set; }
        public int Reads { get; private set; }
        public Action? AfterCharge { get; set; }
        public Action? BeforeRead { get; set; }
        private int _silver = silver;
        private long _revision;
        private readonly Dictionary<Guid, WonderlandBlackmarketReceipt> _committed = [];

        public Task<WonderlandBlackmarketReceipt> ChargeAsync(WonderlandBlackmarketRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check.True(request.IsValid, "handler emits a valid fenced Blackmarket debit request");
            Charges.Add(request);
            if (_committed.TryGetValue(request.OperationId, out var prior)) return Task.FromResult(prior);
            if (_silver < request.Cost)
                return Task.FromResult(new WonderlandBlackmarketReceipt(WonderlandBlackmarketStatus.InsufficientSilver, _silver, _revision));
            _silver -= request.Cost;
            _revision++;
            DebitCount++;
            var committed = new WonderlandBlackmarketReceipt(WonderlandBlackmarketStatus.Committed, _silver, _revision);
            _committed.Add(request.OperationId, committed);
            AfterCharge?.Invoke();
            return Task.FromResult(committed);
        }

        public Task<WonderlandBlackmarketReceipt> RefundAsync(WonderlandBlackmarketRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Refunds.Add(request);
            _silver += request.Cost;
            return Task.FromResult(new WonderlandBlackmarketReceipt(WonderlandBlackmarketStatus.Committed, _silver, ++_revision));
        }

        public Task<CharacterAccountSnapshot> ReadAsync(int accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            var callback = BeforeRead;
            BeforeRead = null;
            callback?.Invoke();
            var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
            Check.Equal(snapshot.AccountId, accountId, "Blackmarket projection requests its authenticated account");
            return Task.FromResult(snapshot with { Character = snapshot.Character! with
                { Wallet = snapshot.Character.Wallet with { Silver = _silver } } });
        }
    }
}
