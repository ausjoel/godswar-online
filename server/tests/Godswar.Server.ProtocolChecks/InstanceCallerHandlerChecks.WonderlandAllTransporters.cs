using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandAllTransportersCheckName =
        "Wonderland all eight island transporters are visible clickable and preserve clear and settlement gates";

    public static async Task RunWonderlandAllTransportersAsync()
    {
        CheckLaterWonderlandTransportPlacement();
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var leader = fixture.Party.Leader;
        // The route is tested while optional mobs remain alive around the
        // treasure. Give this transport fixture enough HP for their real hits.
        leader.Character.MaxHp = 10_000_000;
        leader.Character.CurrentHp = leader.Character.MaxHp;
        leader.Registry.UpdateCharacter(leader.Session, leader.Character, advanceWorldRevision: false);
        for (var island = 1; island <= 7; island++)
        {
            var npc = WonderlandTraversalPolicy.GetTeleporter(island);
            // Approach the exact captured helper, independently of the chest AOI.
            var approach = new WonderlandPosition(npc.X, 0, npc.Z);
            await MoveToWonderlandTransportAsync(leader, npc.ObjectId, approach.X, approach.Z);
            Check.True(HasWonderlandNpcAppearance(leader.ReadPackets(), npc.ObjectId, npc.X, npc.Z),
                $"island {island} actually publishes the transporter to the client at its interaction area");
            var before = leader.ReadPackets().Count;
            await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(npc.ObjectId));
            Check.True(leader.ReadPackets().Skip(before).Single().SequenceEqual(
                    PacketBuilder.NpcDialogOpenAck(npc.ObjectId, 57, npc.NpcKey)),
                $"island {island} opens its native Teleport button without moving the player");
            await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(npc.ObjectId, 57));
            Check.True(leader.ReadPackets().Last().SequenceEqual(
                    PacketBuilder.CapturedNpcFunctionActionResponse(npc.ObjectId, 57, [0, 99 + island])) &&
                leader.ReadPackets().Skip(before).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
                $"island {island} refuses travel until its required monsters are defeated");

            if (island == 6)
            {
                var tick = await WonderlandCombatChecks.KillWonderlandDragonByBurnAsync(leader.Registry,
                    runtime, leader.Session, leader.Character, WonderlandNow(runtime));
                // Periodic checks use the authored future tick, whereas native
                // dialogue admission reads wall time. Synchronize once before
                // completing this fast route, so its final treasure window has
                // actually begun when the live handler evaluates the exit.
                var ahead = tick - DateTimeOffset.UtcNow;
                if (ahead > TimeSpan.Zero)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(ahead.TotalMilliseconds)));
                await leader.Registry.AdvanceMonsterWorldOnceAsync(tick.AddTicks(1), CancellationToken.None);
                Check.True(fixture.Titles.AppliedReceipts.Count(receipt => receipt.Award.IslandNumber == 6) == 1,
                    "the periodic dragon kill settles the sixth title exactly once before native travel");
            }
            else await ClearWonderlandHandlerIslandAsync(fixture, runtime);
            await MoveToWonderlandTransportAsync(leader, npc.ObjectId, approach.X, approach.Z);
            await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(npc.ObjectId));
            before = leader.ReadPackets().Count;
            await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(npc.ObjectId, 57));
            var next = WonderlandTerrainPolicy.GetIsland(island + 1).Entrance;
            Check.True(GetSourceInstanceId(leader) == runtime.InstanceId &&
                leader.Character.PositionX == next.X && leader.Character.PositionZ == next.Z &&
                leader.ReadPackets().Skip(before).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
                $"island {island}'s explicit action reaches the next island in the same exact dungeon");
            await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        }

        var final = WonderlandTraversalPolicy.GetTeleporter(8);
        await MoveToWonderlandTransportAsync(leader, final.ObjectId, final.X, final.Z);
        Check.True(HasWonderlandNpcAppearance(leader.ReadPackets(), final.ObjectId, final.X, final.Z),
            "the central eighth island publishes its installed Returning Helper beyond the native guard's area");
        var initialFinalPackets = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(final.ObjectId, 57));
        Check.Equal(initialFinalPackets, leader.ReadPackets().Count,
            "the final exit also requires its own one-use native dialog");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(final.ObjectId));
        Check.True(leader.ReadPackets().Last().SequenceEqual(
                PacketBuilder.NpcDialogOpenAck(final.ObjectId, 57, "Lelantine_Farm_005")),
            "the final exit uses the verified Returning Helper name and native Teleport action");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(final.ObjectId, 57));
        Check.True(leader.Character.CurrentMap == 207 && runtime.Map.TryGetWonderlandSnapshot(out var active) &&
            active.State == WonderlandRunState.Active,
            "the final helper cannot skip the eighth island or terminate the party's run");

        fixture.Titles.FailuresRemaining = 1;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var completed) && completed.State == WonderlandRunState.Completed &&
            leader.Registry.HasPendingWonderlandTitles(runtime.InstanceId),
            "final clear preserves a real pending durable reward when the store fails");
        await MoveToWonderlandTransportAsync(leader, final.ObjectId, final.X, final.Z);
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(final.ObjectId));
        var blockedAt = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(final.ObjectId, 57));
        Check.True(GetSourceInstanceId(leader) == runtime.InstanceId &&
            leader.ReadPackets().Skip(blockedAt).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "completion alone cannot bypass pending durable final-title settlement");
        await leader.Registry.RetryWonderlandTitlesAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(!leader.Registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
            fixture.Titles.AppliedReceipts.Any(receipt => receipt.Award.IslandNumber == 8),
            "the exact final milestone is durably settled before exit becomes available");
        await CheckWonderlandFinalExitLifeRacesAsync(leader, runtime.InstanceId);

        var life = leader.Registry.GetPlayerLifeRevision(leader.Session);
        Check.True(!leader.Registry.TryResolveWonderlandFinalExit(leader.Session, runtime.InstanceId, life - 1, out _),
            "the final service rejects a stale player life even after successful settlement");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(final.ObjectId));
        var leavingAt = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(final.ObjectId, 57));
        var capital = leader.Character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
        Check.True(leader.Character.CurrentMap == capital && GetSourceInstanceId(leader) != runtime.InstanceId &&
            leader.ReadPackets().Skip(leavingAt).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
            "the final helper performs one authoritative capital transfer for its clicking player");
        var follower = fixture.Party.Followers.Single();
        Check.True(leader.Registry.TryGetSessionWorldInstanceId(follower.Session, out var followerInstance) &&
            followerInstance == runtime.InstanceId && follower.Character.CurrentMap == 207 &&
            runtime.Map.TryGetWonderlandSnapshot(out var afterExit) && afterExit.State == WonderlandRunState.Completed &&
            afterExit.TerminalAt == completed.TerminalAt &&
            WonderlandCompletionPolicy.IsTreasureWindowOpen(afterExit, WonderlandNow(runtime)),
            "one member's early exit does not cancel the run or shorten the remaining party's treasure cooldown");
        var afterDeparture = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(final.ObjectId, 57));
        Check.True(leader.ReadPackets().Skip(afterDeparture).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "replaying the consumed final dialogue cannot cause another transfer");
    }

    private static void CheckLaterWonderlandTransportPlacement()
    {
        var npcs = WonderlandTraversalPolicy.AddTeleporters([]);
        Check.True(WonderlandTraversalPolicy.AddTeleporters(npcs).SequenceEqual(npcs),
            "rebuilding the complete native transporter roster is idempotent");
        var upper = WonderlandTraversalPolicy.GetTeleporter(3);
        var third = WonderlandTerrainPolicy.GetIsland(3);
        Check.True(upper.X == -153.5f && upper.Z == -100.3f && upper.Z > third.Center.Z &&
            third.Exit.X == upper.X && third.Exit.Z == upper.Z &&
            !WonderlandTerrainPolicy.IsCombatArea(3, upper.X, upper.Z) &&
            WonderlandTraversalPolicy.TryGetIsland(upper, out var upperIsland) && upperIsland == 3,
            "the third teleporter matches the complete capture's upper landing and combat safe zone");
        var expected = new (float X, float Z)[]
            { (-153.5f, -100.3f), (-159.5f, 94.3f), (-76.5f, 146.3f), (167.5f, 122.3f), (214.5f, 39.3f), (0, 80) };
        for (var island = 3; island <= 8; island++)
        {
            var npc = WonderlandTraversalPolicy.GetTeleporter(island);
            var geometry = WonderlandTerrainPolicy.GetIsland(island);
            Check.True((npc.X, npc.Z) == expected[island - 3] &&
                geometry.Exit.X == npc.X && geometry.Exit.Z == npc.Z &&
                geometry.Bounds.Contains(npc.X, npc.Z) &&
                !WonderlandTerrainPolicy.IsCombatArea(island, npc.X, npc.Z) &&
                WonderlandTraversalPolicy.TryGetIsland(npc, out var found) && found == island,
                $"island {island}'s transporter uses its captured point or separate final helper and matching safe zone");
            Check.True(!WonderlandTraversalPolicy.TryGetIsland(npc with { X = npc.X + 1 }, out _) &&
                !WonderlandTraversalPolicy.TryGetIsland(npc with { InteractionId = npc.ObjectId + 1 }, out _),
                "altered coordinate or interaction identity cannot impersonate an authored transporter");
        }
        var exit = WonderlandTraversalPolicy.GetTeleporter(8);
        Check.True(exit.ObjectId == 5720 && exit.NpcKey == "Lelantine_Farm_005" &&
            exit.TemplateKey == "Lelantine_Farm_005_WarField1" && exit.SceneKey == "Fane" &&
            !WonderlandTreasureChestPolicy.TryGetIsland(exit, out _),
            "the final native Returning Helper keeps a distinct map207 identity outside all eight treasure objects");
    }
}
