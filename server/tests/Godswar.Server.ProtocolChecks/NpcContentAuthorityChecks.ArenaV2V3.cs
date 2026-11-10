using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class NpcContentAuthorityChecks
{
    private static void CheckArenaV2Release()
    {
        var definitions = NpcContentBaselineV2.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.Equal(
            NpcContentBaselineV2.ExpectedEntryCount,
            definitions.Length,
            "Arena V2 NPC entry count");
        Check.Equal(
            NpcContentBaselineV2.ExpectedRevision,
            revision.Sha256,
            "Arena V2 NPC golden revision");

        var arena = definitions
            .Where(static definition =>
                definition.MapId ==
                    DuelArenaTransporterProtocol.MapId)
            .OrderBy(static definition => definition.NpcKey)
            .ToArray();
        Check.Equal(2, arena.Length, "Arena V2 paired spawn count");
        Check.True(
            arena.Select(static definition => definition.NpcKey)
                .SequenceEqual(new[] { "Arena_002", "Arena_003" }),
            "Arena V2 uses the client-native Doorkeeper and Gatekeeper");
        Check.True(
            arena.All(static definition =>
                definition.ObjectId == definition.InteractionId &&
                definition.AppearanceType ==
                    NpcAppearanceDefaults.AppearanceType),
            "Arena V2 spawn identities are stable and renderable");
    }

    private static void CheckArenaV15DialogueRelease()
    {
        var publishedNpcKeys = NpcContentBaselineV2.LoadDefinitions()
            .Select(static definition => definition.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var rawTexts = NpcTemplateSeeds.Texts
            .Where(text => publishedNpcKeys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(
                text.NpcKey,
                text.SceneKey,
                text.DisplayName,
                text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var texts = NpcDialogueBaselineV15.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV15.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);

        Check.Equal(
            NpcDialogueBaselineV15.ExpectedTextCount,
            texts.Length,
            "Arena V15 dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV15.ExpectedHashedEntryCount,
            revision.EntryCount,
            "Arena V15 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV15.ExpectedRevision,
            revision.Sha256,
            "Arena V15 dialogue golden revision");
    }

    private static void CheckArenaV3Release()
    {
        var previous = NpcContentBaselineV2.LoadDefinitions();
        var definitions = NpcContentBaselineV3.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.Equal(
            NpcContentBaselineV3.ExpectedEntryCount,
            definitions.Length,
            "Arena V3 NPC entry count");
        Check.Equal(
            NpcContentBaselineV3.ExpectedRevision,
            revision.Sha256,
            "Arena V3 NPC golden revision");
        Check.Equal(
            previous.Length,
            definitions.Length,
            "Arena V3 retains the complete V2 identity set");

        var previousByIdentity = previous.ToDictionary(static definition => (
            definition.MapId,
            definition.NpcKey,
            definition.TemplateKey,
            definition.ObjectId));
        Check.Equal(
            previousByIdentity.Count,
            definitions.Select(static definition => (
                    definition.MapId,
                    definition.NpcKey,
                    definition.TemplateKey,
                    definition.ObjectId))
                .Distinct()
                .Count(),
            "Arena V3 retains each V2 identity exactly once");
        var changedPositions = 0;
        foreach (var definition in definitions)
        {
            Check.True(
                previousByIdentity.TryGetValue(
                    (definition.MapId,
                        definition.NpcKey,
                        definition.TemplateKey,
                        definition.ObjectId),
                    out var prior),
                $"Arena V3 retains identity {definition.NpcKey}");
            CheckDefinitionEqual(
                prior!,
                definition with { X = prior!.X, Z = prior.Z },
                $"Arena V3 preserves {definition.NpcKey} outside position");

            var gatekeeper =
                definition.MapId == DuelArenaTransporterProtocol.MapId &&
                definition.ObjectId ==
                    DuelArenaTransporterProtocol.LegacyGatekeeperNpcId;
            var doorkeeper =
                definition.MapId == DuelArenaTransporterProtocol.MapId &&
                definition.ObjectId ==
                    DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId;
            if (gatekeeper)
            {
                Check.True(
                    prior.X == -64f && prior.Z == 92f &&
                    definition.X ==
                        DuelArenaTransporterProtocol.GatekeeperSpawnX &&
                    definition.Z ==
                        DuelArenaTransporterProtocol.GatekeeperSpawnZ &&
                    definition.X == -92f && definition.Z == 92f,
                    "Arena V3 changes only the Gatekeeper X/Z to (-92,92)");
                changedPositions++;
            }
            else if (doorkeeper)
            {
                Check.True(
                    prior.X == -13f && prior.Z == 60f &&
                    definition.X ==
                        DuelArenaTransporterProtocol.DoorkeeperSpawnX &&
                    definition.Z ==
                        DuelArenaTransporterProtocol.DoorkeeperSpawnZ &&
                    definition.X == -7f && definition.Z == 28f,
                    "Arena V3 changes only the Doorkeeper X/Z to (-7,28)");
                changedPositions++;
            }
            else
            {
                Check.True(
                    definition.X == prior.X && definition.Z == prior.Z,
                    $"Arena V3 leaves {definition.NpcKey} position frozen");
            }
        }
        Check.Equal(
            2,
            changedPositions,
            "Arena V3 changes exactly the two transporter positions");
    }

    private static void CheckArenaV16DialogueRelease()
    {
        var publishedNpcKeys = NpcContentBaselineV3.LoadDefinitions()
            .Select(static definition => definition.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var rawTexts = NpcTemplateSeeds.Texts
            .Where(text => publishedNpcKeys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(
                text.NpcKey,
                text.SceneKey,
                text.DisplayName,
                text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var texts = NpcDialogueBaselineV16.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV16.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);

        Check.True(
            NpcDialogueBaselineV16.ExpectedSpawnRevision ==
                NpcContentBaselineV3.ExpectedRevision,
            "Arena V16 dialogue pins the V3 spawn revision");
        Check.Equal(
            NpcDialogueBaselineV16.ExpectedTextCount,
            texts.Length,
            "Arena V16 dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV16.ExpectedHashedEntryCount,
            revision.EntryCount,
            "Arena V16 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV16.ExpectedRevision,
            revision.Sha256,
            "Arena V16 dialogue golden revision");
    }
}
