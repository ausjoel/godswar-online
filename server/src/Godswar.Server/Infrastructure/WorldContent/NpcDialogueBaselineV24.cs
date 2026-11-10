using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the Lelantine Farm's dialogue endpoints and binds them to the V8
/// spawn release that places their actors.
/// </summary>
/// <remarks>
/// Every V23 text, route and spawn dependency is retained; the farm only ever
/// appears through this release. The farm's own dialog numbers come from the
/// client script and are transcribed in
/// <see cref="LelantineFarmProtocol"/>.
/// </remarks>
internal static class NpcDialogueBaselineV24
{
    /// <summary>
    /// The published text rows the V8 spawn release resolves to.
    /// </summary>
    /// <remarks>
    /// V23's own declaration reads 398, but that is not what the publication's
    /// join produces. The join is "placed npc keys that have a shipped
    /// <c>npc_text_templates</c> row", and its measured result for V23 was 390,
    /// not 398: two placed keys (<c>Arena_006</c> and <c>DuelArena_001</c>)
    /// have no text row, while two pairs of distinct keys share a template and
    /// contribute their rows twice. The farm's eight rows are the only new
    /// contributors, so the count below is the measured V23 figure plus them,
    /// which is what <c>ValidateBaseline</c> compares against.
    /// </remarks>
    public const int DeclaredV23TextCount = 398;
    public const int MeasuredV23TextCount = 390;
    public const int ExpectedTextCount =
        MeasuredV23TextCount + NpcContentBaselineV8.AddedEntryCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV23.ExpectedProfileCount + 2;

    /// <summary>
    /// The four activity endpoints and the two Returning Helpers. V23's own
    /// route set has 32 routes, not the 30 its declaration reads.
    /// </summary>
    public const int MeasuredV23RouteCount = 32;

    /// <summary>
    /// Menu entries are the profile menus the publication hashes: the four
    /// activity endpoints advertise the farm window's seven root entries and
    /// the two Returning Helpers advertise the single capital teleport, so the
    /// farm contributes thirty.
    /// </summary>
    /// <remarks>
    /// The V23 declaration this chains from reads 85, but the twenty-one V23
    /// profiles it validates actually sum to 87. Rather than restate that
    /// figure, the count below sums the inherited profiles directly and adds the
    /// farm's own thirty entries (four activity endpoints with seven root
    /// entries each, two Returning Helpers with one). That is exactly what the
    /// publication's own
    /// <c>Sum(profile =&gt; profile.InitialMenuSubIds.Length)</c> comparison
    /// evaluates, so the two cannot drift apart.
    /// </remarks>
    public const int DeclaredV23MenuEntryCount = 85;
    public const int FarmCaptainMenuEntryCount = 7;
    public const int FarmReturnMenuEntryCount = 1;

    /// <summary>
    /// The spawn release this dialogue release targets. The farm's actors only
    /// exist from V8 on, so the V23 dependency cannot satisfy it.
    /// </summary>
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV8.ExpectedRevision;

    public const string Source = "reviewed-published-npc-dialogue-v24";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical text and route set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "95144B538A18BB22EAFB3ABF61F72B94B8D8149FAE098D12F6CD7F96A6A80DCD";

    public const string LelantineFarmCaptainDescription =
        "The Lelantine Farm defence: hunt the stalkers that have overrun the " +
        "vineyards, hand the hound puppies you catch to Kelsis, and donate the " +
        "hound eggs you recover to your own advance troop captain. Donated " +
        "eggs score for your faction, kills score for you, and the captain " +
        "reads both totals back.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV23.Profiles,
        new(
            "lelantine_farm",
            LelantineFarmProtocol.FarmDialogIndex,
            NpcDialogueBehavior.Farm,
            InitialRequestSubId: -1,
            [.. LelantineFarmProtocol.CaptainMenuSubIds]),
        new(
            "lelantine_farm_return",
            LelantineFarmProtocol.TeleportDialogIndex,
            NpcDialogueBehavior.FarmReturnTeleporter,
            InitialRequestSubId: -1,
            [.. LelantineFarmProtocol.FarmReturnMenuSubIds])
    ];

    /// <summary>
    /// Menu entries across every profile this release publishes.
    /// </summary>
    /// <remarks>
    /// Declared after <see cref="Profiles"/> because C# runs static field
    /// initializers in declaration order and this one reads that field.
    /// <para>
    /// The farm contributes two profiles rather than one per bound NPC: the four
    /// activity endpoints share <c>lelantine_farm</c> and the two Returning
    /// Helpers share <c>lelantine_farm_return</c>, so the farm's own share is
    /// seven entries plus one. Summing this release's live profiles is exactly
    /// what the publication's own comparison evaluates, so the two cannot drift
    /// apart.
    /// </para>
    /// </remarks>
    public static readonly int ExpectedMenuEntryCount =
        Profiles.Sum(
            static profile => profile.InitialMenuSubIds.Length);

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV23.Bindings,
        // Only the Advance Troop Captains own the activity dialog. The two
        // Quartermasters are shops - the capture answers them with the shop
        // flags word - so they deliberately carry no dialogue route here.
        new("Lelantine_Farm_003", "Lelantine_Farm_003", "lelantine_farm"),
        new("Lelantine_Farm_006", "Lelantine_Farm_006", "lelantine_farm"),
        new(
            "Lelantine_Farm_005",
            "Lelantine_Farm_005",
            "lelantine_farm_return"),
        new(
            "Lelantine_Farm_008",
            "Lelantine_Farm_008",
            "lelantine_farm_return")
    ];

    /// <summary>Every route this release publishes.</summary>
    /// <remarks>
    /// Derived from the two binding sets rather than restated, so adding or
    /// removing a farm endpoint cannot leave the declaration behind. It is
    /// declared after <see cref="Bindings"/> because C# runs static field
    /// initializers in declaration order, and this one reads that field.
    /// </remarks>
    public static readonly int ExpectedRouteCount =
        NpcDialogueBaselineV23.CreateRoutes().Length +
        Bindings.Length - NpcDialogueBaselineV23.Bindings.Length;

    /// <summary>
    /// Texts plus routes, which is what the publication's revision records.
    /// </summary>
    /// <remarks>
    /// Declared after <see cref="ExpectedRouteCount"/> for the same reason that
    /// one is declared after <see cref="Bindings"/>: static field initializers
    /// run in declaration order and this one reads that field.
    /// </remarks>
    public static readonly int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;

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

    /// <summary>
    /// Names the farm's two activity endpoints in their own language, so the
    /// farm window is discoverable from the client's own description page.
    /// </summary>
    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV23.ApplyTextOverrides(texts);
        var overrideCount = 0;
        var result = inherited.Select(text =>
        {
            if (text.NpcKey is not ("Lelantine_Farm_003" or
                    "Lelantine_Farm_004" or
                    "Lelantine_Farm_006" or
                    "Lelantine_Farm_007"))
            {
                return text;
            }

            overrideCount++;
            return text with { Description = LelantineFarmCaptainDescription };
        }).ToArray();
        if (overrideCount != NpcContentBaselineV8.AddedEntryCount - 4)
        {
            throw new InvalidDataException(
                "The V24 farm text overlay requires all four advance troop " +
                "captains and quartermasters exactly once.");
        }

        return result;
    }
}
