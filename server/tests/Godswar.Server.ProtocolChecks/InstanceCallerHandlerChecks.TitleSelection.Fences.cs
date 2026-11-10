using Godswar.Server.Application.Characters;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckDelayedTitleSelectionAsync()
    {
        await using var fixture = await CreateFixtureAsync(90, transitionReady: true);
        Check.True(fixture.Registry.TryMarkWorldReady(fixture.Session,
            new Dictionary<uint, long>(), out _), "delayed title fixture is world ready");
        var store = SeedTitleSelection(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CharacterTitleSelectionReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.Override = (_, _) => { entered.TrySetResult(); return release.Task; };
        fixture.Registry.ConfigureCharacterTitleSelections(store);
        var before = fixture.ReadPackets().Count;
        var request = InvokeAsync(fixture.Handler, CreateTitleSelectionPacket(5013));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // A newer durable selection/award is projected while this older
            // completed transaction's receipt remains held at the boundary.
            fixture.Character.SelectedTitleId = 5014;
            fixture.Character.MedusaHonorPoints = 4034;
            fixture.Character.MedusaRewardRevision = 22;
            release.TrySetResult(new CharacterTitleSelectionReceipt(
                CharacterTitleSelectionStatus.Applied, 5013, 1234, 21,
                [5009, 5013, 5014, 5101, 5152]));
            await request.WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(fixture.Character.SelectedTitleId == 5014 &&
                fixture.Character.MedusaHonorPoints == 4034 &&
                fixture.Character.MedusaRewardRevision == 22 &&
                fixture.Character.OwnedTitleIds.Contains(5101u),
                "an older receipt merges earned ownership without rolling back selection, wallet, or revision");
            AssertTitleSelectionRefresh(fixture.ReadPackets().Skip(before), 5014);
        }
        finally
        {
            release.TrySetCanceled();
        }
    }

    private static async Task CheckTitleSelectionOwnershipLossAsync()
    {
        foreach (var status in new[] { CharacterTitleSelectionStatus.Applied,
                     CharacterTitleSelectionStatus.OwnershipLost })
        {
            await using var fixture = await CreateFixtureAsync(90, transitionReady: true);
            Check.True(fixture.Registry.TryMarkWorldReady(fixture.Session,
                new Dictionary<uint, long>(), out _), "replaced title fixture is world ready");
            var store = SeedTitleSelection(fixture);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<CharacterTitleSelectionReceipt>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            store.Override = (_, _) => { entered.TrySetResult(); return release.Task; };
            fixture.Registry.ConfigureCharacterTitleSelections(store);
            var before = fixture.ReadPackets().Count;
            var request = InvokeAsync(fixture.Handler, CreateTitleSelectionPacket(5013));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var replacement = new ClientSession(new FactionCrierCaptureTransport());
            fixture.Registry.ReplaceAccountSession(fixture.Character.AccountId, replacement);
            try
            {
                release.TrySetResult(new CharacterTitleSelectionReceipt(status, 5013, 1234, 21,
                    [5009, 5013, 5014, 5152]));
                await request.WaitAsync(TimeSpan.FromSeconds(5));
                Check.True(fixture.Character.SelectedTitleId == 5009 &&
                    fixture.Character.MedusaRewardRevision == 20 &&
                    !fixture.Session.IsDisconnected && !replacement.IsDisconnected &&
                    !fixture.ReadPackets().Skip(before).Any(IsTitleSelectionPacket),
                    "a delayed receipt cannot mutate, publish, or disconnect after captured ownership is replaced");
            }
            finally
            {
                release.TrySetCanceled();
                GameHandlerOwnershipTestFences.Bind(fixture.Registry, fixture.Session,
                    fixture.Character.AccountId, fixture.Character);
            }
        }

        await using var current = await CreateFixtureAsync(90, transitionReady: true);
        Check.True(current.Registry.TryMarkWorldReady(current.Session,
            new Dictionary<uint, long>(), out _), "lost-owner title fixture is world ready");
        var rejected = SeedTitleSelection(current);
        rejected.Override = (_, _) => Task.FromResult(new CharacterTitleSelectionReceipt(
            CharacterTitleSelectionStatus.OwnershipLost, 0, 0, 0, []));
        current.Registry.ConfigureCharacterTitleSelections(rejected);
        await InvokeAsync(current.Handler, CreateTitleSelectionPacket(5013));
        Check.True(current.Session.IsDisconnected && current.Character.SelectedTitleId == 5009,
            "the still-current claimant is terminalized when the durable store rejects its ownership fence");
    }
}
