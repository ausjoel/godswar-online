using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class NpcContentAuthorityChecks
{
    private static void CheckArenaV4Release()
    {
        var previous = NpcContentBaselineV3.LoadDefinitions();
        var definitions = NpcContentBaselineV4.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.Equal(
            NpcContentBaselineV4.ExpectedEntryCount,
            definitions.Length,
            "Arena V4 NPC entry count");
        Check.Equal(
            NpcContentBaselineV4.ExpectedRevision,
            revision.Sha256,
            "Arena V4 NPC golden revision");

        var previousIdentities = previous
            .Select(Identity)
            .ToHashSet();
        var currentByIdentity = definitions.ToDictionary(Identity);
        foreach (var prior in previous)
        {
            Check.True(
                currentByIdentity.TryGetValue(Identity(prior), out var current),
                $"Arena V4 retains V3 identity {prior.NpcKey}");
            CheckDefinitionEqual(
                prior,
                current!,
                $"Arena V4 preserves V3 definition {prior.NpcKey}");
        }
        Check.Equal(
            3,
            definitions.Count(definition =>
                !previousIdentities.Contains(Identity(definition))),
            "Arena V4 adds exactly three identities");

        var arena = definitions
            .Where(static definition =>
                definition.MapId == DuelArenaTransporterProtocol.MapId)
            .OrderBy(static definition => definition.NpcKey)
            .ToArray();
        Check.Equal(5, arena.Length, "Arena V4 complete native roster count");
        AssertArenaActor(
            arena[0],
            DuelArenaNpcRoster.VendorNpcKey,
            DuelArenaNpcRoster.VendorTemplateKey,
            DuelArenaNpcRoster.LegacyVendorNpcId,
            DuelArenaNpcRoster.VendorSpawnX,
            DuelArenaNpcRoster.VendorSpawnZ,
            DuelArenaNpcRoster.VendorFacing);
        AssertArenaActor(
            arena[1],
            DuelArenaTransporterProtocol.DoorkeeperNpcKey,
            "Arena_002_Male18",
            DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId,
            DuelArenaTransporterProtocol.DoorkeeperSpawnX,
            DuelArenaTransporterProtocol.DoorkeeperSpawnZ,
            1f);
        AssertArenaActor(
            arena[2],
            DuelArenaTransporterProtocol.GatekeeperNpcKey,
            "Arena_003_Male18",
            DuelArenaTransporterProtocol.LegacyGatekeeperNpcId,
            DuelArenaTransporterProtocol.GatekeeperSpawnX,
            DuelArenaTransporterProtocol.GatekeeperSpawnZ,
            1f);
        AssertArenaActor(
            arena[3],
            DuelArenaNpcRoster.WardNpcKey,
            DuelArenaNpcRoster.WardTemplateKey,
            DuelArenaNpcRoster.LegacyWardNpcId,
            DuelArenaNpcRoster.WardSpawnX,
            DuelArenaNpcRoster.WardSpawnZ,
            DuelArenaNpcRoster.WardFacing);
        AssertArenaActor(
            arena[4],
            DuelArenaNpcRoster.PhysicianNpcKey,
            DuelArenaNpcRoster.PhysicianTemplateKey,
            DuelArenaNpcRoster.LegacyPhysicianNpcId,
            DuelArenaNpcRoster.PhysicianSpawnX,
            DuelArenaNpcRoster.PhysicianSpawnZ,
            DuelArenaNpcRoster.PhysicianFacing);
    }

    private static void CheckArenaV17DialogueRelease()
    {
        var publishedNpcKeys = NpcContentBaselineV4.LoadDefinitions()
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
        var texts = NpcDialogueBaselineV17.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV17.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);

        Check.True(
            NpcDialogueBaselineV17.ExpectedSpawnRevision ==
                NpcContentBaselineV4.ExpectedRevision,
            "Arena V17 dialogue pins the V4 spawn revision");
        Check.Equal(
            NpcDialogueBaselineV17.ExpectedTextCount,
            texts.Length,
            "Arena V17 dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV17.ExpectedHashedEntryCount,
            revision.EntryCount,
            "Arena V17 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV17.ExpectedRevision,
            revision.Sha256,
            "Arena V17 dialogue golden revision");
        Check.True(
            texts.Where(static text => text.NpcKey is
                    DuelArenaNpcRoster.VendorNpcKey or
                    DuelArenaNpcRoster.WardNpcKey or
                    DuelArenaNpcRoster.PhysicianNpcKey)
                .Select(static text => text.DisplayName)
                .SequenceEqual(new[]
                {
                    "Arena Vendor",
                    "Arena Ward",
                    "Physician"
                }),
            "Arena V17 retains all three restored native labels");
    }

    private static void AssertArenaActor(
        NpcSpawnDefinition actor,
        string key,
        string template,
        uint id,
        float x,
        float z,
        float facing)
    {
        Check.True(
            actor.NpcKey == key &&
            actor.TemplateKey == template &&
            actor.ObjectId == id &&
            actor.InteractionId == id &&
            actor.X == x &&
            actor.Z == z &&
            actor.Facing == facing &&
            actor.AppearanceType == NpcAppearanceDefaults.AppearanceType,
            $"Arena V4 actor {key} has its exact stable identity and geometry");
    }

    private static (
        short MapId,
        string NpcKey,
        string TemplateKey,
        uint ObjectId) Identity(NpcSpawnDefinition definition) =>
        (definition.MapId,
            definition.NpcKey,
            definition.TemplateKey,
            definition.ObjectId);
}
