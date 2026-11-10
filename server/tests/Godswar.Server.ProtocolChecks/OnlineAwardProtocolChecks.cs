using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class OnlineAwardProtocolChecks
{
    public const string CheckName =
        "Online Award stock protocol and pinned dialogue";

    public static Task RunAsync()
    {
        Check.True(
            OnlineAwardProtocol.DialogIndex == 49 &&
            OnlineAwardProtocol.PublishedAthensNpcId == 5271 &&
            OnlineAwardProtocol.SpartaNpcId == 5129 &&
            OnlineAwardProtocol.InitialRequestSubId == -1 &&
            OnlineAwardProtocol.SuccessSubId == 102 &&
            OnlineAwardProtocol.AlreadyClaimedSubId == 103 &&
            OnlineAwardProtocol.BagFullSubId == 104 &&
            OnlineAwardProtocol.UnavailableSubId == 105,
            "Online Award stock endpoint and result contract");

        var spawns = NpcContentBaselineV1.LoadDefinitions();
        var spartaSpawn = spawns.Single(static definition =>
            definition.NpcKey == "Sparta_132");
        Check.True(
            spartaSpawn.InteractionId ==
                OnlineAwardProtocol.SpartaNpcId &&
            OnlineAwardProtocol.IsEndpoint(
                spartaSpawn.NpcKey,
                spartaSpawn.InteractionId) &&
            !OnlineAwardProtocol.IsEndpoint("Sparta_132", 5131),
            "Online Award uses frozen live Sparta interaction 5129 and rejects stale 5131");
        var spawnKeys = spawns
            .Select(static definition => definition.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var texts = NpcTemplateSeeds.Texts
            .Where(seed => spawnKeys.Contains(seed.NpcKey))
            .Select(static seed => new NpcTextDefinition(
                seed.NpcKey,
                seed.SceneKey,
                seed.DisplayName,
                seed.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var routes = NpcDialogueBaselineV7.CreateRoutes();
        var spartaRoute = routes.Single(static route =>
            route.NpcKey == "Sparta_132");
        Check.True(
            NpcDialogueBehaviorRegistry.IsAllowed(
                spartaSpawn,
                spartaRoute) &&
            !NpcDialogueBehaviorRegistry.IsAllowed(
                spartaSpawn with { InteractionId = 5131 },
                spartaRoute),
            "Online Award capability accepts live Sparta 5129 only");
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.Equal(
            NpcDialogueBaselineV7.ExpectedTextCount,
            texts.Length,
            "Online Award dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV7.ExpectedRouteCount,
            routes.Length,
            "Online Award dialogue route count");
        Check.Equal(
            NpcDialogueBaselineV7.ExpectedRevision,
            revision.Sha256,
            "Online Award dialogue golden revision");

        var reward = new OnlineAwardBalanceSnapshot(
            1,
            "A11516DCF5A5CAC3AAAC9CECEFF416C6510789F4386BBAF4C8D7CC9FB2B704FE",
            [
                new(0, 10150, 1, 14, 0, 1),
                new(1, 10150, 4, 10, 0, 1),
                new(2, 10134, 5, 1, 0, 99),
                new(3, 11005, 5, 1, 0, 99)
            ]);
        reward.Validate();
        Check.True(
            CommandMetrics.FamilyCode(CommandFamily.OnlineAward) ==
                "online_award" &&
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.OnlineAward) ==
                CommandIdentityStrength.ClientOperationId,
            "Online Award metrics and secure identity policy are total");
        Check.Equal(
            7,
            reward.Rewards.Sum(static value =>
                (value.Quantity + value.StackCap - 1) / value.StackCap),
            "empty-bag Online Award requires seven slots");
        return Task.CompletedTask;
    }
}
