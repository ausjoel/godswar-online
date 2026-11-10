using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckWonderlandQueryClearAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(3, monsters, players);
        Check.True(SkillStatusEffectCatalog.TryGet(340, out var zeal), "Sacred Zeal test source is available");
        await f.Registry.ApplyRuntimeStatusAndPublishAsync(f.Session, zeal, f.Now, "wonderland-query-baseline",
            CancellationToken.None);
        await f.IncomingAsync("petbird");
        var queried = await f.Registry.SendStatusSnapshotToSelfAsync(f.Session, f.Now, CancellationToken.None,
            "wonderland-before-first-status-pump");
        await f.FlushAsync();
        Check.True(queried?.Effects.Any(x => x.StatusId == WonderlandClientStatusIds.PetbirdBlessing) == true,
            "a normal self query exposes the effect before the first status pump");
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15, zeal.StatusId);
        f.Registry.ClearWonderlandPlayerEffects(f.Session);
        Check.Equal(1, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "a query-exposed icon is cleared even if the publisher cache still contains the original baseline");
        await f.FlushAsync();
        Check.True(StatusEntries(f).Select(x => x.Id).SequenceEqual([zeal.StatusId]),
            "query-before-pump then island travel removes only the encounter icon");
        Check.Equal(0, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "the admitted clear releases tracking and is not repeated");

        await f.IncomingAsync("petbird");
        var clearedAfterAdmission = false;
        f.Registry.WonderlandStatusPublishedHook = () =>
        {
            clearedAfterAdmission = true;
            f.Registry.ClearWonderlandPlayerEffects(f.Session);
        };
        try
        {
            Check.Equal(1, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
                "an active snapshot can commit just before island departure clears its effect");
        }
        finally { f.Registry.WonderlandStatusPublishedHook = null; }
        await f.FlushAsync();
        Check.True(clearedAfterAdmission, "the test clears effects after successful full snapshot admission");
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15, zeal.StatusId);
        Check.Equal(1, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "late clear retains tracking because the last admitted packet still contained the icon");
        await f.FlushAsync();
        Check.True(StatusEntries(f).Select(x => x.Id).SequenceEqual([zeal.StatusId]),
            "the next pump clears the just-admitted icon while preserving Sacred Zeal");
    }

    private static async Task CheckWonderlandViewerStatusFenceAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(3, monsters, players);
        await f.IncomingAsync("petbird");
        var target = f.Runtime.Map.Snapshot().Single(x => x.Session == f.Session);
        var before = StatusPacketCount(f);
        await f.Registry.SendStatusSnapshotToViewerAsync(target, f.Session, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(before + 1, StatusPacketCount(f), "the normal viewer route can publish an active encounter icon");
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15);

        foreach (var expire in new[] { false, true })
        {
            await f.IncomingAsync("petbird");
            var hookCalled = false;
            f.Registry.WonderlandStatusProjectionCapturedHook = () =>
            {
                hookCalled = true;
                if (expire) f.Runtime.Map.AdvanceWonderland(f.Now.AddSeconds(15));
                else f.Registry.ClearWonderlandPlayerEffects(f.Session);
            };
            before = StatusPacketCount(f);
            try
            {
                await f.Registry.SendStatusSnapshotToViewerAsync(target, f.Session, CancellationToken.None);
            }
            finally { f.Registry.WonderlandStatusProjectionCapturedHook = null; }
            await f.FlushAsync();
            Check.True(hookCalled && before == StatusPacketCount(f),
                $"viewer admission rejects an effect {(expire ? "expired" : "cleared")} after its envelope was captured");
            Check.True(!f.Session.IsDisconnected, "a superseded status snapshot does not disconnect its current viewer");
        }

        var unroutedTransport = new FactionCrierCaptureTransport();
        await using var unrouted = new ClientSession(unroutedTransport);
        await f.Registry.SendStatusSnapshotToViewerAsync(target, unrouted, CancellationToken.None);
        Check.Equal(0, unroutedTransport.ReadLegacyPackets().Count,
            "an unavailable Wonderland membership route never falls back to an unfenced status packet");
    }

    private static int StatusPacketCount(Fixture f) => f.Transport.ReadLegacyPackets().Count(packet =>
        packet.Length == 340 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10167);
}
