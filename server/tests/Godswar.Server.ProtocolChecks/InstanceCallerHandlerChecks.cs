using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string CheckName =
        "Instance Caller expiring destination page context";

    public const string DuplicateEntryCheckName =
        "Instance member confirmation that must queue exactly once";

    private static readonly MethodInfo HandlePacketMethod =
        FindHandlerMethod("HandlePacketAsync");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        FindHandlerMethod("InstallNpcCatalog");

    public static async Task RunAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            level: 90,
            transitionReady: true);
        var sourceInstanceId = GetSourceInstanceId(fixture);

        await InvokeAsync(fixture.Handler, CreateActionPacket(-1));
        Check.True(
            fixture.ReadPackets().Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol.InitialMenuSubIds.ToArray())) &&
            GetPageContext(fixture.Handler) is null,
            "initial request advertises Medusa, Atlantis, and Wonderland " +
            "without granting page proof");

        await OpenMedusaPageAsync(fixture);
        var context = GetPageContext(fixture.Handler) ??
            throw new InvalidOperationException(
                "Medusa page context was not issued.");
        Check.True(
            context.AccountId == fixture.Character.AccountId &&
            context.CharacterId == fixture.Character.Id &&
            context.NpcKey == "Athens_060" &&
            context.NpcInteractionId == InstanceCallerProtocol.AthensNpcId &&
            context.RootSubId == InstanceCallerProtocol.MedusaRootSubId &&
            context.SourceWorldInstanceId == sourceInstanceId &&
            context.PageNonce != Guid.Empty &&
            context.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1),
            "page proof binds account, character, NPC, and world instance");
        SetHandlerField<InstanceCallerPageContext?>(
            fixture.Handler,
            "_instanceCallerPageContext",
            null);

        await CheckLegacyPageNavigationAsync(fixture);
        await CheckForgedAndExpiredChoicesAsync(fixture);
        await CheckCanonicalShapeAndWorldBindingAsync(fixture);
        await CheckSuccessfulSoloEntryAsync();
        await CheckLiveIslandSceneTransitionsAsync();
        await CheckCompletionCountdownAndLeaderTerminateAsync();
        await CheckSuccessfulMythicSoloEntryAsync();
        await CheckSuccessfulPartyEntryAsync();
        await CheckAuthoritativeDynamicDungeonTransitionsAsync();
        await CheckDecliningMemberLeavesLeaderInsideAsync();
        await CheckTimedOutMemberLeavesLeaderInsideAsync();
        await CheckDailyEntryEligibilityAsync();
        await CheckAtlantisPartyAdmissionAsync();
        await CheckAtlantisOpalRetryAsync();
    }

    /// <summary>
    /// The duplicate-entry regression on its own, so the behaviour has a
    /// registered check rather than riding on the page-context fixture.
    /// </summary>
    public static async Task RunDuplicateEntryAsync() =>
        await CheckRepeatedConfirmationQueuesOnceAsync();

    private static async Task CheckSuccessfulSoloEntryAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            level: 90,
            transitionReady: true);
        var sourceInstanceId = GetSourceInstanceId(fixture);
        await OpenMedusaPageAsync(fixture);
        var before = fixture.ReadPackets().Count;

        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));

        var emitted = ReadAdmissionPackets(fixture.ReadPackets().Skip(before));
        var targetInstanceId = GetSourceInstanceId(fixture);
        Check.True(
            emitted is [var scene] &&
            scene.SequenceEqual(PacketBuilder.SceneChange(
                0x1448,
                212f,
                0f,
                -217f,
                200)) &&
            fixture.Character.CurrentMap == 200 &&
            targetInstanceId != sourceInstanceId &&
            fixture.Registry.TryGetWorldInstance(
                targetInstanceId,
                out var descriptor) &&
            descriptor.MapId.Value == 200 &&
            descriptor.Kind == InstanceKind.Dungeon &&
            descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Active,
            "the eligible solo leader immediately enters one prepared " +
            "Enhanced dungeon");
    }

    private static async Task CheckSuccessfulMythicSoloEntryAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            level: 90,
            transitionReady: true);
        var sourceInstanceId = GetSourceInstanceId(fixture);
        await OpenMedusaPageAsync(fixture);
        var before = fixture.ReadPackets().Count;

        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.MythicDifficultySubId));

        var emitted = ReadAdmissionPackets(fixture.ReadPackets().Skip(before));
        var targetInstanceId = GetSourceInstanceId(fixture);
        Check.True(
            emitted is [var scene] &&
            scene.SequenceEqual(PacketBuilder.SceneChange(
                0x1448,
                212f,
                0f,
                -217f,
                200)) &&
            fixture.Character.CurrentMap == 200 &&
            targetInstanceId != sourceInstanceId &&
            fixture.Registry.TryGetWorldInstance(
                targetInstanceId,
                out var descriptor) &&
            descriptor.MapId.Value == 200 &&
            descriptor.Kind == InstanceKind.Dungeon &&
            descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Active,
            "the eligible solo leader enters one prepared Mythic dungeon");
    }

    private static async Task CheckForgedAndExpiredChoicesAsync(
        InstanceCallerFixture fixture)
    {
        var beforeForged = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.NormalDifficultySubId));
        Check.True(
            fixture.ReadPackets().Count == beforeForged &&
            GetPageContext(fixture.Handler) is null,
            "direct difficulty choice without page proof fails closed");

        await OpenMedusaPageAsync(fixture);
        SetPageContext(
            fixture.Handler,
            GetPageContext(fixture.Handler)! with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            });
        var beforeExpired = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.NormalDifficultySubId));
        Check.True(
            fixture.ReadPackets().Count == beforeExpired &&
            GetPageContext(fixture.Handler) is null,
            "expired page proof is consumed and rejected");

        await OpenMedusaPageAsync(fixture);
        SetPageContext(
            fixture.Handler,
            GetPageContext(fixture.Handler)! with { AccountId = 999 });
        var beforeWrongAccount = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));
        Check.True(
            fixture.ReadPackets().Count == beforeWrongAccount &&
            GetPageContext(fixture.Handler) is null,
            "account-mismatched page proof is rejected");
    }

    private static async Task CheckLegacyPageNavigationAsync(
        InstanceCallerFixture fixture)
    {
        var beforeAtlantis = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(InstanceCallerProtocol.AtlantisRootSubId));
        var atlantisContext = GetPageContext(fixture.Handler);
        Check.True(
            fixture.ReadPackets()
                .Skip(beforeAtlantis)
                .Single()
                .SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol.AtlantisPageSubIds.ToArray())) &&
            atlantisContext?.RootSubId ==
                InstanceCallerProtocol.AtlantisRootSubId,
            "Atlantis root emits 208/209/210 and binds Atlantis page proof");

        var beforeAtlantisEntry = fixture.ReadPackets().Count;
        var originalLevel = fixture.Character.Level;
        fixture.Character.Level = 89;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerProtocol.AtlantisEnterSubId));
        fixture.Character.Level = originalLevel;
        Check.True(
            fixture.ReadPackets()
                .Skip(beforeAtlantisEntry)
                .Single()
                .SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol.AtlantisLevelResultSubId)) &&
            GetPageContext(fixture.Handler) is null,
            "proved solo Atlantis entry reaches the unchanged level admission " +
            "gate and consumes its page proof");

        var beforeForgedWonderland = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.WonderlandRootSubId,
                InstanceCallerProtocol.WonderlandEnterSubId));
        Check.True(
            fixture.ReadPackets().Count == beforeForgedWonderland &&
            GetPageContext(fixture.Handler) is null,
            "Wonderland entry without Wonderland page proof fails closed");

        var beforeWonderland = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(InstanceCallerProtocol.WonderlandRootSubId));
        var wonderlandContext = GetPageContext(fixture.Handler);
        Check.True(
            fixture.ReadPackets()
                .Skip(beforeWonderland)
                .Single()
                .SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol.WonderlandPageSubIds.ToArray())) &&
            wonderlandContext?.RootSubId ==
                InstanceCallerProtocol.WonderlandRootSubId,
            "Wonderland root emits 211 and binds Wonderland page proof");
        SetHandlerField<InstanceCallerPageContext?>(
            fixture.Handler,
            "_instanceCallerPageContext",
            null);
    }

    private static async Task CheckCanonicalShapeAndWorldBindingAsync(
        InstanceCallerFixture fixture)
    {
        await OpenMedusaPageAsync(fixture);
        var malformed = CreateActionPacket(
            InstanceCallerProtocol.MedusaRootSubId,
            InstanceCallerProtocol.NormalDifficultySubId,
            duplicateDialogIndex: 10);
        var beforeMalformed = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, malformed);
        Check.True(
            fixture.ReadPackets().Count == beforeMalformed &&
            GetPageContext(fixture.Handler) is not null,
            "difficulty requires the canonical duplicated dialog field");

        await CheckNonCanonicalLengthMatrixAsync(fixture);

        SetPageContext(
            fixture.Handler,
            GetPageContext(fixture.Handler)! with
            {
                SourceWorldInstanceId = WorldInstanceId.New()
            });
        var beforeWrongWorld = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.NormalDifficultySubId));
        Check.True(
            fixture.ReadPackets().Count == beforeWrongWorld &&
            GetPageContext(fixture.Handler) is null,
            "source-world mismatch consumes and rejects the page proof");
    }

    private static async Task CheckNonCanonicalLengthMatrixAsync(
        InstanceCallerFixture fixture)
    {
        var malformedLengths = new[]
        {
            (Declared: 91, Buffer: 92),
            (Declared: 92, Buffer: 91),
            (Declared: 92, Buffer: 93),
            (Declared: 93, Buffer: 93)
        };
        foreach (var (declared, buffer) in malformedLengths)
        {
            var before = fixture.ReadPackets().Count;
            await InvokeAsync(
                fixture.Handler,
                CreateActionPacket(
                    InstanceCallerProtocol.MedusaRootSubId,
                    InstanceCallerProtocol.NormalDifficultySubId,
                    declaredLength: declared,
                    bufferLength: buffer));
            Check.True(
                fixture.ReadPackets().Count == before &&
                GetPageContext(fixture.Handler) is not null,
                $"declared/buffer length {declared}/{buffer} fails closed");
        }
    }

    private static async Task OpenMedusaPageAsync(
        InstanceCallerFixture fixture)
    {
        var before = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(InstanceCallerProtocol.MedusaRootSubId));
        Check.True(
            fixture.ReadPackets().Skip(before).Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol.MedusaPageSubIds.ToArray())),
            "root choice returns description, Advanced, Normal, and Mythic");
    }

}
