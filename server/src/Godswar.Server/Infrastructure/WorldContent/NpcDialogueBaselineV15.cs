using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Binds both client-native Arena transport actors to one finite profile. The
/// actors share dialog 1/menu 1001 while the server resolves direction from
/// the clicked, distance-authorized endpoint.
/// </summary>
internal static class NpcDialogueBaselineV15
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV14.ExpectedTextCount + 2;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV14.ExpectedProfileCount + 1;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV14.ExpectedRouteCount + 2;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV14.ExpectedMenuEntryCount + 1;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV2.ExpectedRevision;
    public const string ExpectedRevision =
        "891C909F43D82CC33A26786BB41A4E05B2C652A5DBD6342688280D3C38E7591A";
    public const string Source = "reviewed-published-npc-dialogue-v15";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles
        { get; } =
    [
        .. NpcDialogueBaselineV14.Profiles,
        new(
            "duel_arena_transporter",
            DuelArenaTransporterProtocol.DialogIndex,
            NpcDialogueBehavior.DuelArenaTransporter,
            DuelArenaTransporterProtocol.InitialRequestSubId,
            DuelArenaTransporterProtocol.InitialMenuSubIds
                .ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings
        { get; } =
    [
        .. NpcDialogueBaselineV14.Bindings,
        new(
            DuelArenaTransporterProtocol.DoorkeeperNpcKey,
            DuelArenaTransporterProtocol.DoorkeeperNpcKey,
            "duel_arena_transporter"),
        new(
            DuelArenaTransporterProtocol.GatekeeperNpcKey,
            DuelArenaTransporterProtocol.GatekeeperNpcKey,
            "duel_arena_transporter")
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
                if (!profiles.TryGetValue(
                        binding.ProfileKey,
                        out var profile))
                {
                    throw new InvalidDataException(
                        $"Unknown NPC dialogue profile " +
                        $"'{binding.ProfileKey}'.");
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
        NpcDialogueBaselineV14.ApplyTextOverrides(texts);
}
