using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Binds the dialogue publication to the V9 spawn release, which places the nine
/// extra Sparta actors the September 28 2026 capture recorded.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is added to the dialogue set itself: the nine are ordinary city NPCs
/// with a description and no function number of their own, so they only need the
/// text row the publication's own join already produces for every placed key.
/// That join is why this release exists at all - the text count is the spawn
/// release's entry count, so a spawn release that adds rows cannot be served by
/// a dialogue release pinned to the previous one.
/// </para>
/// <para>
/// Every V24 text, route, profile and binding is retained unchanged, and the V24
/// spawn dependency is replaced with V9's.
/// </para>
/// </remarks>
internal static class NpcDialogueBaselineV25
{
    /// <summary>
    /// The published text rows the V9 spawn release resolves to: V24's count plus
    /// the nine captured Sparta actors, each of which has a shipped
    /// <c>npc_text_templates</c> row.
    /// </summary>
    public const int ExpectedTextCount =
        NpcDialogueBaselineV24.ExpectedTextCount +
        NpcContentBaselineV9.AddedEntryCount;

    public const int ExpectedProfileCount =
        NpcDialogueBaselineV24.ExpectedProfileCount;

    public static readonly int ExpectedRouteCount =
        NpcDialogueBaselineV24.ExpectedRouteCount;

    public static readonly int ExpectedMenuEntryCount =
        NpcDialogueBaselineV24.ExpectedMenuEntryCount;

    /// <summary>
    /// Texts plus routes, which is what the publication's revision records.
    /// </summary>
    public static readonly int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;

    /// <summary>
    /// The spawn release this dialogue release targets. The nine Sparta actors
    /// only exist from V9 on, so the V24 dependency cannot satisfy it.
    /// </summary>
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV9.ExpectedRevision;

    public const string Source = "reviewed-published-npc-dialogue-v25";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical text and route set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "0016F6868FFACE2290AFB49D507CC781A7D35C9092C7172EA47FD1A36386057B";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV24.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV24.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV24.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts) =>
        NpcDialogueBaselineV24.ApplyTextOverrides(texts);
}
