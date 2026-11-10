using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// INSTANCES (entries-off): Bay Under Attack and the Training Ground pages are
/// refused while their server_rates switches are off, with no page context, no
/// daily-entry claim, no countdown and no teleport; Medusa, Atlantis and
/// Wonderland pages answer exactly as before.
/// </summary>
internal static partial class InstanceCallerHandlerChecks
{
    public const string ClosedInstancesCheckName =
        "Instance Caller closed-instance switches (Bay Under Attack, Training Ground)";

    public static async Task RunClosedInstancesAsync()
    {
        CheckClosedInstanceResolution();
        await CheckClosedInstancePagesAsync();
    }

    private static void CheckClosedInstanceResolution()
    {
        var path = Enumerable.Repeat(-1, InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        int[] Submit(int page)
        {
            var arguments = (int[])path.Clone();
            arguments[0] = page;
            return arguments;
        }

        InstanceCallerProtocol.ClosedInstance Resolve(int subId, int[] arguments, bool harbor, bool training) =>
            InstanceCallerProtocol.ResolveClosedInstance(
                InstanceCallerProtocol.DialogIndex, subId, arguments, harbor, training);

        Check.True(
            Resolve(InstanceCallerProtocol.HarborAttackRootSubId, path, false, false) ==
                InstanceCallerProtocol.ClosedInstance.HarborAttack &&
            Resolve(InstanceCallerProtocol.HarborAttackRootSubId,
                Submit(InstanceCallerProtocol.HarborAttackPageSubId), false, false) ==
                InstanceCallerProtocol.ClosedInstance.HarborAttack &&
            Resolve(InstanceCallerProtocol.HeraclesTrialRootSubId, path, false, false) ==
                InstanceCallerProtocol.ClosedInstance.TrainingGround &&
            // WARRIOR (F-51): root 13 now follows its own switch.
            Resolve(InstanceCallerProtocol.WarriorTrialRootSubId, path, false, false) ==
                InstanceCallerProtocol.ClosedInstance.WarriorTraining &&
            Resolve(InstanceCallerProtocol.HeraclesTrialRootSubId,
                Submit(InstanceCallerProtocol.HeraclesTrialPageSubId), false, false) ==
                InstanceCallerProtocol.ClosedInstance.TrainingGround,
            "closed switches catch the harbour and training roots and their page submissions");
        Check.True(
            Resolve(InstanceCallerProtocol.HarborAttackRootSubId, path, true, true) ==
                InstanceCallerProtocol.ClosedInstance.None &&
            Resolve(InstanceCallerProtocol.HeraclesTrialRootSubId,
                Submit(InstanceCallerProtocol.HeraclesTrialPageSubId), true, true) ==
                InstanceCallerProtocol.ClosedInstance.None &&
            Resolve(InstanceCallerProtocol.HarborAttackRootSubId, path, true, false) ==
                InstanceCallerProtocol.ClosedInstance.None &&
            Resolve(InstanceCallerProtocol.HeraclesTrialRootSubId, path, true, false) ==
                InstanceCallerProtocol.ClosedInstance.TrainingGround,
            "open switches pass the pages through, and each switch only closes its own instance");
        foreach (var other in new[]
                 {
                     (InstanceCallerProtocol.MedusaRootSubId, path),
                     (InstanceCallerProtocol.MedusaRootSubId, Submit(InstanceCallerProtocol.NormalDifficultySubId)),
                     (InstanceCallerProtocol.AtlantisRootSubId, path),
                     (InstanceCallerProtocol.AtlantisRootSubId, Submit(InstanceCallerProtocol.AtlantisEnterSubId)),
                     (InstanceCallerProtocol.WonderlandRootSubId, path),
                     (InstanceCallerProtocol.WonderlandRootSubId, Submit(InstanceCallerProtocol.WonderlandEnterSubId)),
                     (InstanceCallerProtocol.InitialRequestSubId, path)
                 })
        {
            Check.True(
                Resolve(other.Item1, other.Item2, false, false) ==
                    InstanceCallerProtocol.ClosedInstance.None,
                $"closed switches never touch root {other.Item1} / page {other.Item2[0]}");
        }
        Check.True(
            InstanceCallerProtocol.ResolveClosedInstance(
                InstanceCallerProtocol.DialogIndex + 1,
                InstanceCallerProtocol.HarborAttackRootSubId, path, false, false) ==
                InstanceCallerProtocol.ClosedInstance.None,
            "another dialog index is never treated as an Instance Caller page");
    }

    private static async Task CheckClosedInstancePagesAsync()
    {
        using var closed = InstanceEntrySwitchScope.Set(false, false);
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            null,
            partySize: 4);
        foreach (var character in fixture.Characters)
        {
            character.Level = 95;
        }
        var leader = fixture.Leader;
        var source = GetSourceInstanceId(leader);
        var sourceMap = leader.Character.CurrentMap;
        var refusal = PacketBuilder.NpcFunctionActionResponse(
            InstanceCallerProtocol.AthensNpcId,
            InstanceCallerProtocol.DialogIndex,
            InstanceCallerProtocol.ClosedInstanceResultSubId);

        async Task<IReadOnlyList<byte[]>> SendAsync(GamePacket packet)
        {
            var before = leader.Transport.ReadLegacyPackets().Count;
            await InvokeAsync(leader.Handler, packet, confirmEntry: true);
            return leader.Transport.ReadLegacyPackets().Skip(before).ToArray();
        }

        bool IsRefusal(IReadOnlyList<byte[]> emitted) =>
            emitted.Count == 2 &&
            emitted[0].SequenceEqual(refusal) &&
            ReadOpcode(emitted[1]) == Opcodes.PythonNote;

        var harborRoot = await SendAsync(CreateActionPacket(
            InstanceCallerProtocol.HarborAttackRootSubId));
        var harborEntry = await SendAsync(CreateActionPacket(
            InstanceCallerProtocol.HarborAttackRootSubId,
            InstanceCallerProtocol.HarborAttackPageSubId));
        Check.True(
            IsRefusal(harborRoot) && IsRefusal(harborEntry),
            "closed Bay Under Attack answers its root and its page with the " +
            "client's own 'remaining today is 0' line plus one personal note");

        var trainingRoot = await SendAsync(CreateActionPacket(
            InstanceCallerProtocol.WarriorTrialRootSubId)); // WARRIOR (F-51): its own switch, also closed here
        var heraclesRoot = await SendAsync(CreateActionPacket(
            InstanceCallerProtocol.HeraclesTrialRootSubId));
        var heraclesEntry = await SendAsync(CreateActionPacket(
            InstanceCallerProtocol.HeraclesTrialRootSubId,
            InstanceCallerProtocol.HeraclesTrialPageSubId));
        Check.True(
            IsRefusal(trainingRoot) && IsRefusal(heraclesRoot) && IsRefusal(heraclesEntry),
            "closed Training Ground answers roots 13 and 17 and page 231 with the refusal");

        Check.True(
            daily.Claims.Count == 0 &&
            PendingEntrySceneId(leader.Handler) is null &&
            leader.Character.CurrentMap == sourceMap &&
            GetSourceInstanceId(leader) == source,
            "a closed instance claims no daily entry, opens no countdown and moves nobody");

        // Regression: the reviewed instances are untouched by the closed switches.
        var atlantisPage = await SendAsync(CreateActionPacket(
            InstanceCallerProtocol.AtlantisRootSubId));
        var medusaPage = await SendAsync(CreateActionPacket(
            InstanceCallerProtocol.MedusaRootSubId));
        Check.True(
            atlantisPage.Count == 1 &&
            atlantisPage[0].SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                InstanceCallerProtocol.AthensNpcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisPageSubIds.ToArray())) &&
            medusaPage.Count == 1 &&
            medusaPage[0].SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                InstanceCallerProtocol.AthensNpcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.MedusaPageSubIds.ToArray())),
            "Atlantis and Medusa pages answer exactly as before with the switches closed");
    }
}
