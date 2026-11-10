using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Godswar.Server.Domain.World.Content;

internal enum CapitalNpcServiceKind
{
    ExchangeMentor,
    TeachingManager,
    BoundGoldVendor,
    BindingGoldShop,
    FestivalEnvoy,
    SacredSealer,
    HolyStoneRedeemer,
    HalloweenEnvoy,
    PetMerchant,
    SkillVendor,
    PropsVendor,
    PointExchanger,
    PrizeChest,
    LevelSealer,

    /// <summary>
    /// The capital mall, opened from the same NPC pair in both camps.
    /// </summary>
    /// <remarks>
    /// Captured on the reference server in Athens (npc 5212): the client opens the
    /// npc with flags 0x200, picks dialog index 16, and the server answers with
    /// the window frame 10021 plus 10201/10248/10199, all keyed by window id 765.
    /// The second service on the same npc is dialog index 24.
    /// </remarks>
    Mall,

    // The Athens merchant quarter, captured 2026-09-24: fourteen npcs that
    // answer a click with function number zero and then stream their stock
    // straight after the client's 10068 open request. Nothing distinguishes them
    // from each other at the protocol level - only the catalog differs - so each
    // keeps its own kind and reads as the role its npc_text_templates name
    // gives it. Eight price in bound gold, one in gold and five in silver,
    // which is the currency byte each catalog's frames carry.
    AthensWarriorEquipmentVendor,
    AthensScholarEquipmentVendor,
    AthensJewelryEquipmentVendor,
    AthensArmorEquipmentVendor,
    AthensWarriorSupplier,
    AthensSkillMerchant,
    AthensArmorMerchant,
    AthensScholarSupplier,
    AthensJewelryMerchant,
    AthensAlchemyRecipeVendor,
    AthensIngredientsVendor,
    AthensForgingRecipeVendor,
    AthensMythcraftingRecipeVendor,
    AthensScholarshipRecipeVendor,

    /// <summary>
    /// The Lelantine Farm's Quartermaster, a shop rather than a dialogue.
    /// </summary>
    /// <remarks>
    /// Captured <c>S2C 10067 {5616, 4, 0, "Lelantine_Farm_004"}</c> at
    /// <c>2026-09-26 21:01:31</c>: flags 4 is the shop page, and the client
    /// followed it with an <c>10068</c> page request that the reference server
    /// answered with a ten-listing silver catalogue of the five tuck nets.
    /// </remarks>
    LelantineFarmQuartermaster,

    // The Sparta merchant quarter, captured 2026-09-28 (session c202c633,
    // local 01:52-02:09): ten merchants that answer a click with function
    // number zero and then stream their stock after the client's 10068 page
    // request, exactly like the Athens quarter. Two keys share one shelf, so
    // the seven kinds below cover ten actors. Every one of them prices in
    // silver - the user's requirement for this port, and the price the capture
    // itself carries (each listing costs the item's own silver price).
    SpartaWarriorEquipmentMerchant,
    SpartaScholarEquipmentMerchant,
    SpartaSkillMerchant,
    SpartaArmorMerchant,
    SpartaJewelryMerchant,
    SpartaPropsMerchant,
    SpartaPetMerchant
}

internal enum CapitalNpcShopCurrency
{
    Gold,
    BindingGold,
    Silver,

    // Point Exchanger currencies. The stock client resolves a record's cost from
    // its own tables and exposes three separate balances for this vendor:
    // Prestige/Honor, HardPoint/Point, and MedalPoint (Thearchy Medal). Honor
    // reuses the existing character honor balance; Point and Medal are new.
    Honor,
    Point,
    Medal
}

internal readonly record struct CapitalNpcShopBalances(
    int Silver,
    int Gold,
    int BindingGold,
    int Honor = 0,
    int Point = 0,
    int Medal = 0)
{
    public int Get(CapitalNpcShopCurrency currency) =>
        currency switch
        {
            CapitalNpcShopCurrency.Silver => Silver,
            CapitalNpcShopCurrency.Gold => Gold,
            CapitalNpcShopCurrency.BindingGold => BindingGold,
            CapitalNpcShopCurrency.Honor => Honor,
            CapitalNpcShopCurrency.Point => Point,
            CapitalNpcShopCurrency.Medal => Medal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(currency),
                currency,
                "Unknown capital shop currency.")
        };
}

internal static class CapitalNpcServiceProtocol
{
    public const int PurchasePayloadBytes = 20;
    public const int ExchangeDialogIndex = 2;
    public const int DescriptionOpenFlags = 0;
    public const int ShopOpenFlags = 4;
    public const int PrizeChestOpenFlags = 0x40;
    public const int LevelSealerDialogIndex = 116;
    public const int LevelSealerDescriptionSubId = 101;
    public const int LevelSealerSealSubId = 102;
    public const int LevelSealerUnsealSubId = 103;
    public const int LevelSealerInsufficientFundsSubId = 104;
    public const int LevelSealerUnavailableSubId = 105;
    public const int LevelSealerSealedSubId = 106;
    public const int LevelSealerUnsealedSubId = 107;
    public const int LevelSealerAlreadyUnsealedSubId = 108;
    public const int LevelSealerAlreadySealedSubId = 109;

    public static ImmutableArray<int> ExchangeInitialMenu { get; } =
        [49, 50, 51];

    public static bool TryResolve(
        NpcSpawnDefinition npc,
        out CapitalNpcServiceKind service) =>
        TryResolve(npc.NpcKey, npc.InteractionId, out service);

    public static bool TryResolve(
        string npcKey,
        uint interactionId,
        out CapitalNpcServiceKind service)
    {
        var resolved = (npcKey, interactionId) switch
        {
            ("Sparta_052", 5049u) or ("Athens_052", 5190u) =>
                (CapitalNpcServiceKind?)CapitalNpcServiceKind.ExchangeMentor,
            ("Sparta_069", 5066u) or ("Athens_069", 5207u) =>
                CapitalNpcServiceKind.TeachingManager,
            // Athens_087 is carried by the 2026-09-24 capture as npc 5225, so it
            // now takes the reference's own interaction id like every other
            // captured city npc.
            ("Sparta_087", 5084u) or ("Athens_087", 5225u) =>
                CapitalNpcServiceKind.BoundGoldVendor,
            ("Sparta_068", 5065u) or ("Athens_068", 5206u) =>
                CapitalNpcServiceKind.BindingGoldShop,
            ("Sparta_084", 5081u) or ("Athens_084", 5222u) =>
                CapitalNpcServiceKind.FestivalEnvoy,
            ("Sparta_130", 5127u) or ("Athens_130", 5268u) =>
                CapitalNpcServiceKind.SacredSealer,
            ("Sparta_131", 5128u) or ("Athens_131", 5269u) =>
                CapitalNpcServiceKind.HolyStoneRedeemer,
            ("Sparta_053", 5050u) or ("Athens_053", 5191u) =>
                CapitalNpcServiceKind.HalloweenEnvoy,
            // The Athens pet merchant keeps the older, larger shelf. Sparta's own
            // pet merchant was captured on 2026-09-28 streaming a different,
            // silver shelf, so it has its own kind below.
            ("Athens_089", 5227u) =>
                CapitalNpcServiceKind.PetMerchant,
            // The newbie maps' skill and props vendors. Sparta's city actors used
            // to share these kinds; the 2026-09-28 capture shows they stream their
            // own, smaller shelves, so they now have their own kinds below and
            // these two arms keep only the Athens and newbie endpoints.
            ("Athens_036", 5175u) or
            ("Sparta_Newbie_004", 46565u) or
            // The capture places the Athens skill vendor on npc 5285 on map 2.
            // It used to be keyed by the published ini id 54453, which
            // CapturedNpcPlacementPolicy overwrites, so the shop never answered.
            ("Athens_Newbie_004", 5285u) =>
                CapitalNpcServiceKind.SkillVendor,
            ("Athens_021", 5161u) =>
                CapitalNpcServiceKind.PropsVendor,
            ("Sparta_077", 5074u) or ("Athens_077", 5215u) =>
                CapitalNpcServiceKind.PointExchanger,
            ("Sparta_123", 5120u) or ("Athens_123", 5261u) =>
                CapitalNpcServiceKind.PrizeChest,
            ("Sparta_142", 5139u) or ("Athens_142", 5280u) =>
                CapitalNpcServiceKind.LevelSealer,
            ("Sparta_074", 5071u) or ("Athens_074", 5212u) =>
                CapitalNpcServiceKind.Mall,
            // The Athens merchant quarter. Each key here is the npc the capture
            // actually streamed stock from, and CapturedNpcPlacementPolicy gives
            // that npc the capture's own object and interaction id, so the id in
            // this arm is the one the client echoes back.
            ("Athens_026", 5166u) =>
                CapitalNpcServiceKind.AthensWarriorEquipmentVendor,
            ("Athens_027", 5167u) =>
                CapitalNpcServiceKind.AthensScholarEquipmentVendor,
            ("Athens_028", 5168u) =>
                CapitalNpcServiceKind.AthensJewelryEquipmentVendor,
            ("Athens_029", 5169u) =>
                CapitalNpcServiceKind.AthensArmorEquipmentVendor,
            ("Athens_096", 5234u) =>
                CapitalNpcServiceKind.AthensWarriorSupplier,
            ("Athens_099", 5237u) =>
                CapitalNpcServiceKind.AthensSkillMerchant,
            ("Athens_102", 5240u) =>
                CapitalNpcServiceKind.AthensArmorMerchant,
            ("Athens_107", 5245u) =>
                CapitalNpcServiceKind.AthensScholarSupplier,
            ("Athens_108", 5246u) =>
                CapitalNpcServiceKind.AthensJewelryMerchant,
            // ROUND3 PROF soul stones (2026-11-09): Sparta's five life-skill
            // vendors (Sparta_121/122/135/136/137, same names, greetings and
            // positions as Athens) were never captured and answered nothing, so
            // Spartans could not buy a single recipe, vessel or hammer. GENERATED:
            // they sell the Athens shelves (same items, prices and currency); the
            // npc id is patched at egress. The ids are the captured map-0 ones the
            // client echoes back (CapturedNpcPlacements: 5118/5119/5132/5133/5134).
            ("Athens_121", 5259u) or ("Sparta_121", 5118u) =>
                CapitalNpcServiceKind.AthensAlchemyRecipeVendor,
            ("Athens_122", 5260u) or ("Sparta_122", 5119u) =>
                CapitalNpcServiceKind.AthensIngredientsVendor,
            ("Athens_135", 5273u) or ("Sparta_135", 5132u) =>
                CapitalNpcServiceKind.AthensForgingRecipeVendor,
            ("Athens_136", 5274u) or ("Sparta_136", 5133u) =>
                CapitalNpcServiceKind.AthensMythcraftingRecipeVendor,
            ("Athens_137", 5275u) or ("Sparta_137", 5134u) =>
                CapitalNpcServiceKind.AthensScholarshipRecipeVendor,
            // The Lelantine Farm's Quartermaster. The farm roster places these
            // two under the capture's own object ids, so the ids in this arm are
            // the ones the client echoes back.
            ("Lelantine_Farm_004", 5616u) or
            ("Lelantine_Farm_007", 5621u) =>
                CapitalNpcServiceKind.LelantineFarmQuartermaster,
            // The Sparta merchant quarter, captured 2026-09-28 (session
            // c202c633). Map 0 is published from the capture already, so
            // CapturedNpcPlacementPolicy leaves its rows alone and the id the
            // client echoes back is the one npc_spawn_definitions carries -
            // which is why these arms use the published ids (Sparta_028 is
            // 42888, not the reference's own 5026) and not the capture's.
            ("Sparta_028", 42_888u) or ("Sparta_096", 5_093u) =>
                CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant,
            ("Sparta_029", 5_026u) or ("Sparta_107", 5_104u) =>
                CapitalNpcServiceKind.SpartaScholarEquipmentMerchant,
            // Sparta_034 and Sparta_036 used to read as the newbie maps' skill
            // and props vendors; the capture shows the city actors stream their
            // own, smaller shelves, so they keep their own kinds now. The
            // Athens and newbie endpoints keep SkillVendor/PropsVendor.
            ("Sparta_034", 44_345u) or ("Sparta_099", 5_096u) =>
                CapitalNpcServiceKind.SpartaSkillMerchant,
            ("Sparta_037", 5_034u) =>
                CapitalNpcServiceKind.SpartaArmorMerchant,
            ("Sparta_038", 5_035u) =>
                CapitalNpcServiceKind.SpartaJewelryMerchant,
            ("Sparta_036", 5_033u) =>
                CapitalNpcServiceKind.SpartaPropsMerchant,
            // The Athens pet merchant keeps the older, larger shelf; the Sparta
            // one is the silver shelf the capture streamed from npc 5086.
            ("Sparta_089", 5_086u) =>
                CapitalNpcServiceKind.SpartaPetMerchant,
            _ => null
        };

        service = resolved.GetValueOrDefault();
        return resolved.HasValue;
    }

    /// <summary>The dialog index the mall's own window is opened from.</summary>
    public const int MallDialogIndex = 16;

    /// <summary>The mall's second service on the same npc, captured at index 24.</summary>
    public const int MallSecondDialogIndex = 24;

    /// <summary>
    /// The mall's sub-menu ids, in the order the reference answered them.
    /// </summary>
    public static ImmutableArray<int> MallMenu { get; } = [101, 201];

    /// <summary>The second service's sub-menu ids.</summary>
    public static ImmutableArray<int> MallSecondMenu { get; } = [101, 1, 2];

    /// <summary>The page the second service answered once its first choice was made.</summary>
    public static ImmutableArray<int> MallSecondPage { get; } = [601];

    /// <summary>
    /// The page the reference answered right after it pushed the mall window frames:
    /// <c>S2C 10070 {5212, 16, 100}</c> at 22:09:50.561, 281 ms behind the frames.
    /// </summary>
    public static ImmutableArray<int> MallOpenPage { get; } = [100];

    /// <summary>
    /// Window flags the mall npc opens with. Captured as 0x200, where the ordinary
    /// capital shops advertise 4 and the quest page 3.
    /// </summary>
    public const int MallOpenFlags = 0x200;

    /// <summary>
    /// What the function-key mall charges. The captured catalog carries only the
    /// unit price (record <c>+68</c>), so the balance it is drawn from is the one
    /// the mall spends: gold. Every other capital shop charges silver or binding
    /// gold, which is why the mall needs its own entry here.
    /// </summary>
    public const CapitalNpcShopCurrency MallCurrency =
        CapitalNpcShopCurrency.Gold;

    public static bool IsShop(CapitalNpcServiceKind service) =>
        service is
            CapitalNpcServiceKind.BoundGoldVendor or
            CapitalNpcServiceKind.BindingGoldShop or
            CapitalNpcServiceKind.PetMerchant or
            CapitalNpcServiceKind.SkillVendor or
            CapitalNpcServiceKind.PointExchanger or
            CapitalNpcServiceKind.PropsVendor or
            CapitalNpcServiceKind.AthensWarriorEquipmentVendor or
            CapitalNpcServiceKind.AthensScholarEquipmentVendor or
            CapitalNpcServiceKind.AthensJewelryEquipmentVendor or
            CapitalNpcServiceKind.AthensArmorEquipmentVendor or
            CapitalNpcServiceKind.AthensWarriorSupplier or
            CapitalNpcServiceKind.AthensSkillMerchant or
            CapitalNpcServiceKind.AthensArmorMerchant or
            CapitalNpcServiceKind.AthensScholarSupplier or
            CapitalNpcServiceKind.AthensJewelryMerchant or
            CapitalNpcServiceKind.AthensAlchemyRecipeVendor or
            CapitalNpcServiceKind.AthensIngredientsVendor or
            CapitalNpcServiceKind.AthensForgingRecipeVendor or
            CapitalNpcServiceKind.AthensMythcraftingRecipeVendor or
            CapitalNpcServiceKind.AthensScholarshipRecipeVendor or
            CapitalNpcServiceKind.LelantineFarmQuartermaster or
            CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant or
            CapitalNpcServiceKind.SpartaScholarEquipmentMerchant or
            CapitalNpcServiceKind.SpartaSkillMerchant or
            CapitalNpcServiceKind.SpartaArmorMerchant or
            CapitalNpcServiceKind.SpartaJewelryMerchant or
            CapitalNpcServiceKind.SpartaPropsMerchant or
            CapitalNpcServiceKind.SpartaPetMerchant;

    public static NpcSpawnDefinition ApplyCapturedSpawnCompatibility(
        NpcSpawnDefinition npc) =>
        (npc.MapId, npc.NpcKey, npc.InteractionId) switch
        {
            (0, "Sparta_142", 5139u) => npc with
            {
                TemplateKey = "Sparta_142_Hallo",
                X = 120.007515f,
                Z = -138.16507f,
                AppearanceType = 17u,
                Facing = 3.071875f
            },
            (1, "Athens_142", 5281u) => npc with
            {
                TemplateKey = "Athens_142_Hallo",
                X = 120.007515f,
                Z = -138.16507f,
                AppearanceType = 65_809u,
                Facing = 3.071875f
            },
            _ => npc
        };

    public static bool TryGetDialogueRoutes(
        NpcSpawnDefinition npc,
        out IReadOnlyList<NpcDialogueRouteDefinition> routes)
    {
        routes = [];
        if (!TryResolve(npc, out var service))
        {
            return false;
        }

        routes = service switch
        {
            CapitalNpcServiceKind.FestivalEnvoy =>
                [Route(npc, 28, [2, 3, 4, 5])],
            CapitalNpcServiceKind.SacredSealer =>
                [Route(npc, 95, [1])],
            CapitalNpcServiceKind.HolyStoneRedeemer =>
                [Route(npc, 38, [0, 101])],
            CapitalNpcServiceKind.HalloweenEnvoy =>
                [
                    Route(npc, 95, [0, 1, 2, 3, 4, 5]),
                    Route(npc, 113, [0, 5, 6, 7, 8, 8001], routeOrder: 1)
                ],
            CapitalNpcServiceKind.LevelSealer =>
                [Route(
                    npc,
                    LevelSealerDialogIndex,
                    [
                        LevelSealerDescriptionSubId,
                        LevelSealerSealSubId,
                        LevelSealerUnsealSubId
                    ])],
            _ => []
        };
        return routes.Count != 0;
    }

    public static bool TryGetInitialDialogueReply(
        CapitalNpcServiceKind service,
        out int responseDialogIndex,
        out int[] responseSubIds)
    {
        (responseDialogIndex, responseSubIds) = service switch
        {
            CapitalNpcServiceKind.FestivalEnvoy =>
                (28, new[] { 2, 3, 4, 5 }),
            CapitalNpcServiceKind.HolyStoneRedeemer =>
                (38, new[] { 0, 101 }),
            CapitalNpcServiceKind.HalloweenEnvoy =>
                (113, new[] { 0, 1, 2, 3, 4, 5 }),
            CapitalNpcServiceKind.LevelSealer =>
                (
                    LevelSealerDialogIndex,
                    new[]
                    {
                        LevelSealerDescriptionSubId,
                        LevelSealerSealSubId,
                        LevelSealerUnsealSubId
                    }),
            _ => (0, Array.Empty<int>())
        };
        return responseSubIds.Length != 0;
    }

    public static bool TryGetDialogueReply(
        CapitalNpcServiceKind service,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out int responseDialogIndex,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        (responseDialogIndex, responseSubIds) =
            (service, dialogIndex, subId, ReadPath(arguments)) switch
            {
                (CapitalNpcServiceKind.FestivalEnvoy, 28, 5, "") =>
                    (28, new[] { 501, 502, 4, 5 }),
                (CapitalNpcServiceKind.FestivalEnvoy, 28, 5, "501") =>
                    (28, new[] { 511, 3, 4, 5 }),
                (CapitalNpcServiceKind.HalloweenEnvoy, 113, 4, "") =>
                    (113, new[] { 0, 5, 6, 7, 8, 8001 }),
                _ => (0, Array.Empty<int>())
            };
        return responseSubIds.Length != 0;
    }

    public static bool IsWeekendExperienceClaim(
        CapitalNpcServiceKind service,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return service == CapitalNpcServiceKind.FestivalEnvoy &&
            dialogIndex == 28 &&
            subId == 5 &&
            ReadPath(arguments) == "501";
    }

    public static bool TryGetLevelSealerChange(
        CapitalNpcServiceKind service,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out bool desiredSealed)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        desiredSealed = subId == LevelSealerSealSubId;
        return service == CapitalNpcServiceKind.LevelSealer &&
            dialogIndex == LevelSealerDialogIndex &&
            subId is LevelSealerSealSubId or LevelSealerUnsealSubId &&
            ReadPath(arguments) == string.Empty;
    }

    private static NpcDialogueRouteDefinition Route(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int[] menu,
        int routeOrder = 0) =>
        new(
            npc.NpcKey,
            npc.NpcKey,
            dialogIndex,
            NpcDialogueBehavior.CapturedCapital,
            menu.ToImmutableArray())
        {
            RouteOrder = routeOrder
        };

    private static string? ReadPath(IReadOnlyList<int> arguments)
    {
        var path = new List<int>();
        var padding = false;
        foreach (var argument in arguments)
        {
            if (argument == -1)
            {
                padding = true;
                continue;
            }
            if (padding)
            {
                return null;
            }
            path.Add(argument);
        }
        return string.Join('/', path);
    }

    public static bool TryGetShopCurrency(
        CapitalNpcServiceKind service,
        out CapitalNpcShopCurrency currency)
    {
        currency = service switch
        {
            CapitalNpcServiceKind.BoundGoldVendor =>
                CapitalNpcShopCurrency.Gold,
            CapitalNpcServiceKind.BindingGoldShop or
            CapitalNpcServiceKind.SkillVendor or
            CapitalNpcServiceKind.PointExchanger =>
                CapitalNpcShopCurrency.BindingGold,
            CapitalNpcServiceKind.PetMerchant or
            // The farm quartermaster's captured frames carry currency byte 1,
            // which is silver, so it prices in the character's own silver.
            CapitalNpcServiceKind.LelantineFarmQuartermaster =>
                CapitalNpcShopCurrency.Silver,
            // Athens merchant quarter. The charge each one advertises is the
            // currency byte its captured frames carry, so this table restates the
            // capture rather than choosing a balance.
            CapitalNpcServiceKind.AthensWarriorEquipmentVendor or
            CapitalNpcServiceKind.AthensScholarEquipmentVendor or
            CapitalNpcServiceKind.AthensJewelryEquipmentVendor or
            CapitalNpcServiceKind.AthensArmorEquipmentVendor or
            CapitalNpcServiceKind.AthensWarriorSupplier or
            CapitalNpcServiceKind.AthensSkillMerchant or
            CapitalNpcServiceKind.AthensArmorMerchant or
            CapitalNpcServiceKind.AthensScholarSupplier or
            CapitalNpcServiceKind.AthensIngredientsVendor =>
                CapitalNpcShopCurrency.BindingGold,
            CapitalNpcServiceKind.AthensJewelryMerchant =>
                CapitalNpcShopCurrency.Gold,
            CapitalNpcServiceKind.AthensAlchemyRecipeVendor or
            CapitalNpcServiceKind.AthensForgingRecipeVendor or
            CapitalNpcServiceKind.AthensMythcraftingRecipeVendor or
            CapitalNpcServiceKind.AthensScholarshipRecipeVendor =>
                CapitalNpcShopCurrency.Silver,
            // The Sparta merchant quarter captured on 2026-09-28. The user's
            // requirement for this port is that every item on these shelves is
            // bought with silver, which is also the price the capture carries:
            // each equipment listing costs the item's own silver price in the
            // client's ItemBaseAttribute.xml. The captured frames' currency byte
            // (0x04 on seven of them) is rewritten to the client's silver code
            // when the catalogue is loaded, so the client draws the silver
            // balance and the purchase charges the same wallet.
            CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant or
            CapitalNpcServiceKind.SpartaScholarEquipmentMerchant or
            CapitalNpcServiceKind.SpartaSkillMerchant or
            CapitalNpcServiceKind.SpartaArmorMerchant or
            CapitalNpcServiceKind.SpartaJewelryMerchant or
            CapitalNpcServiceKind.SpartaPropsMerchant or
            CapitalNpcServiceKind.SpartaPetMerchant =>
                CapitalNpcShopCurrency.Silver,
            _ => default
        };
        return service is
            CapitalNpcServiceKind.BoundGoldVendor or
            CapitalNpcServiceKind.BindingGoldShop or
            CapitalNpcServiceKind.PetMerchant or
            CapitalNpcServiceKind.SkillVendor or
            CapitalNpcServiceKind.PointExchanger or
            CapitalNpcServiceKind.AthensWarriorEquipmentVendor or
            CapitalNpcServiceKind.AthensScholarEquipmentVendor or
            CapitalNpcServiceKind.AthensJewelryEquipmentVendor or
            CapitalNpcServiceKind.AthensArmorEquipmentVendor or
            CapitalNpcServiceKind.AthensWarriorSupplier or
            CapitalNpcServiceKind.AthensSkillMerchant or
            CapitalNpcServiceKind.AthensArmorMerchant or
            CapitalNpcServiceKind.AthensScholarSupplier or
            CapitalNpcServiceKind.AthensJewelryMerchant or
            CapitalNpcServiceKind.AthensAlchemyRecipeVendor or
            CapitalNpcServiceKind.AthensIngredientsVendor or
            CapitalNpcServiceKind.AthensForgingRecipeVendor or
            CapitalNpcServiceKind.AthensMythcraftingRecipeVendor or
            CapitalNpcServiceKind.AthensScholarshipRecipeVendor or
            CapitalNpcServiceKind.LelantineFarmQuartermaster or
            CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant or
            CapitalNpcServiceKind.SpartaScholarEquipmentMerchant or
            CapitalNpcServiceKind.SpartaSkillMerchant or
            CapitalNpcServiceKind.SpartaArmorMerchant or
            CapitalNpcServiceKind.SpartaJewelryMerchant or
            CapitalNpcServiceKind.SpartaPropsMerchant or
            CapitalNpcServiceKind.SpartaPetMerchant;
    }

    public static bool TryGetShopCurrency(
        byte shopType,
        out CapitalNpcShopCurrency currency)
    {        currency = shopType switch
        {
            2 => CapitalNpcShopCurrency.Gold,
            3 => CapitalNpcShopCurrency.Silver,
            4 => CapitalNpcShopCurrency.BindingGold,
            // Point Exchanger frames. The captured reference frames carried 0x04
            // for every category, which is why the whole vendor priced in
            // B-Gold; these codes carry the three balances the stock client
            // actually shows for this vendor.
            5 => CapitalNpcShopCurrency.Honor,
            6 => CapitalNpcShopCurrency.Point,
            7 => CapitalNpcShopCurrency.Medal,
            _ => default
        };
        return shopType is 2 or 3 or 4 or 5 or 6 or 7;
    }

    /// <summary>
    /// Resolves which balance a Point Exchanger listing charges.
    /// </summary>
    /// <remarks>
    /// The reference server advertised its whole catalog under one currency
    /// code, so the stock client showed a single requirement for a vendor that
    /// really charges three balances. The mapping is not carried by opcode
    /// 10071 at all: the three groups' records have byte-identical nonzero
    /// field layouts, and the client ships no table for it either. It is
    /// server-owned data, keyed here by the listing marker's high byte.
    /// <para>
    /// Honor reuses the character honor balance; point and medal are their own.
    /// </para>
    /// </remarks>
    public static bool TryGetPointExchangerCurrency(
        uint itemId,
        out CapitalNpcShopCurrency currency)
    {
        currency = itemId switch
        {
            // 0x04 group.
            9940 or 9941 or 9942 or 9943 or 9944 or 9945 or 9946 or 9947 or
            9948 or 9949 or 9963 or 9991 or 4223 or 4233 or
            9030 or 9031 or 9040 or 9041 or 9042 or 9050 or 9021 or
            4266 or 4267 or 4269 =>
                CapitalNpcShopCurrency.Honor,
            // 0x05 group.
            4213 or
            3931 or 3962 or
            9950 or 9951 or 9952 or 9953 or 9954 or 9955 or 9956 or 9957 or
            10090 or 10100 or 10083 or 4502 or 4524 or 3819 or 11010 =>
                CapitalNpcShopCurrency.Point,
            // 0x06 group.
            9958 or 9959 or 14069 or 4193 =>
                CapitalNpcShopCurrency.Medal,
            _ => default
        };
        return currency != default;
    }

    public static NpcDialogueRouteDefinition ExchangeRoute(
        NpcSpawnDefinition npc)
    {
        if (!TryResolve(npc, out var service) ||
            service != CapitalNpcServiceKind.ExchangeMentor)
        {
            throw new ArgumentException(
                "NPC is not an exact capital Exchange Mentor endpoint.",
                nameof(npc));
        }

        return new NpcDialogueRouteDefinition(
            npc.NpcKey,
            npc.NpcKey,
            ExchangeDialogIndex,
            NpcDialogueBehavior.CreditExchange,
            ExchangeInitialMenu);
    }

    public static bool TryGetExchangePage(
        int subId,
        out int[] pageSubIds)
    {
        pageSubIds = subId switch
        {
            50 => [311, 312, 313],
            51 => [314, 315, 316],
            _ => []
        };
        return pageSubIds.Length != 0;
    }

    public static bool TryParsePurchase(
        ReadOnlySpan<byte> payload,
        out CapitalNpcShopPurchaseIntent intent)
    {
        intent = default;
        if (payload.Length != PurchasePayloadBytes)
        {
            return false;
        }

        var candidate = new CapitalNpcShopPurchaseIntent(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16)));
        if (candidate.NpcId == 0 ||
            candidate.Category is < 0 or > byte.MaxValue ||
            candidate.ListingIndex < 0 ||
            candidate.Quantity is < 1 or > byte.MaxValue ||
            candidate.ItemId == 0)
        {
            return false;
        }

        intent = candidate;
        return true;
    }
}

internal readonly record struct CapitalNpcShopPurchaseIntent(
    uint NpcId,
    int Category,
    int ListingIndex,
    int Quantity,
    uint ItemId);
