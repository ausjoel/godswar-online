using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the two capital Battlefield Transporters and expands the stock
/// Instance Caller root menu for Atlantis and Wonderland. All endpoint menus
/// remain finite, ordered, and server-owned.
/// </summary>
internal static class NpcDialogueBaselineV12
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV11.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV11.ExpectedProfileCount + 2;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV11.ExpectedRouteCount + 2;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV11.ExpectedMenuEntryCount + 8;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV11.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "23EB65E8BB62489F894F83F511FD2EB0B26A84E74712692DF848B65623C23877";
    public const string Source = "reviewed-published-npc-dialogue-v12";
    public const string BattlefieldTransporterDescription =
        "Travel to Pindus Mountains, Ni Mini Valley, or the Duel Arena. " +
        "Pindus (Lv31-120) opens for 45 minutes at 9:00pm Wednesday and " +
        "Friday; 8:00am and 3:00pm Saturday; and 8:00am and 9:00pm Sunday. " +
        "Ni Mini (Lv70-89) opens for 45 minutes at 7:00pm Friday, 7:00am " +
        "Saturday, and 7:00pm Sunday. Duel Arena is always available. " +
        "All times follow the realm calendar.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV11.Profiles.Where(
            static profile => !string.Equals(
                profile.ProfileKey,
                "instance_caller",
                StringComparison.Ordinal)),
        new(
            "instance_caller",
            InstanceCallerProtocol.DialogIndex,
            NpcDialogueBehavior.InstanceCaller,
            InstanceCallerProtocol.InitialRequestSubId,
            InstanceCallerProtocol.InitialMenuSubIds.ToImmutableArray()),
        new(
            "sparta_battlefield_transporter",
            BattlefieldTransporterProtocol.DialogIndex,
            NpcDialogueBehavior.BattlefieldTransporter,
            BattlefieldTransporterProtocol.InitialRequestSubId,
            BattlefieldTransporterProtocol.SpartaInitialMenuSubIds
                .ToImmutableArray()),
        new(
            "athens_battlefield_transporter",
            BattlefieldTransporterProtocol.DialogIndex,
            NpcDialogueBehavior.BattlefieldTransporter,
            BattlefieldTransporterProtocol.InitialRequestSubId,
            BattlefieldTransporterProtocol.AthensInitialMenuSubIds
                .ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV11.Bindings,
        new(
            "Athens_056",
            "Athens_056",
            "athens_battlefield_transporter"),
        new(
            "Sparta_056",
            "Sparta_056",
            "sparta_battlefield_transporter")
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
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV11.ApplyTextOverrides(texts);
        var overrideCount = 0;
        var result = inherited.Select(text =>
        {
            if (text.NpcKey is not ("Athens_056" or "Sparta_056"))
            {
                return text;
            }

            overrideCount++;
            return text with
            {
                Description = BattlefieldTransporterDescription
            };
        }).ToArray();
        if (overrideCount != 2)
        {
            throw new InvalidDataException(
                "The V12 Battlefield text overlay requires both capital " +
                "transporters exactly once.");
        }

        return result;
    }
}
