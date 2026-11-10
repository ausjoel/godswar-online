using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string TitleSelectionCheckName =
        "Native owned title equip and hide preserve ownership, wallet, and session fences";

    public static async Task RunTitleSelectionAsync()
    {
        await CheckNativeTitleSelectionAsync();
        await CheckTitleSelectionRefusalsAsync();
        await CheckDelayedTitleSelectionAsync();
        await CheckTitleSelectionOwnershipLossAsync();
    }

    private static async Task CheckNativeTitleSelectionAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 3);
        await PrepareAtlantisDepartureAsync(fixture);
        var leader = fixture.Leader;
        var sameWorld = fixture.Followers[0];
        var otherWorld = fixture.Followers[1];
        Check.True(leader.Registry.TryTransferMap(otherWorld.Session, 205, 0, 136f, -150f) &&
            leader.Registry.TryMarkWorldReady(otherWorld.Session, new Dictionary<uint, long>(), out _),
            "title projection fixture has a ready observer in another exact world instance");
        await FlushAtlantisDepartureAsync(otherWorld.Session);
        var store = SeedTitleSelection(leader);
        leader.Registry.ConfigureCharacterTitleSelections(store);
        var hp = leader.Character.CurrentHp;
        var mp = leader.Character.CurrentMp;
        var owned = leader.Character.OwnedTitleIds.ToArray();
        var expectedRevision = 20L;
        foreach (var selected in new uint[] { 5013, 5014, 0, 5009, 5009 })
        {
            var before = leader.ReadPackets().Count;
            var sameBefore = sameWorld.Transport.ReadLegacyPackets().Count;
            var otherBefore = otherWorld.Transport.ReadLegacyPackets().Count;
            var previous = leader.Character.SelectedTitleId;
            await InvokeAsync(leader.Handler, CreateTitleSelectionPacket(selected));
            if (previous != selected) expectedRevision++;
            Check.True(leader.Character.SelectedTitleId == selected &&
                leader.Character.MedusaRewardRevision == expectedRevision &&
                leader.Character.MedusaHonorPoints == 1234 &&
                leader.Character.OwnedTitleIds.SequenceEqual(owned) &&
                leader.Character.CurrentHp == hp && leader.Character.CurrentMp == mp,
                "native equip/hide changes only display selection and its durable revision");
            AssertTitleSelectionRefresh(leader.ReadPackets().Skip(before), selected);
            var observerPackets = sameWorld.Transport.ReadLegacyPackets().Skip(sameBefore).ToArray();
            Check.True(observerPackets.Count(IsTitleDisplayPacket) == 1 &&
                observerPackets.All(packet => ReadOpcode(packet) != Opcodes.DesignationInfo) &&
                ReadSelectedDisplay(observerPackets.Single(IsTitleDisplayPacket)) == selected,
                "same-instance observers see the selected title without receiving the private ownership list");
            Check.True(!otherWorld.Transport.ReadLegacyPackets().Skip(otherBefore).Any(IsTitleSelectionPacket),
                "title selection never publishes into a different world instance");
        }
        Check.True(store.Requests.All(request =>
            request.Subject.AccountId == leader.Character.AccountId &&
            request.Subject.CharacterId == leader.Character.Id && request.Ownership.IsValid),
            "native title requests always bind the requesting account, character, and checkpoint owner");
    }

    private static async Task CheckTitleSelectionRefusalsAsync()
    {
        await using var fixture = await CreateFixtureAsync(90, transitionReady: true);
        Check.True(fixture.Registry.TryMarkWorldReady(fixture.Session,
            new Dictionary<uint, long>(), out _), "title refusal fixture is world ready");
        var store = SeedTitleSelection(fixture);
        // Hide is optimistic in Origin: even an unavailable store must restore
        // the authoritative list header and visible title, without a mutation.
        var before = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateTitleSelectionPacket(0));
        AssertTitleSelectionRefresh(fixture.ReadPackets().Skip(before), 5009);
        fixture.Registry.ConfigureCharacterTitleSelections(store);
        foreach (var malformed in new[]
        {
            CreateTitleSelectionPacket(5013, actualLength: 4),
            CreateTitleSelectionPacket(5013, actualLength: 7),
            CreateTitleSelectionPacket(5013, actualLength: 9),
            CreateTitleSelectionPacket(5013, declaredLength: 7),
            CreateTitleSelectionPacket(5013, declaredLength: 9)
        })
        {
            Check.True(!TitleSelectionProtocol.TryRead(malformed, out _),
                "native title selection rejects truncated, oversized, and inconsistent framing");
            await InvokeAsync(fixture.Handler, malformed);
        }
        Check.Equal(0, store.Requests.Count, "malformed native frames never reach the title store");
        foreach (var title in new uint[] { 1, uint.MaxValue })
        {
            before = fixture.ReadPackets().Count;
            await InvokeAsync(fixture.Handler, CreateTitleSelectionPacket(title));
            AssertTitleSelectionRefresh(fixture.ReadPackets().Skip(before), 5009);
        }
        store.Override = (_, _) => throw new InvalidOperationException("simulated unavailable title store");
        before = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateTitleSelectionPacket(0));
        AssertTitleSelectionRefresh(fixture.ReadPackets().Skip(before), 5009);
        Check.True(fixture.Character.SelectedTitleId == 5009 &&
            fixture.Character.MedusaRewardRevision == 20 && fixture.Character.MedusaHonorPoints == 1234 &&
            !fixture.Session.IsDisconnected,
            "unowned titles and transient store failure preserve all authoritative title state");
    }

    private static GamePacket CreateTitleSelectionPacket(uint titleId,
        int actualLength = 8, ushort declaredLength = 8)
    {
        var bytes = new byte[actualLength];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, declaredLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.DesignationSelection);
        if (actualLength >= 8) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), titleId);
        return new GamePacket(bytes);
    }

    private static bool IsTitleDisplayPacket(byte[] packet) => ReadOpcode(packet) == 10199;

    private static bool IsTitleSelectionPacket(byte[] packet) =>
        ReadOpcode(packet) is 10199 or Opcodes.DesignationInfo;

    private static uint ReadSelectedDisplay(byte[] packet)
    {
        Check.Equal(80, packet.Length, "native 10199 title display retains its full stock layout");
        return BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(76));
    }

    private static void AssertTitleSelectionRefresh(IEnumerable<byte[]> source, uint expectedTitle)
    {
        var packets = source.Where(IsTitleSelectionPacket).ToArray();
        Check.True(packets.Length == 2 && ReadOpcode(packets[0]) == Opcodes.DesignationInfo &&
            IsTitleDisplayPacket(packets[1]) &&
            BinaryPrimitives.ReadUInt32LittleEndian(packets[0].AsSpan(4)) == expectedTitle &&
            ReadSelectedDisplay(packets[1]) == expectedTitle,
            "the requester receives authoritative 10196 ownership/selection followed by 10199 display");
    }

    private static ScriptedTitleSelectionStore SeedTitleSelection(InstanceCallerFixture fixture)
    {
        fixture.Character.OwnedTitleIds = [5009, 5013, 5014, 5152];
        fixture.Character.SelectedTitleId = 5009;
        fixture.Character.MedusaHonorPoints = 1234;
        fixture.Character.MedusaRewardRevision = 20;
        return new ScriptedTitleSelectionStore();
    }

    private sealed class ScriptedTitleSelectionStore : ICharacterTitleSelectionStore
    {
        private uint _selected = 5009;
        private long _revision = 20;
        private readonly uint[] _owned = [5009, 5013, 5014, 5152];
        public List<CharacterTitleSelectionRequest> Requests { get; } = [];
        public Func<CharacterTitleSelectionRequest, CancellationToken,
            Task<CharacterTitleSelectionReceipt>>? Override { get; set; }

        public Task<CharacterTitleSelectionReceipt> SelectAsync(CharacterTitleSelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Override is not null) return Override(request, cancellationToken);
            if (request.TitleId != 0 && !_owned.Contains(request.TitleId))
                return Task.FromResult(new CharacterTitleSelectionReceipt(
                    CharacterTitleSelectionStatus.TitleNotOwned, 0, 0, 0, []));
            var status = _selected == request.TitleId
                ? CharacterTitleSelectionStatus.Unchanged : CharacterTitleSelectionStatus.Applied;
            if (status == CharacterTitleSelectionStatus.Applied) _revision++;
            _selected = request.TitleId;
            return Task.FromResult(new CharacterTitleSelectionReceipt(status, _selected, 1234, _revision, _owned));
        }
    }
}
