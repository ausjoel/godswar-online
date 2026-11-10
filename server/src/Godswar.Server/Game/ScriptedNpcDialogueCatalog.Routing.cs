using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The scripted dialogues an NPC answers with, keyed by the client function number
    /// each one owns, or <see langword="null"/> when the NPC is not one of them.
    /// </summary>
    /// <remarks>
    /// Keyed, because one NPC can advertise several functions: the open packet's
    /// <c>10067</c> field at <c>+12</c> is a base-1000 packed list of function numbers,
    /// and the client later echoes the chosen one back as the <c>10069</c> dialog index.
    /// A single-script NPC therefore holds one entry; an NPC such as the title expert,
    /// which owns both 称谓鉴定 and 称谓奖励, holds one entry per function and needs
    /// no further wiring. An index absent from the map is left unanswered.
    ///
    /// Split out from <c>ScriptedNpcDialogueCatalog.cs</c>, which had reached the
    /// repository's 20,000-byte file limit once this table grew. This is the only place
    /// an NPC is bound to a dialogue, and both the open packet and the click traffic
    /// route through it, so a dialogue registered here needs no handler change.
    /// </remarks>
    private static Dictionary<int, ScriptedNpcDialogue>? ResolveScriptedNpcDialogues(
        NpcSpawnDefinition npc) => npc.NpcKey switch
        {
            "Athens_083" or "Sparta_083" => ByFunction(MysteriousElderDialogue),
            "Athens_120" or "Sparta_120" => ByFunction(ProfessionMentorDialogue),
            "Athens_139" or "Sparta_139" => ByFunction(PersonalHelperDialogue),
            "Athens_072" or "Sparta_072" => ByFunction(WithTroyEntry(EventTransporterDialogue)), // W2 F-27 TROY: + 900 when troy.entry
            "Athens_059" or "Sparta_059" => ByFunction(WeddingPriestDialogue),
            "Athens_058" or "Sparta_058" => ByFunction(CupidDialogue),
            "Athens_044" or "Sparta_044" => ByFunction(ClassShifterDialogue),
            "Athens_075" or "Sparta_075" => ByFunction(FortuneTellerDialogue),
            "Athens_091" or "Sparta_091" => ByFunction(CupidsShadowDialogue),
            // MARRIAGE: Pan's Whelk Necklace, routed only while the marriage switch is on.
            // PAN CHAIN: plus the certificate draw (function 10) while GODSWAR_PAN_AVATAR_DRAW is on.
            "Pans_Labyrinth_010" => PanAvatarDialogues(),
            // PAN CHAIN: the Pan's Envoys (function 11) while GODSWAR_PAN_ENVOY is on.
            "Athens_090" or "Sparta_090" when Godswar.Server.Domain.World.Content.PanInvitationProtocol.EnvoyEnabled =>
                ByFunction(PanEnvoyDialogue),
            "Agate_009" or "Agate_010" when Godswar.Server.Domain.World.Content.PanInvitationProtocol.EnvoyEnabled =>
                ByFunction(PanTreasureLandEnvoyDialogue),
            // KELSIS (F-15): Delighted Kelsis' dog contest, routed only while npc.kelsis_contest is on.
            Godswar.Server.Domain.World.Content.KelsisContestPolicy.NpcKey when KelsisContestOn =>
                ByFunction(KelsisDialogue),
            // PAN CHAIN slice 2: the Cursed Zone envoys' part 2 while GODSWAR_PAN_CURSED_ENVOY is on.
            _ when Godswar.Server.Domain.World.Content.PanInvitationProtocol.CursedEnvoyEnabled &&
                Godswar.Server.Domain.World.Content.PanInvitationProtocol.IsCursedZoneEnvoy(npc.NpcKey) =>
                ByFunction(PanCursedZoneEnvoyDialogue),
            "Athens_109" or "Sparta_109" => ByFunction(ApothecaryDialogue),
            "Athens_138" or "Sparta_138" => ByFunction(PlasticSurgeonDialogue),
            "Athens_140" or "Sparta_140" => ByFunction(MysteryMerchantDialogue),
            "Athens_053" or "Sparta_053" => ByFunction(HalloweenEnvoyDialogue),
            // W3 seasons: the Thanksgiving Envoy exists only while its season is open (SeasonalEvents).
            "Athens_012" or "Sparta_012" => ByFunction(ThanksgivingEnvoyDialogue),
            // W3 Christmas: Santa Claus exists only while its season is open (SeasonalEvents).
            "Athens_035" or "Sparta_035" => ByFunction(SantaClausDialogue),
            "Athens_084" or "Sparta_084" => ByFunction(FestivalEnvoyDialogue),
            // W3 seasons oriental/carnival: exist only while their season is open (OrientalCarnivalSeason).
            "Athens_031" or "Sparta_025" => ByFunction(CuisineScrollDialogue, CuisineWishDialogue),
            "Athens_126" or "Sparta_126" => ByFunction(CarnivalEnvoyDialogue),
            "Athens_116" or "Sparta_116" => ByFunction(MountFeederDialogue),
            "Athens_118" or "Sparta_118" => ByFunction(RandomQuestManagerDialogue),
            "Athens_092" or "Sparta_092" => ByFunction(SpringFestivalEnvoyDialogue),
            // ROUND3 W3 Spring Festival: the Lunar Beast Slayer exists only while its season is open (SeasonalEvents).
            "Athens_112" or "Sparta_112" => ByFunction(LunarBeastSlayerDialogue),
            "Athens_038" or "Sparta_039" => ByFunction(GuildQuestSupervisorDialogue),
            "Athens_050" or "Sparta_050" => ByFunction(GuildAltarDialogue),
            "Athens_051" or "Sparta_051" => ByFunction(GuildMemberAdvisorDialogue),
            "Athens_076" or "Sparta_076" => ByFunction(WarSupplierDialogue),
            // FINALISE2: War Supplier 3 (unspawned today), the B-Gold Trader and the Cadian Generals.
            "Marathon_All_010" or "Peloponnese_All_010" => ByFunction(WarSupplierDialogue),
            "Athens_133" or "Sparta_133" => ByFunction(BGoldTraderDialogue),
            // F-27 / F-140: the redeemer's own three functions (SYS_NPC_38-40: details, exchange, refund).
            "Athens_131" or "Sparta_131" when HolyStoneRedeemerPagesOn =>
                ByFunction(HolyStoneRedeemerDialogue, HolyStoneExchangeDialogue, HolyStoneRefundDialogue),
            "Athens_131" or "Sparta_131" => ByFunction(HolyStoneRedeemerDialogue),
            "Athens_115" => ByFunction(SummerGiftEnvoyDialogue), // FINALISE2: same NPC as the captured Sparta_115
            "Marathon_All_013" or "Peloponnese_All_013" => ByFunction(CadianGeneralDialogue),
            // W3 seasons mid-autumn: both Lunar Priests (Athens_032 was unrouted; Athens_026 is Demetrius).
            "Athens_032" or "Sparta_026" => ByFunction(LunarPriestDialogue),
            "Athens_128" or "Sparta_128" => ByFunction(EasterEnvoyDialogue),
            "Athens_119" or "Sparta_119" => ByFunction(IggAnniversaryEnvoyDialogue),
            "Athens_141" or "Sparta_141" => ByFunction(FactionChangerDialogue),
            "Athens_129" or "Sparta_129" => ByFunction(RichManEnvoyDialogue),
            "Athens_130" or "Sparta_130" => ByFunction(SacredSealerDialogue),
            // W3 seasons easter: the Easter Bunny Envoy (NpcFunRelive.lua, 51); exists only while Easter is open.
            "Athens_117" or "Sparta_117" => ByFunction(EasterBunnyEnvoyDialogue),
            // Both title functions belong to this one NPC, so the open packet advertises
            // 12 and 13 and the click is routed by the echoed dialog index.
            "Athens_071" or "Sparta_071" =>
                ByFunction(TitleIdentificationDialogue, TitleRewardDialogue),
            "Athens_078" or "Sparta_078" => ByFunction(FitnessTrainerDialogue),
            "Athens_079" or "Sparta_079" => ByFunction(FitnessTrainerDialogue),
            "Athens_080" or "Sparta_080" => ByFunction(FitnessTrainerDialogue),
            "Athens_081" or "Sparta_081" => ByFunction(FitnessTrainerDialogue),
            "Athens_082" or "Sparta_082" => ByFunction(FitnessTrainerDialogue),
            // EVENTSNPC: trainers 1-5 and the Ancient Relics exist only while their event is on.
            _ when Godswar.Server.Application.EventsNpc.EventsNpcPolicy.TrainerNumber(npc.NpcKey) is >= 1 and <= 5 =>
                ByFunction(FitnessTrainerDialogue),
            _ when Godswar.Server.Application.EventsNpc.EventsNpcPolicy.RelicNumber(npc.NpcKey) > 0 =>
                ByFunction(AncientRelicsDialogue),
            "Athens_125" or "Sparta_125" => ByFunction(TrojanWarCoordinatorDialogue),
            // W3 seasons may day: the GENERATED May Day Envoy (55) with the Fertiliser Supplier (56); only while open.
            "Athens_MayDay" or "Sparta_MayDay" => ByFunction(MayDayEnvoyDialogue, FertiliserSupplierDialogue),
            // W2 F-27 TROY slice 2: map 44 NPCs placed by troy-spawns.sql. Agamemnon (Troy_003, whose texts the
            // coordinator menu draws) and the Trojan War Transporter at the beach (Troy_001) share the coordinator menu;
            // the chests and the two transporters answer from GameClientHandler.TroyBosses.cs.
            "Troy_001" or "Troy_003" => ByFunction(TrojanWarCoordinatorDialogue),
            "Troy_002" or "Troy_006" or "Troy_007" or "Troy_008" or "Troy_009" or "Troy_010" or "Troy_011" =>
                ByFunction(TroyMapNpcDialogue),
            // Captured on 2026-09-28. Sparta_115 owns function 66 and Sparta_124
            // function 67; the latter shares that number with Sparta_125 above,
            // which is exactly why each NPC keeps its own entry - the routing
            // table is keyed by the npc, so the two windows cannot borrow each
            // other's buttons.
            "Sparta_115" => ByFunction(SummerGiftEnvoyDialogue),
            "Sparta_124" => ByFunction(TroyEventAwarderDialogue),
            // W2 F-27 TROY: the Athens awarder is the same NPC (Event Awarder, same text and place, 147,-36); it was
            // description-only. Routed only while troy.awards is on, so the off switch keeps it as captured.
            "Athens_124" when Godswar.Server.Application.Troy.TroyEvent.AwardsOn => ByFunction(TroyEventAwarderDialogue),
            // The battlefield awarder. Function 3 is NPC_FLAG_SYS_WAR in the
            // client's own table; the reference advertised exactly that number on
            // 2026-09-28, and the quest page cannot take the click first because
            // the gate only fires for a carried starter-chain quest this npc
            // responds to.
            //
            // Athens_073 is a different actor with its own appearance and id, but
            // it is the same endpoint: both capitals' awarders are driven by
            // NpcFunWar.lua, so they share this one dialogue and each keeps its
            // own identity through the routing table's npc key.
            "Athens_073" or "Sparta_073" =>
                ByFunction(BattlefieldAwarderDialogue),
            "Labyrinth_006" or "Labyrint2_006" => ByFunction(ZeusEnvoyDialogue),
            "WarField_003" or "WarField_008" => ByFunction(BattlefieldCrierDialogue),
            // BATTLEFIELD: the Pindus Quartermasters and Fortune Altars (NpcFunWar.lua,
            // function 3). They are not in the published map 38 roster; when they are
            // placed (gm_npc_spawns, pindus-npcs-generated.sql) their clicks are
            // answered by GameClientHandler.PindusSupply.cs.
            "WarField_002" or "WarField_007" => ByFunction(PindusQuartermasterDialogue),
            "WarField_005" or "WarField_010" or "WarField_011" or "WarField_012" or "WarField_013" or
                "WarField_014" or "WarField_015" or "WarField_016" or "WarField_017" or "WarField_018" or
                "WarField_019" or "WarField_020" => ByFunction(PindusFortuneAltarDialogue),
            _ => null
        };

    /// <summary>
    /// Indexes scripted dialogues by the client function number they own, so an NPC with
    /// several functions adds arguments here and nothing else.
    /// </summary>
    private static Dictionary<int, ScriptedNpcDialogue> ByFunction(
        params ScriptedNpcDialogue[] dialogues) =>
        dialogues.ToDictionary(dialogue => dialogue.FunctionNumber);
}
