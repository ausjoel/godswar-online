using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// NORTH-GIVERS-2026-11 (Development/quests-audit/release-2026-11/giver): the north quests
/// leave the Altar Envoy, who is Mycenae's transporter, for six GM-placed givers beside
/// him, one per map band, so the transporter dialog stays reachable and no giver's offer
/// list overflows the client's twenty slots by more than one.
/// </summary>
internal static class NorthQuestGiverChecks
{
    public const string CheckName = "North quest givers: six GM npcs beside the Mycenae transporter";

    private const string Envoy = "Mycenae_All_013";

    private const uint EnvoyObjectId = 59_721;

    public static Task RunAsync()
    {
        try
        {
            Table();
            Assignment();
            OfferCounts();
            Placement();
            MissingGivers();
        }
        finally
        {
            GmNpcOverrideCatalog.Install(GmNpcOverrideSnapshot.Empty);
        }

        return Task.CompletedTask;
    }

    private static void Table()
    {
        var givers = NorthQuestGivers.All;
        Check.Equal(6, givers.Count, "givers: one per north map band");
        Check.Equal(6, givers.Select(g => g.NpcKey).Distinct().Count(), "givers: keys unique");
        Check.Equal(6, givers.Select(g => g.ObjectId).Distinct().Count(), "givers: object ids unique");
        Check.True(givers.All(g => g.NpcKey != Envoy && g.ObjectId != EnvoyObjectId), "givers: none is the Altar Envoy");
        Check.True(givers.All(g => g.ObjectId is >= 65_460 and <= 65_465),
            "givers: object ids clear of npc-placement 65410-65447 and the transporter plan 65490/65491");
        var templates = NpcTemplateSeeds.Appearances.ToDictionary(t => t.TemplateKey, StringComparer.Ordinal);
        foreach (var giver in givers)
        {
            Check.True(templates.TryGetValue(giver.TemplateKey, out var template) && template.NpcKey == giver.NpcKey,
                $"giver {giver.NpcKey}: its template is an Origin client appearance of that key");
            Check.True(Math.Sqrt(Math.Pow(giver.X + 42, 2) + Math.Pow(giver.Z - 24, 2)) is >= 4.5 and <= 10,
                $"giver {giver.NpcKey}: stands 4.5-10 units from the Envoy (-42, 24)");
        }
    }

    private static void Assignment()
    {
        var north = NorthQuests.GeneratedSteps;
        var keys = NorthQuestGivers.All.Select(g => g.NpcKey).ToHashSet(StringComparer.Ordinal);
        Check.True(north.All(step => step.GiverKey == step.ResponderKey && keys.Contains(step.GiverKey)),
            "north: every quest is given and taken back by one north giver");
        Check.Equal(0, north.Count(step => step.GiverKey == Envoy || step.ResponderKey == Envoy),
            "north: the Altar Envoy (transporter) gives and takes no north quest");
        Check.True(StarterQuestChain.Steps.All(step => !keys.Contains(step.GiverKey) && !keys.Contains(step.ResponderKey) ||
                                                      north.Any(n => n.QuestId == step.QuestId)),
            "givers: no published quest uses a north giver key");

        // One giver per map: the objectives' map says which band a quest belongs to.
        var objectives = NorthQuests.AddObjectives(new Dictionary<uint, QuestObjective[]>());
        var mapsByGiver = north.GroupBy(step => step.GiverKey)
            .ToDictionary(g => g.Key, g => g.Select(step => objectives[step.QuestId][0].MapId).Distinct().ToList());
        Check.Equal(6, mapsByGiver.Count, "north: all six givers are used");
        Check.True(mapsByGiver.Values.All(maps => maps.Count == 1), "north: each giver serves one map");
        Check.Equal(6, mapsByGiver.Values.Select(maps => maps[0]).Distinct().Count(), "north: each map has its own giver");
        Check.True(north.GroupBy(step => step.GiverKey).All(g =>
                g.Count(step => step.Camp == StarterQuestChain.SpartaCamp) ==
                g.Count(step => step.Camp == StarterQuestChain.AthensCamp)),
            "north: each giver serves both camps alike");
    }

    private static void OfferCounts()
    {
        // What a character could take at each giver, before any story step is done (the
        // most a level can have). The client's npc list holds twenty.
        var north = NorthQuests.GeneratedSteps;
        var worst = 0;
        var over = 0;
        foreach (var giver in NorthQuestGivers.All)
        {
            foreach (var camp in new[] { StarterQuestChain.SpartaCamp, StarterQuestChain.AthensCamp })
            {
                for (var level = 141; level <= 200; level++)
                {
                    var open = north.Count(step => step.GiverKey == giver.NpcKey && step.Camp == camp &&
                                                   (!step.IsMainLine || step.PrerequisiteQuestId == 0) &&
                                                   GameClientHandler.IsWithinAcceptBand(level, step));
                    worst = Math.Max(worst, open);
                    over += open > PacketBuilderCapacity ? 1 : 0;
                }
            }
        }

        Check.True(worst <= 21, $"givers: at most 21 offers at one giver (worst {worst}; one giver used to have 32)");
        Check.True(over <= 12, $"givers: at most 6 levels per camp over the 20 slots (found {over} giver-levels)");
        for (var level = 141; level <= 200; level++)
        {
            var at = level;
            Check.True(north.Any(step => step.Camp == StarterQuestChain.SpartaCamp &&
                                         (!step.IsMainLine || step.PrerequisiteQuestId == 0) &&
                                         GameClientHandler.IsWithinAcceptBand(at, step)),
                $"north: level {level} has an offer at a north giver");
        }
    }

    private const int PacketBuilderCapacity = Packets.PacketBuilder.QuestNpcListCapacity;

    private static void Placement()
    {
        var envoy = new NpcSpawnDefinition(
            NorthQuestGivers.MapId, "Mycenae_All", Envoy, "Mycenae_All_013_Male34", EnvoyObjectId,
            -42f, 24f, InteractionId: EnvoyObjectId, 17, 1f, [], []);
        var snapshot = Snapshot();
        GmNpcOverrideCatalog.Install(snapshot);
        Check.True(NorthQuestGivers.All.All(g => !snapshot.TryGetDialogue(null, out _)),
            "givers: no GM dialogue, so a click falls through to the quest page");

        var placed = GmNpcDialoguePolicy.BuildPlacements(snapshot, NorthQuestGivers.MapId, [envoy]);
        Check.Equal(6, placed.Count, "placement: all six givers placed on Mycenae");
        Check.True(placed.All(npc => npc.SceneKey == "Mycenae_All" && npc.InteractionId == npc.ObjectId),
            "placement: Mycenae scene key, interaction id = object id");
        var empty = GmNpcDialoguePolicy.BuildPlacements(snapshot, NorthQuestGivers.MapId, []);
        Check.Equal(6, empty.Count, "placement: also placed with no published roster (map scene key fallback)");

        foreach (var giver in NorthQuestGivers.All)
        {
            Check.Equal(giver.ObjectId, GameClientHandler.ResolveGmQuestNpcId(NorthQuestGivers.MapId, giver.NpcKey, [envoy]),
                $"snapshot fallback: {giver.NpcKey} resolves to its GM object id");
            // A character logging in on a north map resolves the giver through its
            // GiverMapId (6), never through the map it stands on.
            Check.Equal(0u, GameClientHandler.ResolveGmQuestNpcId(20, giver.NpcKey, []),
                $"snapshot fallback: {giver.NpcKey} is not placed on Delphi Forest");
        }

        Check.True(NorthQuests.GeneratedSteps.All(step => step.GiverMapId == NorthQuestGivers.MapId &&
                                                          step.ResponderMapId == NorthQuestGivers.MapId),
            "north: giver and responder map is Mycenae, where the givers are placed");
        Check.Equal(0u, GameClientHandler.ResolveGmQuestNpcId(NorthQuestGivers.MapId, Envoy, [envoy]),
            "snapshot fallback: a published npc is not resolved as a GM npc");
    }

    private static void MissingGivers()
    {
        var north = NorthQuests.GeneratedSteps;
        Check.Equal(0, NorthQuestGiverPolicy.MissingGivers(Snapshot(), north).Count, "startup: nothing missing with the six rows");
        Check.Equal(6, NorthQuestGiverPolicy.MissingGivers(GmNpcOverrideSnapshot.Empty, north).Count,
            "startup: all six reported without the rows");
        var oneOff = GmNpcOverrideSnapshot.Create(
            [.. Rows().Select((row, index) => index == 0 ? row with { Enabled = false } : row)], []);
        Check.Equal(1, NorthQuestGiverPolicy.MissingGivers(oneOff, north).Count, "startup: a disabled giver is reported");
        Check.Equal(0, NorthQuestGiverPolicy.MissingGivers(GmNpcOverrideSnapshot.Empty, []).Count,
            "startup: nothing reported while the north quests are off");
    }

    private static GmNpcOverrideSnapshot Snapshot() => GmNpcOverrideSnapshot.Create(Rows(), []);

    private static List<GmNpcSpawnOverride> Rows() =>
        [.. NorthQuestGivers.All.Select((g, index) => new GmNpcSpawnOverride(
            index + 1, $"Mycenae - North quests: {g.Map} ({g.Name})", NorthQuestGivers.MapId, g.NpcKey,
            g.TemplateKey, null, g.ObjectId, NorthQuestGivers.AppearanceType, g.X, g.Z, 1f, true))];
}
