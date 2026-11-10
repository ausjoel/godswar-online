using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Pins the V25 dialogue release, which binds the dialogue publication to the V9
/// spawn release that places the nine captured Sparta actors.
/// </summary>
/// <remarks>
/// The publication refuses to start when the text count disagrees with the spawn
/// release's entry count, so this check rebuilds the publication's own join -
/// placed keys against the shipped text seed - and compares it here instead of
/// leaving it to a crash loop in a running server.
/// </remarks>
internal static partial class NpcContentAuthorityChecks
{
    private static void CheckSpartaV25Release()
    {
        var definitions = NpcContentBaselineV9.LoadDefinitions();

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

        var texts = NpcDialogueBaselineV25.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV25.CreateRoutes();
        var profiles = NpcDialogueBaselineV25.Profiles;
        var payload = WorldContentRevisionHasher.HashNpcDialogues(texts, routes);

        Check.Equal(
            NpcDialogueBaselineV25.ExpectedTextCount,
            texts.Length,
            "Sparta V25 published text count");
        Check.Equal(
            NpcDialogueBaselineV25.ExpectedRouteCount,
            routes.Length,
            "Sparta V25 published route count");
        Check.Equal(
            NpcDialogueBaselineV25.ExpectedProfileCount,
            profiles.Length,
            "Sparta V25 published profile count");
        Check.Equal(
            NpcDialogueBaselineV25.ExpectedMenuEntryCount,
            profiles.Sum(static profile => profile.InitialMenuSubIds.Length),
            "Sparta V25 published menu-entry count");
        Check.Equal(
            NpcDialogueBaselineV25.ExpectedHashedEntryCount,
            payload.EntryCount,
            "Sparta V25 hashed entry count");
        Check.Equal(
            NpcDialogueBaselineV25.ExpectedRevision,
            payload.Sha256,
            "Sparta V25 canonical revision");
        Check.Equal(
            NpcContentBaselineV9.ExpectedRevision,
            NpcDialogueBaselineV25.ExpectedSpawnRevision,
            "Sparta V25 targets the V9 spawn release");

        // The publication's own trigger requires the text count to equal the
        // spawn release's entry count.
        Check.Equal(
            definitions.Length,
            texts.Length,
            "Sparta V25 text rows equal the V9 spawn rows");

        // The nine captured actors must each contribute a text row, which is the
        // only thing this release changes about the dialogue set.
        foreach (var captured in NpcContentBaselineV9.CapturedNpcs)
        {
            Check.True(
                texts.Any(text => text.NpcKey == captured.NpcKey),
                $"{captured.NpcKey} publishes a description row");
        }

        // The route set is inherited unchanged: the nine are description-only.
        Check.True(
            !routes.Any(static route =>
                route.NpcKey is "Sparta_012" or "Sparta_024" or "Sparta_025" or
                    "Sparta_063" or "Sparta_065" or "Sparta_066" or
                    "Sparta_067" or "Sparta_117" or "Sparta_126"),
            "the nine captured actors carry no dialogue route");
    }
}
