using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the three reviewed ordinary Transporter endpoints with separate
/// endpoint-specific menus. Travel admission remains finite and server-owned.
/// </summary>
internal static class NpcDialogueBaselineV11
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV10.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV10.ExpectedProfileCount + 3;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV10.ExpectedRouteCount + 3;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV10.ExpectedMenuEntryCount + 15;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV10.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "4FA0131A203FEBF79E80EA0F48A5EB7CD1284E96FBAAE7076200EE3CBACD17A9";
    public const string Source = "reviewed-published-npc-dialogue-v11";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV10.Profiles,
        new(
            "sparta_transporter",
            TransporterProtocol.DialogIndex,
            NpcDialogueBehavior.Transporter,
            TransporterProtocol.InitialRequestSubId,
            TransporterProtocol.SpartaInitialMenuSubIds.ToImmutableArray()),
        new(
            "athens_transporter",
            TransporterProtocol.DialogIndex,
            NpcDialogueBehavior.Transporter,
            TransporterProtocol.InitialRequestSubId,
            TransporterProtocol.AthensInitialMenuSubIds.ToImmutableArray()),
        new(
            "mycenae_transporter",
            TransporterProtocol.DialogIndex,
            NpcDialogueBehavior.Transporter,
            TransporterProtocol.InitialRequestSubId,
            TransporterProtocol.MycenaeInitialMenuSubIds.ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV10.Bindings,
        new("Athens_041", "Athens_041", "athens_transporter"),
        new("Mycenae_All_013", "Mycenae_All_013", "mycenae_transporter"),
        new("Sparta_042", "Sparta_042", "sparta_transporter")
    ];

    public static NpcDialogueRouteDefinition[] CreateRoutes()
    {
        var profiles = Profiles.ToDictionary(
            static profile => profile.ProfileKey,
            StringComparer.Ordinal);
        return Bindings
            .OrderBy(static binding => binding.NpcKey, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RouteOrder)
            .Select(binding =>
            {
                if (!profiles.TryGetValue(binding.ProfileKey, out var profile))
                {
                    throw new InvalidDataException(
                        $"Unknown NPC dialogue profile '{binding.ProfileKey}'.");
                }

                return new NpcDialogueRouteDefinition(
                    binding.NpcKey,
                    binding.ClientScriptKey,
                    profile.DialogIndex,
                    profile.Behavior,
                    profile.InitialMenuSubIds)
                {
                    RouteOrder = binding.RouteOrder
                };
            })
            .ToArray();
    }

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts) =>
        NpcDialogueBaselineV10.ApplyTextOverrides(texts);
}
