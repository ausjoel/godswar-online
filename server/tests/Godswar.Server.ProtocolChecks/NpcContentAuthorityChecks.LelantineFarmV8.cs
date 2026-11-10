using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Pins the two releases the Lelantine Farm is published through: the V8 NPC
/// content that places its roster, and the V24 dialogue release that routes it.
/// </summary>
/// <remarks>
/// The publication refuses to start when a declared count disagrees with what
/// it actually computed, so these checks exist to catch that disagreement here
/// rather than as a crash loop in a running server.
/// </remarks>
internal static partial class NpcContentAuthorityChecks
{
    private static void CheckLelantineFarmV8Release()
    {
        var previous = NpcContentBaselineV7.LoadDefinitions();
        var definitions = NpcContentBaselineV8.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);

        Check.Equal(
            NpcContentBaselineV7.ExpectedEntryCount +
                NpcContentBaselineV8.AddedEntryCount,
            definitions.Length,
            "farm V8 adds exactly the farm roster to V7");
        Check.Equal(
            NpcContentBaselineV8.ExpectedEntryCount,
            definitions.Length,
            "farm V8 declared entry count");
        Check.Equal(
            NpcContentBaselineV8.ExpectedRevision,
            revision.Sha256,
            "farm V8 golden revision");
        Check.Equal(
            definitions.Length,
            revision.EntryCount,
            "farm V8 revision entry count");

        // The reviewed V7 release stays immutable: the farm is additive only.
        var previousKeys = previous
            .Select(static npc => (npc.MapId, npc.NpcKey))
            .ToHashSet();
        foreach (var key in previousKeys)
        {
            Check.True(
                definitions.Any(npc => (npc.MapId, npc.NpcKey) == key),
                $"farm V8 retains V7 actor {key.NpcKey}");
        }

        Check.True(
            !previous.Any(static npc =>
                npc.MapId == LelantineFarmProtocol.MapId),
            "V7 published no farm map rows");

        var farm = definitions
            .Where(static npc => npc.MapId == LelantineFarmProtocol.MapId)
            .ToArray();
        Check.Equal(
            NpcContentBaselineV8.AddedEntryCount,
            farm.Length,
            "farm V8 places the whole roster");
        Check.True(
            farm.All(static npc =>
                npc.SceneKey == LelantineFarmProtocol.SceneKey),
            "farm V8 actors use the published farm scene key");
        Check.True(
            farm.All(static npc => npc.ObjectId == npc.InteractionId),
            "farm V8 actors interact under their own object id");
        Check.True(
            farm.All(static npc => npc.ObjectId is >= 1_500 and < 8_000),
            "farm V8 actor ids stay inside the native npc range");
        Check.True(
            farm.All(static npc =>
                npc.AppearanceType == LelantineFarmProtocol.AppearanceType),
            "farm V8 actors share the captured WarField appearance word");
        Check.True(
            farm.Select(static npc => npc.ObjectId).SequenceEqual(
                LelantineFarmProtocol.AllowedFarmNpcIds),
            "farm V8 uses the reserved farm npc id band");
        Check.True(
            !previous.Any(npc =>
                LelantineFarmProtocol.AllowedFarmNpcIds.Contains(npc.ObjectId)),
            "farm V8 ids are free on every published map");

        // Every authored row must resolve through the protocol's own roster, so
        // the placement and the dialogue endpoints cannot drift apart.
        foreach (var npc in farm)
        {
            Check.True(
                LelantineFarmProtocol.TryResolve(
                    npc.NpcKey,
                    npc.InteractionId,
                    out var resolved),
                $"farm V8 actor {npc.NpcKey} resolves through the protocol");
            Check.Equal(
                npc.TemplateKey,
                resolved.TemplateKey,
                $"farm V8 actor {npc.NpcKey} template");
            Check.Equal(npc.X, resolved.X, $"farm V8 actor {npc.NpcKey} X");
            Check.Equal(npc.Z, resolved.Z, $"farm V8 actor {npc.NpcKey} Z");
        }

        Check.True(
            new[] { "Lelantine_Farm_003", "Lelantine_Farm_004" }
                .All(key => farm.Any(npc => npc.NpcKey == key)),
            "farm V8 places both captured activity endpoints");
        Check.True(
            farm.Any(static npc =>
                npc.NpcKey == "Lelantine_Farm_002" &&
                npc.X == 6f && npc.Z == 10f),
            "farm V8 anchors Kelsis at the published Kelsis point");

        var athens = farm.Single(static npc =>
            npc.NpcKey == "Lelantine_Farm_003");
        var sparta = farm.Single(static npc =>
            npc.NpcKey == "Lelantine_Farm_006");
        Check.True(
            athens.X < 0 && athens.Z > 0,
            "farm V8 Athenian captain stands on the Athenian base");
        Check.True(
            sparta.X > 0 && sparta.Z < 0,
            "farm V8 Spartan captain stands on the Spartan base");
    }

    /// <summary>
    /// The V24 dialogue release must satisfy every count its own publication
    /// checks, because a disagreement there aborts server startup.
    /// </summary>
    private static void CheckLelantineFarmV24Release()
    {
        var definitions = NpcContentBaselineV8.LoadDefinitions();

        // The publication joins placed npc keys to the shipped text seed, one
        // row per key, then the inherited overlays add the two captured Arena
        // actors that have no seed row.
        var textByKey = NpcTemplateSeeds.Texts
            .GroupBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        var rawTexts = definitions
            .Select(static definition => definition.NpcKey)
            .Distinct(StringComparer.Ordinal)
            .Where(textByKey.ContainsKey)
            .Select(key => textByKey[key])
            .Select(static text => new NpcTextDefinition(
                text.NpcKey,
                text.SceneKey,
                text.DisplayName,
                text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();

        var texts = NpcDialogueBaselineV24.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV24.CreateRoutes();
        var profiles = NpcDialogueBaselineV24.Profiles;
        var menuEntries = profiles.Sum(
            static profile => profile.InitialMenuSubIds.Length);
        var payload = WorldContentRevisionHasher.HashNpcDialogues(texts, routes);

        Check.Equal(
            NpcDialogueBaselineV24.ExpectedTextCount,
            texts.Length,
            "farm V24 published text count");
        Check.Equal(
            NpcDialogueBaselineV24.ExpectedRouteCount,
            routes.Length,
            "farm V24 published route count");
        Check.Equal(
            NpcDialogueBaselineV24.ExpectedProfileCount,
            profiles.Length,
            "farm V24 published profile count");
        Check.Equal(
            NpcDialogueBaselineV24.ExpectedMenuEntryCount,
            menuEntries,
            "farm V24 published menu-entry count");
        Check.Equal(
            NpcDialogueBaselineV24.ExpectedHashedEntryCount,
            payload.EntryCount,
            "farm V24 hashed entry count");
        Check.Equal(
            NpcDialogueBaselineV24.ExpectedRevision,
            payload.Sha256,
            "farm V24 canonical revision");
        Check.Equal(
            NpcContentBaselineV8.ExpectedRevision,
            NpcDialogueBaselineV24.ExpectedSpawnRevision,
            "farm V24 targets the V8 spawn release");

        // The database's own publication trigger requires the text count to
        // equal the spawn entry count of the release being targeted.
        Check.Equal(
            definitions.Length,
            texts.Length,
            "farm V24 text rows equal the V8 spawn rows");

        // The two farm profiles must be present under the farm's own dialog
        // numbers, since the client selects the script by that number alone.
        var farmProfile = profiles.Single(static profile =>
            profile.ProfileKey == "lelantine_farm");
        Check.Equal(
            LelantineFarmProtocol.FarmDialogIndex,
            farmProfile.DialogIndex,
            "farm profile carries the NpcFunFarm dialog index");
        Check.True(
            farmProfile.Behavior == NpcDialogueBehavior.Farm,
            "farm profile uses the farm behaviour");
        Check.True(
            farmProfile.InitialMenuSubIds.SequenceEqual(
                LelantineFarmProtocol.CaptainMenuSubIds),
            "farm profile advertises the captured captain root menu");

        var returnProfile = profiles.Single(static profile =>
            profile.ProfileKey == "lelantine_farm_return");
        Check.Equal(
            LelantineFarmProtocol.TeleportDialogIndex,
            returnProfile.DialogIndex,
            "farm return profile carries the transport dialog index");
        Check.True(
            returnProfile.Behavior ==
                NpcDialogueBehavior.FarmReturnTeleporter,
            "farm return profile uses the return behaviour");
    }
}
