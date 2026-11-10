using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CapitalNpcServiceProtocolChecks
{
    public const string CheckName =
        "Captured capital NPC dialogue and shop catalogs";

    public static Task RunAsync()
    {
        CheckEndpointsAndExchangePages();
        CheckOpenPackets();
        CheckDescriptionOnlyNpcOpen();
        CheckShopCatalogs();
        CheckCapturedDialogRoutes();
        CheckSpartaMerchantQuarterRestored();
        LevelSealerSpawnCompatibilityChecks.Run();
        CheckPurchaseIntentAndOfferAuthority();
        CheckBindingGoldMigration();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The Wishing Pool and the other published dialogue-only NPCs own text but
    /// no extended function, so the dialog-open handler answers them with the
    /// description-only window instead of an empty reply.
    /// </summary>
    private static void CheckDescriptionOnlyNpcOpen()
    {
        // Sparta_074 / Athens_074 are the Wishing Pool pair; Sparta_083 /
        // Athens_083 are the Mysterious Elder that awards the Lost Book scroll.
        (string Key, uint Id)[] dialogueOnly =
        [
            ("Sparta_074", 5071),
            ("Athens_074", 5213),
            ("Sparta_083", 5080),
            ("Athens_083", 5222)
        ];

        var published = NpcContentBaselineV1.LoadDefinitions();
        foreach (var (key, id) in dialogueOnly)
        {
            var npc = published.Single(candidate => candidate.NpcKey == key);
            Check.True(
                npc.InteractionId == id,
                $"dialogue-only NPC {key} keeps its published interaction id");
        }

        foreach (var (key, id) in dialogueOnly)
        {
            var packet = PacketBuilder.NpcDescriptionDialogOpenAck(id, key);
            Check.True(
                IsOpen(packet, id, flags: 0, packedDialog: 0) &&
                PacketText.ReadFixedAscii(packet, 16, 32) == key,
                $"Wishing Pool style NPC {key} opens the description window " +
                "with its own client script key");
        }

        // The description window must never advertise a function index: the
        // client would try to dispatch a Lua function that has no handler for
        // this NPC.
        var wishingPool = PacketBuilder.NpcDescriptionDialogOpenAck(
            5071,
            "Sparta_074");
        Check.True(
            BinaryPrimitives.ReadInt32LittleEndian(wishingPool.AsSpan(12)) == 0,
            "the description-only window advertises no extended function");
    }

    private static void CheckEndpointsAndExchangePages()
    {
        (string Key, uint Id, CapitalNpcServiceKind Service)[] endpoints =
        [
            ("Sparta_052", 5049, CapitalNpcServiceKind.ExchangeMentor),
            ("Athens_052", 5190, CapitalNpcServiceKind.ExchangeMentor),
            ("Sparta_069", 5066, CapitalNpcServiceKind.TeachingManager),
            ("Athens_069", 5207, CapitalNpcServiceKind.TeachingManager),
            ("Sparta_087", 5084, CapitalNpcServiceKind.BoundGoldVendor),
            // Athens_087 is carried by the 2026-09-24 capture as npc 5225, so it
            // takes the reference's own object and interaction id like every other
            // captured npc instead of being renumbered above the captured range.
            ("Athens_087", 5225, CapitalNpcServiceKind.BoundGoldVendor),
            ("Sparta_068", 5065, CapitalNpcServiceKind.BindingGoldShop),
            ("Athens_068", 5206, CapitalNpcServiceKind.BindingGoldShop),
            ("Sparta_084", 5081, CapitalNpcServiceKind.FestivalEnvoy),
            ("Athens_084", 5222, CapitalNpcServiceKind.FestivalEnvoy),
            ("Sparta_130", 5127, CapitalNpcServiceKind.SacredSealer),
            ("Athens_130", 5268, CapitalNpcServiceKind.SacredSealer),
            ("Sparta_131", 5128, CapitalNpcServiceKind.HolyStoneRedeemer),
            ("Athens_131", 5269, CapitalNpcServiceKind.HolyStoneRedeemer),
            ("Sparta_053", 5050, CapitalNpcServiceKind.HalloweenEnvoy),
            ("Athens_053", 5191, CapitalNpcServiceKind.HalloweenEnvoy),
            ("Sparta_089", 5086, CapitalNpcServiceKind.PetMerchant),
            ("Athens_089", 5227, CapitalNpcServiceKind.PetMerchant),
            ("Sparta_034", 44345, CapitalNpcServiceKind.SkillVendor),
            ("Athens_036", 5175, CapitalNpcServiceKind.SkillVendor),
            // The Athens newbie skill vendor is placed on map 2, where the
            // capture renumbers it to 5285; keying it by the published ini id
            // 54453 left the shop unanswerable.
            ("Athens_Newbie_004", 5285, CapitalNpcServiceKind.SkillVendor),
            ("Sparta_Newbie_004", 46565, CapitalNpcServiceKind.SkillVendor),
            ("Sparta_036", 5033, CapitalNpcServiceKind.PropsVendor),
            ("Athens_021", 5161, CapitalNpcServiceKind.PropsVendor),
            ("Sparta_077", 5074, CapitalNpcServiceKind.PointExchanger),
            ("Athens_077", 5215, CapitalNpcServiceKind.PointExchanger),
            ("Sparta_123", 5120, CapitalNpcServiceKind.PrizeChest),
            ("Athens_123", 5261, CapitalNpcServiceKind.PrizeChest),
            ("Sparta_142", 5139, CapitalNpcServiceKind.LevelSealer),
            ("Athens_142", 5280, CapitalNpcServiceKind.LevelSealer),
            // ROUND3 PROF soul stones: Sparta's life-skill vendors sell the Athens shelves (GENERATED).
            ("Sparta_121", 5118, CapitalNpcServiceKind.AthensAlchemyRecipeVendor),
            ("Sparta_122", 5119, CapitalNpcServiceKind.AthensIngredientsVendor),
            ("Sparta_135", 5132, CapitalNpcServiceKind.AthensForgingRecipeVendor),
            ("Sparta_136", 5133, CapitalNpcServiceKind.AthensMythcraftingRecipeVendor),
            ("Sparta_137", 5134, CapitalNpcServiceKind.AthensScholarshipRecipeVendor)
        ];

        var published = NpcContentBaselineV1.LoadDefinitions();

        foreach (var (key, id, expected) in endpoints)
        {
            // The captured maps' published ids are the client ini ones; the server
            // rewrites them to the reference's own as it publishes the map, and the
            // map normalizer moves any uncaptured npc whose published id the capture
            // took, so the routes must resolve the effective npc of the whole map.
            var source = published.Single(candidate => candidate.NpcKey == key);
            var effective = CapturedNpcPlacementPolicy
                .ApplyToMap(
                    [.. published.Where(candidate => candidate.MapId == source.MapId)])
                .Single(candidate => candidate.NpcKey == key);
            Check.True(
                effective.InteractionId == id &&
                CapitalNpcServiceProtocol.TryResolve(
                    effective,
                    out var actual) &&
                actual == expected,
                $"published capital endpoint {key}/{id} resolves to {expected}");
        }

        Check.True(
            !CapitalNpcServiceProtocol.TryResolve(
                Npc("Sparta_052", 5191),
                out _) &&
            !CapitalNpcServiceProtocol.TryResolve(
                Npc("Sparta_053", 5049),
                out _),
            "mixed or unrelated NPC identities cannot acquire capital behavior");

        var route = CapitalNpcServiceProtocol.ExchangeRoute(
            Npc("Sparta_052", 5049));
        Check.True(
            route.DialogIndex == 2 &&
            route.Behavior == NpcDialogueBehavior.CreditExchange &&
            route.InitialMenuSubIds.SequenceEqual([49, 50, 51]) &&
            CapitalNpcServiceProtocol.TryGetExchangePage(50, out var ethics) &&
            ethics.SequenceEqual([311, 312, 313]) &&
            CapitalNpcServiceProtocol.TryGetExchangePage(51, out var reputation) &&
            reputation.SequenceEqual([314, 315, 316]) &&
            !CapitalNpcServiceProtocol.TryGetExchangePage(49, out _),
            "Exchange Mentor follows the captured root and branch pages");
    }

    private static void CheckOpenPackets()
    {
        var description = PacketBuilder.NpcDescriptionDialogOpenAck(
            5066,
            "Sparta_069");
        var shop = PacketBuilder.NpcShopDialogOpenAck(
            5084,
            "Sparta_087");
        var chest = PacketBuilder.NpcPrizeChestDialogOpenAck(
            5120,
            "Sparta_123");

        Check.True(
            IsOpen(description, 5066, flags: 0, packedDialog: 0) &&
            IsOpen(shop, 5084, flags: 4, packedDialog: 0) &&
            IsOpen(chest, 5120, flags: 0x40, packedDialog: 0),
            "description, shop, and Prize Chest advertise captured window types");
    }

    private static void CheckShopCatalogs()
    {
        var bound = PacketBuilder.CapitalNpcShopCatalog(
            5084,
            new CapitalNpcShopBalances(111, 12_345, 222),
            CapitalNpcServiceKind.BoundGoldVendor);
        var boundFrames = ReadCatalogFrames(bound);
        Check.True(
            bound.Length == 18_424 &&
            boundFrames.Count == 13 &&
            boundFrames.Sum(static frame => frame[10]) == 132 &&
            boundFrames.All(frame => IsCatalogHeader(
                frame,
                npcId: 5084,
                shopType: 2,
                balance: 12_345)) &&
            boundFrames.SelectMany(ReadItemIds).All(static itemId =>
                itemId is not (4064 or 4177 or 4515 or 10059)) &&
            ReadItemId(boundFrames[0], 0) == 1002 &&
            ReadPrice(boundFrames[0], 0) == 10_000 &&
            ReadItemId(boundFrames[^1], 3) == 9024 &&
            ReadPrice(boundFrames[^1], 3) == 2_073,
            "Bound Gold Vendor retains 132 client-compatible captured records");

        var binding = PacketBuilder.CapitalNpcShopCatalog(
            5065,
            new CapitalNpcShopBalances(111, 222, 54_321),
            CapitalNpcServiceKind.BindingGoldShop);
        var bindingFrames = ReadCatalogFrames(binding);
        Check.True(
            binding.Length == 12_816 &&
            bindingFrames.Count == 9 &&
            bindingFrames.Sum(static frame => frame[10]) == 115 &&
            bindingFrames.All(frame => IsCatalogHeader(
                frame,
                npcId: 5065,
                shopType: 4,
                balance: 54_321)) &&
            ReadItemId(bindingFrames[0], 0) == 4230 &&
            ReadPrice(bindingFrames[0], 0) == 12 &&
            ReadItemId(bindingFrames[^1], 13) == 5618 &&
            ReadPrice(bindingFrames[^1], 13) == 3_200,
            "B-GOLD Shop reproduces all 115 captured stock records");

        var replay = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
            5226,
            new CapitalNpcShopBalances(6, 7, 8),
            CapitalNpcServiceKind.BoundGoldVendor));
        Check.True(
            replay.All(frame => IsCatalogHeader(
                frame,
                npcId: 5226,
                shopType: 2,
                balance: 7)) &&
            boundFrames.All(frame => IsCatalogHeader(
                frame,
                npcId: 5084,
                shopType: 2,
                balance: 12_345)),
            "catalog reuse patches a clone and cannot corrupt prior responses");

        var capturedBalances = new CapitalNpcShopBalances(321, 456, 654);
        var pet = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
            5086,
            capturedBalances,
            CapitalNpcServiceKind.PetMerchant));
        var skills = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
            44345,
            capturedBalances,
            CapitalNpcServiceKind.SkillVendor));
        var props = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
            5033,
            capturedBalances,
            CapitalNpcServiceKind.PropsVendor));
        Check.True(
            pet.Count == 7 &&
            pet.Sum(static frame => frame[10]) == 78 &&
            pet.All(frame => IsCatalogHeader(
                frame, 5086, shopType: 3, balance: 321)) &&
            ReadItemId(pet[0], 0) == 10000 &&
            skills.Count == 8 &&
            skills.Sum(static frame => frame[10]) == 86 &&
            skills.All(frame => IsCatalogHeader(
                frame, 44345, shopType: 4, balance: 654)) &&
            ReadItemId(skills[0], 0) == 5445 &&
            props.Count == 5 &&
            props.Sum(static frame => frame[10]) == 35 &&
            IsCatalogHeader(
                props[0], 5033, shopType: 3, balance: 321) &&
            props.Skip(1).All(frame => IsCatalogHeader(
                frame, 5033, shopType: 4, balance: 654)) &&
            ReadItemId(props[0], 0) == 3100 &&
            props.SelectMany(ReadItemIds).All(static id => id != 14085),
            "Pet, Skill, and Props catalogs reproduce compatible captured stock");

        var points = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
            5074,
            new CapitalNpcShopBalances(321, 456, 654, 111, 222, 333),
            CapitalNpcServiceKind.PointExchanger));
        // The vendor advertises one frame-level currency code but charges three
        // balances, so the wire stays byte-identical to the capture while the
        // advertised balance follows the frame's own listings.
        Check.True(
            points.Count == 5 &&
            points.Sum(static frame => frame[10]) == 54 &&
            points.Select(static frame => frame[8]).Distinct().Count() == 4 &&
            points.All(frame => frame[9] == 4) &&
            points.All(frame => BinaryPrimitives.ReadUInt32LittleEndian(
                frame.AsSpan(4)) == 5074) &&
            // Frames 1/2/3 hold honor stock, frame 4 point stock, frame 5
            // mixes honor (4266/4267/4269) with point (11010).
            BinaryPrimitives.ReadInt32LittleEndian(points[0].AsSpan(12)) == 111 &&
            BinaryPrimitives.ReadInt32LittleEndian(points[2].AsSpan(12)) == 111 &&
            ReadItemId(points[0], 0) == 9940 &&
            ReadPrice(points[0], 0) == 3_600 &&
            ReadItemId(points[0], 15) == 4223 &&
            ReadPrice(points[0], 15) == 20_000 &&
            points.SelectMany(ReadItemIds).All(static id => id != 14073),
            "Point Exchanger reproduces all 54 client-compatible captured records");

        // The Athens merchant quarter, captured 2026-09-24. Each entry is the
        // npc key and id, the frames it streams, the records those frames
        // declare, the wire currency byte they carry and the balance that byte
        // charges, and the first and last stock record - so a change to any
        // catalog's contents, ordering or currency fails here.
        (string Key, uint Npc, int Frames, int Records, byte ShopType,
            CapitalNpcShopCurrency Charge, uint First, uint FirstPrice,
            uint Last, uint LastPrice)[] athens =
        [
            ("Athens_026", 5166, 5, 63, 4, CapitalNpcShopCurrency.BindingGold,
                1000, 58, 3006, 683),
            ("Athens_027", 5167, 5, 63, 4, CapitalNpcShopCurrency.BindingGold,
                1700, 58, 3006, 683),
            ("Athens_028", 5168, 6, 76, 4, CapitalNpcShopCurrency.BindingGold,
                3100, 58, 3612, 683),
            ("Athens_029", 5169, 7, 91, 4, CapitalNpcShopCurrency.BindingGold,
                2700, 58, 3612, 683),
            ("Athens_096", 5234, 5, 63, 4, CapitalNpcShopCurrency.BindingGold,
                1000, 58, 3006, 683),
            ("Athens_099", 5237, 8, 86, 4, CapitalNpcShopCurrency.BindingGold,
                5445, 1, 5208, 8500),
            ("Athens_102", 5240, 6, 78, 4, CapitalNpcShopCurrency.BindingGold,
                2100, 58, 3412, 683),
            ("Athens_107", 5245, 5, 63, 4, CapitalNpcShopCurrency.BindingGold,
                1700, 58, 3006, 683),
            ("Athens_108", 5246, 2, 24, 2, CapitalNpcShopCurrency.Gold,
                3100, 58, 3210, 654),
            ("Athens_121", 5259, 5, 79, 3, CapitalNpcShopCurrency.Silver,
                13000, 1856, 13074, 919855),
            ("Athens_122", 5260, 8, 96, 4, CapitalNpcShopCurrency.BindingGold,
                12800, 105, 12982, 2125),
            ("Athens_135", 5273, 5, 76, 3, CapitalNpcShopCurrency.Silver,
                13200, 1856, 13271, 634970),
            ("Athens_136", 5274, 5, 80, 3, CapitalNpcShopCurrency.Silver,
                13400, 1856, 13475, 764707),
            ("Athens_137", 5275, 6, 93, 3, CapitalNpcShopCurrency.Silver,
                13600, 1856, 13692, 634970)
        ];
        foreach (var (key, npc, frameCount, records, shopType, charge, first,
                     firstPrice, last, lastPrice) in athens)
        {
            var resolved = CapitalNpcServiceProtocol.TryResolve(
                Npc(key, npc), out var merchant);
            Check.True(
                resolved &&
                CapitalNpcServiceProtocol.IsShop(merchant) &&
                CapitalNpcServiceProtocol.TryGetShopCurrency(
                    merchant, out var charged) &&
                charged == charge,
                $"Athens merchant {key} resolves to a shop charging its own balance");

            // The frame advertises the balance its own listings charge, so the
            // expected value follows the currency rather than being one number.
            var balances = new CapitalNpcShopBalances(
                Silver: 111, Gold: 222, BindingGold: 333);
            var catalog = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
                npc, balances, merchant));
            var lastFrame = catalog[^1];
            Check.True(
                catalog.Count == frameCount &&
                catalog.Sum(static frame => frame[10]) == records &&
                catalog.All(frame => IsCatalogHeader(
                    frame, npc, shopType, balances.Get(charge))) &&
                ReadItemId(catalog[0], 0) == first &&
                ReadPrice(catalog[0], 0) == firstPrice &&
                ReadItemId(lastFrame, lastFrame[10] - 1) == last &&
                ReadPrice(lastFrame, lastFrame[10] - 1) == lastPrice,
                $"Athens merchant {npc} reproduces its {records} captured records");
        }

        Check.True(
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                9940, out var honorItem) &&
            honorItem == CapitalNpcShopCurrency.Honor &&
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                9958, out var medalItem) &&
            medalItem == CapitalNpcShopCurrency.Medal &&
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                3931, out var pointItem) &&
            pointItem == CapitalNpcShopCurrency.Point &&
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                11010, out var pointItem2) &&
            pointItem2 == CapitalNpcShopCurrency.Point &&
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                4266, out var honorItem2) &&
            honorItem2 == CapitalNpcShopCurrency.Honor &&
            !CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(999_999u, out _),
            "Point Exchanger listings charge their own balance");

        // Decision b06: the Rank IV badge (4193) sells for medals, like
        // Promotional Insignia III (14069) on the same tab.
        Check.True(
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                4193, out var rankFourBadge) &&
            rankFourBadge == CapitalNpcShopCurrency.Medal &&
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                14069, out var insigniaThree) &&
            insigniaThree == CapitalNpcShopCurrency.Medal,
            "B06 Rank IV badge 4193 is charged in medals at the Point Exchanger");

        Check.True(
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)5, out var wireHonor) &&
            wireHonor == CapitalNpcShopCurrency.Honor &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)6, out var wirePoint) &&
            wirePoint == CapitalNpcShopCurrency.Point &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)7, out var wireMedal) &&
            wireMedal == CapitalNpcShopCurrency.Medal &&
            !CapitalNpcServiceProtocol.TryGetShopCurrency((byte)8, out _),
            "Point Exchanger wire currency codes map to its three balances");
    }

    /// <summary>
    /// The ten Sparta merchants the September 28 2026 capture streamed stock
    /// from, four of which the live catalog used to delete outright.
    /// </summary>
    /// <remarks>
    /// <c>Sparta_028</c>, <c>Sparta_029</c>, <c>Sparta_037</c> and
    /// <c>Sparta_038</c> were removed from the world by the (now deleted)
    /// <c>IsSuppressedSpawn</c> filter, so the reference's own four merchants
    /// never reached the client. The capture shows each of them answering a
    /// click with flags 4 and then streaming a shelf, so the filter is gone and
    /// every one of the ten now resolves to a shop kind.
    /// </remarks>
    private static void CheckSpartaMerchantQuarterRestored()
    {
        var published = NpcContentBaselineV1.LoadDefinitions();
        (string Key, uint Id, CapitalNpcServiceKind Kind, int Frames,
            int Listings)[] merchants =
        [
            ("Sparta_028", 42_888u, CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant, 5, 63),
            ("Sparta_096", 5_093u, CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant, 5, 63),
            ("Sparta_029", 5_026u, CapitalNpcServiceKind.SpartaScholarEquipmentMerchant, 5, 63),
            ("Sparta_107", 5_104u, CapitalNpcServiceKind.SpartaScholarEquipmentMerchant, 5, 63),
            ("Sparta_034", 44_345u, CapitalNpcServiceKind.SpartaSkillMerchant, 8, 86),
            ("Sparta_099", 5_096u, CapitalNpcServiceKind.SpartaSkillMerchant, 8, 86),
            ("Sparta_037", 5_034u, CapitalNpcServiceKind.SpartaArmorMerchant, 7, 91),
            ("Sparta_038", 5_035u, CapitalNpcServiceKind.SpartaJewelryMerchant, 6, 76),
            ("Sparta_036", 5_033u, CapitalNpcServiceKind.SpartaPropsMerchant, 4, 31),
            ("Sparta_089", 5_086u, CapitalNpcServiceKind.SpartaPetMerchant, 8, 87)
        ];

        Check.Equal(
            merchants.Length,
            merchants.Select(static entry => entry.Key).Distinct().Count(),
            "the captured Sparta quarter has ten distinct merchants");

        foreach (var (key, id, kind, frames, listings) in merchants)
        {
            var npc = published.Single(candidate => candidate.NpcKey == key);
            Check.Equal(
                id,
                npc.InteractionId,
                $"{key} keeps the published id the client echoes back");
            Check.True(
                CapitalNpcServiceProtocol.TryResolve(key, id, out var resolved) &&
                resolved == kind,
                $"{key} resolves to its captured shop kind");
            Check.True(
                CapitalNpcServiceProtocol.IsShop(kind),
                $"{key}'s captured kind is a shop");
            Check.True(
                CapitalNpcServiceProtocol.TryGetShopCurrency(
                    kind,
                    out var currency) &&
                currency == CapitalNpcShopCurrency.Silver,
                $"{key} charges silver, as this port requires");

            var catalog = PacketBuilder.CapitalNpcShopCatalog(
                id,
                new CapitalNpcShopBalances(Silver: 1_234, Gold: 5, BindingGold: 6),
                kind);
            var offset = 0;
            var frameCount = 0;
            var itemCount = 0;
            while (offset < catalog.Length)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    catalog.AsSpan(offset, sizeof(ushort)));
                Check.True(
                    length >= 16 && offset + length <= catalog.Length,
                    $"{key} catalogue frame {frameCount} is well formed");
                Check.Equal(
                    id,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        catalog.AsSpan(offset + 4, sizeof(uint))),
                    $"{key} catalogue frame {frameCount} carries its own id");
                Check.Equal(
                    (byte)3,
                    catalog[offset + 9],
                    $"{key} catalogue frame {frameCount} prices in silver");
                Check.Equal(
                    1_234,
                    BinaryPrimitives.ReadInt32LittleEndian(
                        catalog.AsSpan(offset + 12, sizeof(int))),
                    $"{key} catalogue frame {frameCount} advertises the silver balance");
                itemCount += catalog[offset + 10];
                frameCount++;
                offset += length;
            }

            Check.Equal(
                frames,
                frameCount,
                $"{key} streams the captured frame count");
            Check.Equal(
                frames == 5 ? 63 : itemCount,
                itemCount,
                $"{key} streams the captured listing count");
        }
    }

    private static bool IsOpen(
        byte[] packet,
        uint npcId,
        int flags,
        int packedDialog) =>
        packet.Length == 48 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet) == 48 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
            Opcodes.NpcDialogOpen &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == npcId &&
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == flags &&
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12)) ==
            packedDialog;

    private static bool IsCatalogHeader(
        byte[] packet,
        uint npcId,
        byte shopType,
        int balance) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
            Opcodes.NpcShopCatalog &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == npcId &&
        packet[9] == shopType &&
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12)) == balance;

    internal static IReadOnlyList<byte[]> ReadCatalogFrames(byte[] stream)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                stream.AsSpan(offset));
            Check.True(
                length >= 16 && offset + length <= stream.Length,
                "capital shop catalog frame is bounded");
            frames.Add(stream.AsSpan(offset, length).ToArray());
            offset += length;
        }

        Check.Equal(stream.Length, offset,
            "capital shop catalog stream ends on a frame boundary");
        return frames;
    }

    private static uint ReadItemId(byte[] packet, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(16 + (index * 88)));

    private static uint ReadPrice(byte[] packet, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(16 + (index * 88) + 68));

    internal static IEnumerable<uint> ReadItemIds(byte[] packet)
    {
        for (var index = 0; index < packet[10]; index++)
        {
            yield return ReadItemId(packet, index);
        }
    }

    private static void CheckPurchaseIntentAndOfferAuthority()
    {
        var payload = new byte[CapitalNpcServiceProtocol.PurchasePayloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 5084);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 31);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 3101);

        Check.True(
            CapitalNpcServiceProtocol.TryParsePurchase(
                payload,
                out var intent) &&
            intent == new CapitalNpcShopPurchaseIntent(
                5084,
                0,
                31,
                1,
                3101) &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BoundGoldVendor,
                intent.Category,
                intent.ListingIndex,
                intent.ItemId,
                out var offer) &&
            offer.UnitPrice == 5_000 &&
            offer.Currency == CapitalNpcShopCurrency.Gold &&
            offer.Item == new CompactItemEntry(
                3101,
                70,
                103,
                143,
                120,
                null,
                3,
                3,
                0,
                1,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            "captured purchase resolves its server-owned item and price");

        CheckCapturedShopOfferAuthority();

        Check.True(
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.BoundGoldVendor,
                out var vendorCurrency) &&
            vendorCurrency == CapitalNpcShopCurrency.Gold &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.BindingGoldShop,
                out var shopCurrency) &&
            shopCurrency == CapitalNpcShopCurrency.BindingGold &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.PetMerchant,
                out var petCurrency) &&
            petCurrency == CapitalNpcShopCurrency.Silver &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.SkillVendor,
                out var skillCurrency) &&
            skillCurrency == CapitalNpcShopCurrency.BindingGold &&
            !CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.PropsVendor,
                out _) &&
            !CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.TeachingManager,
                out _),
            "uniform shop services expose their currency while mixed Props " +
            "requires frame-level resolution");

        Check.True(
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)2,
                out var wireGold) &&
            wireGold == CapitalNpcShopCurrency.Gold &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)3,
                out var wireSilver) &&
            wireSilver == CapitalNpcShopCurrency.Silver &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)4,
                out var wireBindingGold) &&
            wireBindingGold == CapitalNpcShopCurrency.BindingGold &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)5,
                out var wireHonor) &&
            wireHonor == CapitalNpcShopCurrency.Honor &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)6,
                out var wirePoint) &&
            wirePoint == CapitalNpcShopCurrency.Point &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                (byte)7,
                out var wireMedal) &&
            wireMedal == CapitalNpcShopCurrency.Medal &&
            !CapitalNpcServiceProtocol.TryGetShopCurrency((byte)8, out _) &&
            CapitalNpcServiceProtocol.IsShop(
                CapitalNpcServiceKind.PetMerchant) &&
            CapitalNpcServiceProtocol.IsShop(
                CapitalNpcServiceKind.SkillVendor) &&
            CapitalNpcServiceProtocol.IsShop(
                CapitalNpcServiceKind.PropsVendor) &&
            CapitalNpcServiceProtocol.IsShop(
                CapitalNpcServiceKind.PointExchanger) &&
            !CapitalNpcServiceProtocol.IsShop(
                CapitalNpcServiceKind.TeachingManager),
            "catalog wire types and all purchase-capable services are " +
            "recognized explicitly");

        Check.True(
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BoundGoldVendor,
                category: 1,
                listingIndex: 31,
                expectedItemId: 3101,
                out _) &&
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BoundGoldVendor,
                category: 0,
                listingIndex: 31,
                expectedItemId: 3102,
                out _) &&
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BindingGoldShop,
                category: 0,
                listingIndex: 31,
                expectedItemId: 3101,
                out _) &&
            !CapitalNpcServiceProtocol.TryParsePurchase(
                payload.AsSpan(0, payload.Length - 1),
                out _),
            "category, listing, item, shop, and framing collisions fail closed");
    }

    private static NpcSpawnDefinition Npc(string key, uint interactionId) =>
        new(
            MapId: 0,
            SceneKey: key,
            NpcKey: key,
            TemplateKey: key,
            ObjectId: interactionId,
            X: 0,
            Z: 0,
            InteractionId: interactionId,
            AppearanceType: 0,
            Facing: 0,
            Detail10077: [],
            Detail10080: []);

    private static void CheckBindingGoldMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id ==
                "20260828_121_capital_npc_binding_gold");
        Check.True(
            migration.Sql.Contains(
                "ADD COLUMN IF NOT EXISTS \"BindingGold\"",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'silver', 'gold', 'binding_gold'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "binding_gold_delta",
                StringComparison.Ordinal),
            "capital shops install B-Gold storage, ledger, and reconciliation support");
    }
}
