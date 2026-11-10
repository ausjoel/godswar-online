using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterHandlerChecks
{
    private static async Task<Fixture> CreateFixtureAsync(
        Endpoint endpoint,
        bool captured = false,
        byte camp = GameDefaults.SpartaCamp)
    {
        var character = new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "DuelArenaHandlerHero",
            CreatedUtc = new DateTime(
                2026,
                9,
                1,
                0,
                0,
                0,
                DateTimeKind.Utc),
            Camp = camp,
            CurrentMap = DuelArenaTransporterProtocol.MapId,
            PositionX = endpoint.Spawn.X,
            PositionZ = endpoint.Spawn.Z,
            Level = 140,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
        var endpoints = Endpoints();
        var spawns = captured
            ? NpcContentBaselineV7.LoadDefinitions().Where(static npc =>
                npc.MapId == DuelArenaCapturedLayout.MapId).ToArray()
            : endpoints.Select(static candidate => candidate.Spawn).ToArray();
        var npcKeys = spawns
            .Select(static candidate => candidate.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var routes = (captured ? NpcDialogueBaselineV21.CreateRoutes()
                : NpcDialogueBaselineV19.CreateRoutes())
            .Where(candidate => npcKeys.Contains(candidate.NpcKey))
            .ToArray();
        var texts = NpcTemplateSeeds.Texts
            .Where(candidate => npcKeys.Contains(candidate.NpcKey))
            .Select(static candidate => new NpcTextDefinition(
                candidate.NpcKey,
                candidate.SceneKey,
                candidate.DisplayName,
                candidate.Description))
            .ToArray();
        if (captured)
        {
            texts = NpcDialogueBaselineV20.ApplyTextOverrides(
                NpcTemplateSeeds.Texts.Select(static text => new NpcTextDefinition(
                    text.NpcKey, text.SceneKey, text.DisplayName, text.Description))
                    .ToArray()).Where(text => npcKeys.Contains(text.NpcKey)).ToArray();
        }
        var worldContent = PinnedWorldContentReader.Create(
            "duel-arena-handler-v1",
            captured ? [0, 1, DuelArenaTransporterProtocol.MapId] : [DuelArenaTransporterProtocol.MapId],
            spawns,
            [],
            [],
            new DateTimeOffset(
                2026,
                9,
                1,
                0,
                0,
                0,
                TimeSpan.Zero),
            npcTexts: texts,
            npcDialogueRoutes: routes,
            gameplay: GameplayContentTestFixtures.Published);
        var store = new ArenaStore();
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            AccountId,
            character);
        registry.JoinMap(
            session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(CharacterId),
            worldReady: true);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            worldContent,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "duel-arena-handler-check"));
        SetHandlerField(handler, "_character", character);
        SetHandlerField(handler, "_registered", true);
        SetHandlerField(handler, "_worldPresenceAnnounced", true);

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            DuelArenaTransporterProtocol.MapId,
            spawns,
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Arena NPC visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "Arena NPC visibility calculates");
        visibility.Commit(delta);

        return new Fixture(
            session,
            transport,
            handler,
            registry,
            store,
            character);
    }

}
