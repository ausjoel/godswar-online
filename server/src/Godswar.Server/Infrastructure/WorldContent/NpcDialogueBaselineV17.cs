using System.Collections.Immutable;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Pins the unchanged V16 transport dialogue geometry to the complete V4
/// Arena roster. The three restored client-native actors retain their stock
/// names and descriptions.
/// </summary>
internal static class NpcDialogueBaselineV17
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV16.ExpectedTextCount + 3;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV16.ExpectedProfileCount;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV16.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV16.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV4.ExpectedRevision;
    public const string ExpectedRevision =
        "0F602C24EF6322D661EB023F8F13CD6A89286671F046987F534B48AFAB84144B";
    public const string Source = "reviewed-published-npc-dialogue-v17";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV16.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV16.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV16.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts) =>
        NpcDialogueBaselineV16.ApplyTextOverrides(texts);
}
