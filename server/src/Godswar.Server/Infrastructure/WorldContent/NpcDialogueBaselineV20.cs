using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the seven Arena actors and direct transport endpoints observed
/// in artifacts/duel-arena-capture-20260907/arena-npc-evidence.json.
/// </summary>
internal static class NpcDialogueBaselineV20
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV19.ExpectedTextCount + 2;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV19.ExpectedProfileCount + 1;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV19.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV19.ExpectedMenuEntryCount + 1;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV7.ExpectedRevision;
    public const string ExpectedRevision =
        "DAA3796DB22B08A419EBB95017FD0587ECEF66FEB92086EB9C741C2817E7291E";
    public const string Source = "reviewed-published-npc-dialogue-v20";
    public const string GatekeeperProfileKey = "duel_arena_captured_gatekeeper";
    public const string WardProfileKey = "duel_arena_captured_ward";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles
        { get; } =
    [
        .. NpcDialogueBaselineV19.Profiles.Where(static profile =>
            profile.ProfileKey != "duel_arena_transporter"),
        new(GatekeeperProfileKey,
            DuelArenaCapturedTransportProtocol.GatekeeperDialogIndex,
            NpcDialogueBehavior.DuelArenaTransporter, -1,
            [DuelArenaCapturedTransportProtocol.TravelSubId]),
        new(WardProfileKey,
            DuelArenaCapturedTransportProtocol.WardDialogIndex,
            NpcDialogueBehavior.DuelArenaTransporter, -1,
            [DuelArenaCapturedTransportProtocol.TravelSubId])
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings
        { get; } =
    [
        .. NpcDialogueBaselineV19.Bindings.Where(static binding =>
            binding.ProfileKey != "duel_arena_transporter"),
        new(DuelArenaCapturedLayout.GatekeeperNpcKey,
            DuelArenaCapturedTransportProtocol.GatekeeperClientScriptKey,
            GatekeeperProfileKey),
        new(DuelArenaCapturedLayout.WardNpcKey,
            DuelArenaCapturedLayout.WardNpcKey, WardProfileKey)
    ];

    public static NpcDialogueRouteDefinition[] CreateRoutes()
    {
        var profiles = Profiles.ToDictionary(
            static profile => profile.ProfileKey, StringComparer.Ordinal);
        return Bindings
            .OrderBy(static binding => binding.NpcKey, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RouteOrder)
            .Select(binding =>
            {
                var profile = profiles[binding.ProfileKey];
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
        var originals = texts.ToDictionary(
            static text => text.NpcKey, StringComparer.Ordinal);
        var inherited = NpcDialogueBaselineV19.ApplyTextOverrides(texts)
            .ToDictionary(static text => text.NpcKey, StringComparer.Ordinal);
        foreach (var npcKey in new[]
                 { "Arena_001", "Arena_002", "Arena_003", "Arena_004", "Arena_005" })
        {
            if (!originals.TryGetValue(npcKey, out var original))
            {
                throw new InvalidDataException(
                    $"The V20 Arena text overlay requires '{npcKey}'.");
            }

            inherited[npcKey] = original;
        }

        // The capture restores these actors without altering the generated
        // stock template seed or any sealed predecessor publication.
        inherited["DuelArena_001"] = new NpcTextDefinition(
            "DuelArena_001", "Arena", "[Warehouse] Akou",
            "After the logistics are developed, I will suggest they form a " +
            "|cffF14187Federal Trade Bank|cffffffff. Thus,the " +
            "|cffF14187Atticas Alliance in Athens will become stronger.");
        inherited["Arena_006"] = new NpcTextDefinition(
            "Arena_006", "Arena", "Airdrop Merchant",
            "I offer the best airdrop for the community.");
        return inherited.Values
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
    }
}
