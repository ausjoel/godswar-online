using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The Lelantine Farm's entry transport, admission window and dialogue
/// endpoints as published data rather than live sessions.
/// </summary>
internal static class LelantineFarmProtocolChecks
{
    public const string CheckName = "Lelantine Farm entry, window, and endpoints";

    private static readonly RealmCalendar Realm =
        RealmCalendar.CreateForTesting(RealmId.Tempest, "Asia/Manila");

    public static Task RunAsync()
    {
        CheckFarmEntryRoute();
        CheckFarmWindowIsAlwaysOpen();
        CheckFarmRosterContract();
        CheckScoreQueriesAreDistinct();
        CheckScoreEncoding();
        CheckEggDonationRungs();
        CheckDonationSubmission();
        CheckDonationBroadcast();
        CheckFactionTotalIsTheSumOfPersonalScores();
        CheckKillAwardsAndLevelGap();
        CheckNonFarmWindowsAreUnchanged();
        CheckCapturablePetTenHits();
        CheckCerberusBookRoll();
        return Task.CompletedTask;
    }

    /// <summary>
    /// FINALISE-PETS: capturable pets (farm dogs, Rock Elf, Atlantis pets; wild babies while the switch is on)
    /// carry 10 health and take 1 per hit; ordinary monsters are untouched.
    /// </summary>
    private static void CheckCapturablePetTenHits()
    {
        Check.True(CapturablePetCombatPolicy.IsCaptureTarget(42, "A_normalA_male_001"), "farm Simple Dog is a capture target");
        Check.True(CapturablePetCombatPolicy.IsCaptureTarget(42, "A_normalD_famale_001"), "farm Wise Dog is a capture target");
        Check.True(!CapturablePetCombatPolicy.IsCaptureTarget(42, "A_normal_wolf_001"), "farm wolf is not a capture target");
        Check.True(!CapturablePetCombatPolicy.IsCaptureTarget(42, LelantineFarmLootPolicy.CerberusTemplateKey), "Cerberus is not a capture target");
        Check.True(CapturablePetCombatPolicy.IsCaptureTarget(200, MedusaIslandAmbientSpawnPolicy.BabyRockElfTemplateKey), "Medusa Baby Rock Elf is a capture target");
        // F-122: with atlantis.mermaid_dew on (default) the Mermaid has real health; the Siren keeps the ten-hit rule.
        Check.True(!CapturablePetCombatPolicy.IsCaptureTarget(205, AtlantisPetSpawnPolicy.MermaidTemplateKey, AtlantisPetSpawnPolicy.MermaidObjectId), "Atlantis Mermaid is not a ten-hit target (F-122)");
        Check.True(CapturablePetCombatPolicy.IsCaptureTarget(205, AtlantisPetSpawnPolicy.SirenTemplateKey, AtlantisPetSpawnPolicy.SirenObjectId), "Atlantis Siren is a capture target");
        Check.True(!CapturablePetCombatPolicy.IsCaptureTarget(0, "A_normalA_male_001"), "a farm dog key off the farm is not a capture target");
        var packet = new byte[108];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 5000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), 5000);
        var dog = new CapturedMonsterSpawn(42, "Lelantine_Farm", "A_normalA_male_001", "Simple Dog", 46001, 0, 0, packet);
        var normalized = CapturablePetCombatPolicy.NormalizeSpawn(dog);
        Check.Equal(10u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(normalized.Packet.AsSpan(20)), "dog current health 10");
        Check.Equal(10u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(normalized.Packet.AsSpan(24)), "dog maximum health 10");
        Check.Equal(5000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dog.Packet.AsSpan(24)), "the published packet is not mutated");
        Check.Equal(1u, CapturablePetCombatPolicy.ClampDamage(dog, 99_999), "a hit on a dog applies 1");
        var wolf = dog with { TemplateKey = "A_normal_wolf_001" };
        Check.Equal(99_999u, CapturablePetCombatPolicy.ClampDamage(wolf, 99_999), "a hit on a wolf is unchanged");
        Check.True(ReferenceEquals(wolf, CapturablePetCombatPolicy.NormalizeSpawn(wolf)), "a wolf spawn is not normalised");
    }

    /// <summary>FINALISE-PETS: Cerberus drops one level I/II pet skill book on about half of its deaths.</summary>
    private static void CheckCerberusBookRoll()
    {
        Check.True(LelantineFarmLootPolicy.IsCerberus(42, "B_boss_wolf_006"), "farm Cerberus is recognised");
        Check.True(!LelantineFarmLootPolicy.IsCerberus(41, "B_boss_wolf_006"), "the template off the farm is not");
        var drops = 0;
        var books = new HashSet<uint>();
        for (var i = 0; i < 4000; i++)
        {
            var id = new Guid(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)).AsSpan(0, 16));
            if (LelantineFarmLootPolicy.TryRollCerberusBook(id, out var book))
            {
                drops++;
                books.Add(book);
                Check.True(LelantineFarmLootPolicy.CerberusBookPool.Contains(book), $"book {book} is in the pool");
            }
            Check.True(LelantineFarmLootPolicy.TryRollCerberusBook(id, out var again) == (again != 0) &&
                (again == 0 || LelantineFarmLootPolicy.TryRollCerberusBook(id, out var third) && third == again),
                "the roll is deterministic in the death event");
        }
        Check.True(drops is > 1800 and < 2200, $"about half of 4000 deaths drop a book ({drops})");
        Check.True(books.Count > LelantineFarmLootPolicy.CerberusBookPool.Count / 2, $"many different books drop ({books.Count})");
        Check.True(LelantineFarmLootPolicy.CerberusBookPool.All(static id => id is >= 10200 and <= 10291),
            "the pool is level I/II pet skill books");
        Check.True(!LelantineFarmLootPolicy.TryRollCerberusBook(Guid.Empty, out _), "no roll without a death event");
    }

    /// <summary>
    /// FINALISE-PETS (the admin 2026-11-05): the activity scores exactly the three Loyal Dog natures - Cowardly (3)
    /// is ten points, Rational (5) thirty and Brave (10) a hundred and fifty - and refuses every other
    /// quality rather than pricing it. A captured farm dog only ever rolls one of those three.
    /// </summary>
    private static void CheckEggDonationRungs()
    {
        foreach (var (aptitude, expected) in new (short Aptitude, int Points)[]
                 {
                     (LelantineFarmPointsPolicy.CowardlyEggAptitude, 10),
                     (LelantineFarmPointsPolicy.RationalEggAptitude, 30),
                     (LelantineFarmPointsPolicy.BraveEggAptitude, 150)
                 })
        {
            Check.True(
                LelantineFarmPointsPolicy.TryResolveEggDonationPoints(
                    aptitude,
                    out var points) &&
                points == expected,
                $"egg aptitude {aptitude} is worth {expected} points");
        }

        Check.True(
            LelantineFarmPointsPolicy.CowardlyEggAptitude == 3 &&
            LelantineFarmPointsPolicy.RationalEggAptitude == 5 &&
            LelantineFarmPointsPolicy.BraveEggAptitude == 10,
            "the scored aptitudes are the client's 3 (Cowardly), 5 (Rational) and 10 (Brave)");

        // Everything else is refused: the client's other aptitudes and an
        // unset quality of zero.
        foreach (var aptitude in new short[] { 0, 1, 2, 4, 6, 7, 8, 9, 14, 15 })
        {
            Check.True(
                !LelantineFarmPointsPolicy.TryResolveEggDonationPoints(
                    aptitude,
                    out var points) &&
                points == 0,
                $"egg aptitude {aptitude} is not scored");
        }

        // Every dog kind rolls only scored natures, all three can occur, and the weights cover 10,000.
        foreach (var tier in new[]
                 {
                     MedusaEncounterDifficulty.Normal,
                     MedusaEncounterDifficulty.Enhanced,
                     MedusaEncounterDifficulty.Mythic
                 })
        {
            var weights = LelantineFarmDogPolicy.NatureWeights(tier);
            Check.True(weights.Sum(static weight => weight.BasisPoints) == 10_000,
                $"dog tier {tier} nature weights total 10,000");
            var seen = new HashSet<short>();
            for (var roll = 0; roll < 10_000; roll += 50)
            {
                var nature = LelantineFarmDogPolicy.RollNature(tier, roll);
                Check.True(LelantineFarmPointsPolicy.TryResolveEggDonationPoints(nature, out _),
                    $"dog tier {tier} roll {roll} gives a donatable nature");
                seen.Add(nature);
            }
            Check.True(seen.SetEquals(new short[] { 3, 5, 10 }),
                $"dog tier {tier} can give Cowardly, Rational and Brave");
        }

        Check.True(
            LelantineFarmPointsPolicy.IsDonationEgg(
                LelantineFarmPointsPolicy.HoundEggItemId),
            "the hound egg is the donation item");
    }

    /// <summary>
    /// A score reaches the window packed onto the selector the client draws it
    /// on, because the client reads each number as
    /// <c>(SubID - selector) / 1000</c>.
    /// </summary>
    private static void CheckScoreEncoding()
    {
        Check.Equal(
            LelantineFarmProtocol.ScoreValueScale,
            1000,
            "the window's value scale is the client's own divisor");

        foreach (var (selector, value) in new (int Selector, long Value)[]
                 {
                     (LelantineFarmProtocol.PersonalScoreSelector, 0L),
                     (LelantineFarmProtocol.PersonalScoreSelector, 12345L),
                     (LelantineFarmProtocol.HighestScoreSelector, 7L),
                     (LelantineFarmProtocol.RankingSelector, 1L),
                     (LelantineFarmProtocol.SpartaScoreSelector, 999999L),
                     (LelantineFarmProtocol.AthensScoreSelector, 2000000L)
                 })
        {
            var encoded = LelantineFarmProtocol.EncodeScore(selector, value);
            Check.Equal(
                value,
                LelantineFarmProtocol.DecodeScore(encoded),
                $"selector {selector} carries {value}");
            Check.True(
                (encoded - selector) / 1000 == value &&
                encoded % LelantineFarmProtocol.ScoreValueScale == selector,
                $"selector {selector} survives the client's arithmetic");
        }

        // A zero score is the bare selector, which is the exact form the
        // capture answered the two queries with.
        Check.True(
            LelantineFarmProtocol.EncodeScore(
                LelantineFarmProtocol.PersonalScoreSelector,
                0) == 11 &&
            LelantineFarmProtocol.EncodeScore(
                LelantineFarmProtocol.SpartaScoreSelector,
                0) == 23,
            "an empty score board is the captured bare selector");

        Check.True(
            !LelantineFarmProtocol.IsScoreSelector(1) &&
            !LelantineFarmProtocol.IsScoreSelector(14) &&
            LelantineFarmProtocol.IsScoreSelector(11) &&
            LelantineFarmProtocol.IsScoreSelector(24),
            "only the five drawn lines are score selectors");
        Check.Throws<ArgumentOutOfRangeException>(
            () => LelantineFarmProtocol.EncodeScore(15, 1),
            "a non-score number cannot carry a value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => LelantineFarmProtocol.EncodeScore(
                LelantineFarmProtocol.PersonalScoreSelector,
                -1),
            "a score cannot be negative");
    }

    /// <summary>
    /// A donation submission names the stack the player put in the item control,
    /// so the eggs taken - and therefore the aptitude they score as - are the
    /// ones the player chose.
    /// </summary>
    private static void CheckDonationSubmission()
    {
        // The three submissions measured live on 2026-09-26, word for word: the
        // confirm entry as the path's first word, the item control as
        // bagPage * 100 + pageSlot, and the typed count on the amount word.
        foreach (var (submitted, expectedSlot) in new (int[] Arguments, int Slot)[]
                 {
                     (Submission(item: 118, amount: 1), 42),
                     (Submission(item: 117, amount: 1), 41),
                     (Submission(item: 116, amount: 50), 40)
                 })
        {
            Check.True(
                LelantineFarmProtocol.TryResolveSubmittedEggSlot(
                    submitted,
                    out var slot) &&
                slot == expectedSlot,
                $"item control {submitted[6]} is kit-bag slot {expectedSlot}");
        }

        // The first bag page carries no page offset, and the page's own slots run
        // from 0 to 23 - so 23 is the last slot of the first page and 24 is not a
        // slot of it at all.
        foreach (var (coordinate, expectedFirstPageSlot) in
                 new[] { (16, 16), (23, 23) })
        {
            Check.True(
                LelantineFarmProtocol.TryResolveSubmittedEggSlot(
                    Submission(item: coordinate, amount: 5),
                    out var firstPage) &&
                firstPage == expectedFirstPageSlot,
                $"item control {coordinate} is first-page slot " +
                $"{expectedFirstPageSlot}");
        }

        // An empty box, a page past the bag, and a slot past the page are all
        // refused rather than pointed at some other stack.
        foreach (var coordinate in new[] { -1, 24, 400, 124, 399 })
        {
            Check.True(
                !LelantineFarmProtocol.TryResolveSubmittedEggSlot(
                    Submission(item: coordinate, amount: 5),
                    out var rejected) &&
                rejected == -1,
                $"item control {coordinate} resolves to no stack");
        }

        // The confirm word is never a quantity: reading it as one is exactly the
        // bug that made a submission count as nothing.
        Check.Equal(
            0,
            LelantineFarmProtocol.WindowConfirmSubId,
            "the confirm entry is the path's literal zero");
        Check.True(
            !LelantineFarmPointsPolicy.IsAcceptedQuantity(
                LelantineFarmProtocol.WindowConfirmSubId),
            "the confirm word cannot be mistaken for a donation count");

        // The typed count rides on the payload's +0x38 word, which is argument 10
        // of the 18 the action carries. That offset is the one measured on the
        // guild altar's own input pages and read here through DialogAmount.
        const int dialogAmountOffset = 0x38;
        const int firstArgumentOffset = 16;
        Check.Equal(
            10,
            (dialogAmountOffset - firstArgumentOffset) / 4,
            "the typed count is argument 10 of the action frame");
    }

    /// <summary>
    /// One measured donation submission: the window's confirm entry, the item
    /// control's bag coordinate, and the typed count on its own word.
    /// </summary>
    private static int[] Submission(int item, int amount)
    {
        var arguments = new int[18];
        Array.Fill(arguments, -1);
        arguments[0] = LelantineFarmProtocol.WindowConfirmSubId;
        arguments[6] = item;
        arguments[10] = amount;
        return arguments;
    }

    /// <summary>
    /// A donation worth more than the activity's threshold is proclaimed to the
    /// map on the client's own centre-screen note, whose text the client builds
    /// from the note's message ids rather than from anything on the wire.
    /// </summary>
    private static void CheckDonationBroadcast()
    {
        // The client script's own composition: SrvMsg.lua's SrvMsg_NOTE_181 = 33
        // draws name, its Lelantine table's first field, the note's third field
        // and its second field, on CHANNEL_MIDDLE = 0.
        Check.Equal(
            LelantineFarmProtocol.DonationBroadcastNoteType,
            33,
            "the announcement uses the client's Lelantine note type");
        Check.Equal(
            LelantineFarmProtocol.DonationBroadcastChannel,
            0,
            "the announcement is drawn on the client's middle channel");
        Check.True(
            LelantineFarmProtocol.DonationSpartaNoteId == "51070" &&
            LelantineFarmProtocol.DonationAthensNoteId == "51080" &&
            LelantineFarmProtocol.DonationPointsNoteId == "51090",
            "the announcement names the client's three message ids");

        // The note is "camp id # points id # points", which the client renders as
        // "捐献犬宝宝宠物蛋，<camp>阵营获得<points>积分！".
        Check.Equal(
            "51070#51090#110",
            LelantineFarmProtocol.BuildDonationBroadcastNote(
                athenian: false,
                points: 110),
            "a Spartan donation's note");
        Check.Equal(
            "51080#51090#1",
            LelantineFarmProtocol.BuildDonationBroadcastNote(
                athenian: true,
                points: 1),
            "an Athenian donation's note");

        // The threshold announces a donation worth more than a hundred points:
        // a hundred exactly is silent, a hundred and one is not.
        Check.True(
            !LelantineFarmProtocol.IsAnnouncedDonation(100) &&
            LelantineFarmProtocol.IsAnnouncedDonation(101) &&
            !LelantineFarmProtocol.IsAnnouncedDonation(10) &&
            LelantineFarmProtocol.IsAnnouncedDonation(110),
            "the announcement threshold is more than a hundred points");

        // The frame itself: opcode 10038, the note type at +4, the channel at +8,
        // the donor at +9 and the note at +73, each a fixed 64-byte field.
        var packet = PacketBuilder.LelantineDonationBroadcast(
            "test",
            athenian: false,
            points: 110);
        Check.Equal(137, packet.Length, "a note frame is 137 bytes");
        Check.Equal(
            10038,
            (int)System.Buffers.Binary.BinaryPrimitives
                .ReadUInt16LittleEndian(packet.AsSpan(2)),
            "the note frame carries the note opcode");
        Check.Equal(
            LelantineFarmProtocol.DonationBroadcastNoteType,
            System.Buffers.Binary.BinaryPrimitives
                .ReadInt32LittleEndian(packet.AsSpan(4)),
            "the note frame carries the announcement type");
        Check.Equal(
            (byte)LelantineFarmProtocol.DonationBroadcastChannel,
            packet[8],
            "the note frame carries the middle channel");
        Check.Equal(
            "test",
            ReadFixedAscii(packet.AsSpan(9, 64)),
            "the note frame names the donor");
        Check.Equal(
            "51070#51090#110",
            ReadFixedAscii(packet.AsSpan(73, 64)),
            "the note frame carries the announcement note");

        // A name that cannot fit the native field is refused rather than cut
        // into a different name.
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.LelantineDonationBroadcast(
                new string('a', 64),
                athenian: false,
                points: 110),
            "an over-long donor name cannot be announced");
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(
            end < 0 ? field : field[..end]);
    }

    /// <summary>
    /// The activity's faction score is the sum of every same-faction personal
    /// score, so the ledger the two reads share has to carry both donations and
    /// credited kills, and the donation-only running total has to be gone.
    /// </summary>
    private static void CheckFactionTotalIsTheSumOfPersonalScores()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id ==
                "20260926_210_lelantine_farm_personal_scores");
        var sql = migration.Sql;

        Check.True(
            sql.Contains(
                "CREATE OR REPLACE VIEW public.lelantine_farm_personal_points",
                StringComparison.Ordinal),
            "a personal score is published as one view");
        Check.True(
            sql.Contains(
                "FROM public.lelantine_farm_donations",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FROM public.lelantine_farm_kill_points",
                StringComparison.Ordinal),
            "a personal score sums donations and credited kills");
        Check.True(
            sql.Contains(
                "GROUP BY ledger.character_id, ledger.faction",
                StringComparison.Ordinal),
            "a personal score is held per character and faction");
        Check.True(
            sql.Contains(
                "DROP TABLE IF EXISTS public.lelantine_farm_faction_points",
                StringComparison.Ordinal),
            "the donation-only faction running total is retired");
        Check.True(
            sql.Contains(
                "PRIMARY KEY (character_id, faction)",
                StringComparison.Ordinal),
            "credited kills are keyed by the camp they were earned for");
        // The release must still be registered, and no later release may be
        // backdated behind it: a forward-only history means the next release
        // becomes the new head, so pinning this one as the head outright would
        // fail the moment anything else ships.
        Check.True(
            PostgresSchemaMigrationCatalog.All.Any(candidate =>
                candidate.Id == migration.Id) &&
            string.CompareOrdinal(
                PostgresSchemaMigrationCatalog.All[^1].Id,
                migration.Id) >= 0,
            "the score-summing release is registered and nothing backdates it");
    }

    /// <summary>
    /// The activity publishes two different score reads and the capture answers
    /// them with two different reply groups, so they must stay distinct.
    /// </summary>
    private static void CheckScoreQueriesAreDistinct()
    {
        // Captured: S2C 10070 {5615, 47, 11, 12, 13} at 21:00:56.
        Check.True(
            LelantineFarmProtocol.CaptainStatisticsSubIds.SequenceEqual(
                [11, 12, 13]),
            "the personal statistics group is the captured 11, 12, 13");
        // Captured: S2C 10070 {5615, 47, 23, 24} at 21:01:23.
        Check.True(
            LelantineFarmProtocol.CaptainFactionScoreSubIds.SequenceEqual(
                [23, 24]),
            "the faction score group is the captured 23, 24");

        // The captured groups are the page's own lines in the order the page
        // stacks them: personal score, high score, rank - then each camp.
        Check.True(
            LelantineFarmProtocol.CaptainStatisticsSubIds.SequenceEqual(
                [
                    LelantineFarmProtocol.PersonalScoreSelector,
                    LelantineFarmProtocol.HighestScoreSelector,
                    LelantineFarmProtocol.RankingSelector
                ]),
            "the statistics group is the ranking page's three lines");
        Check.True(
            LelantineFarmProtocol.CaptainFactionScoreSubIds.SequenceEqual(
                [
                    LelantineFarmProtocol.SpartaScoreSelector,
                    LelantineFarmProtocol.AthensScoreSelector
                ]),
            "the faction group is the two camps' totals");
        Check.True(
            !LelantineFarmProtocol.CaptainStatisticsSubIds.Intersect(
                LelantineFarmProtocol.CaptainFactionScoreSubIds).Any(),
            "the two score queries never share a reply group");

        // The high score is one line on the page, so the value on it is the
        // battlefield's highest personal score rather than one per camp.
        Check.True(
            LelantineFarmProtocol.CaptainStatisticsSubIds.Count(
                static subId => subId ==
                    LelantineFarmProtocol.HighestScoreSelector) == 1,
            "the ranking page draws the high score on exactly one line");

        // "查看阵营积分" and "查看战场排名" are separate root entries, so the
        // handler can route each to its own group.
        Check.True(
            LelantineFarmProtocol.FarmFactionPointPage !=
                LelantineFarmProtocol.FarmRankingPage,
            "the faction and ranking entries are distinct root buttons");
        Check.True(
            LelantineFarmProtocol.CaptainMenuSubIds.Contains(
                LelantineFarmProtocol.FarmFactionPointPage) &&
            LelantineFarmProtocol.CaptainMenuSubIds.Contains(
                LelantineFarmProtocol.FarmRankingPage),
            "both score entries are on the captured root menu");
    }

    /// <summary>
    /// The activity's three kill awards and its ten-level gap for normal
    /// monsters.
    /// </summary>
    private static void CheckKillAwardsAndLevelGap()
    {
        Check.Equal(
            LelantineFarmPointsPolicy.NormalKillPoints,
            1,
            "a normal farm monster is worth one point");
        Check.Equal(
            LelantineFarmPointsPolicy.EliteKillPoints,
            10,
            "an elite farm monster is worth ten points");
        Check.Equal(
            LelantineFarmPointsPolicy.BossKillPoints,
            1000,
            "the farm boss is worth a thousand points");
        Check.Equal(
            LelantineFarmPointsPolicy.NormalMonsterMaximumLowerLevelGap,
            10,
            "the normal-monster level gap is ten");

        Check.True(
            LelantineFarmPointsPolicy.ResolveKillPoints(
                "normal", isElite: false, isBoss: false) == 1 &&
            LelantineFarmPointsPolicy.ResolveKillPoints(
                "elite", isElite: true, isBoss: false) == 10 &&
            LelantineFarmPointsPolicy.ResolveKillPoints(
                "boss", isElite: false, isBoss: true) == 1000,
            "rank selects the award");

        // A normal monster exactly ten levels lower still scores; one level
        // further down does not.
        Check.True(
            LelantineFarmPointsPolicy.IsKillEligible(
                playerLevel: 100,
                monsterLevel: 90,
                isElite: false,
                isBoss: false),
            "a normal monster ten levels lower still scores");
        Check.True(
            !LelantineFarmPointsPolicy.IsKillEligible(
                playerLevel: 100,
                monsterLevel: 89,
                isElite: false,
                isBoss: false),
            "a normal monster eleven levels lower does not score");
        Check.True(
            LelantineFarmPointsPolicy.IsKillEligible(
                playerLevel: 100,
                monsterLevel: 1,
                isElite: true,
                isBoss: false),
            "an elite scores regardless of the gap");
        Check.True(
            LelantineFarmPointsPolicy.IsKillEligible(
                playerLevel: 140,
                monsterLevel: 1,
                isElite: false,
                isBoss: true),
            "the boss scores regardless of the gap");
    }

    /// <summary>
    /// The captured farm click is <c>C2S 10069 {5194, 1, 282}</c>, so 282 has to
    /// resolve to map 42 from either capital's transporter, on the transport
    /// dialog index, with every trailing argument untouched.
    /// </summary>
    private static void CheckFarmEntryRoute()
    {
        foreach (var (npcKey, npcId, mapId, camp, sparta) in new[]
                 {
                     ("Sparta_056", 5053u, (byte)0, (byte)0, true),
                     ("Athens_056", 5195u, (byte)1, (byte)1, false)
                 })
        {
            foreach (var subId in new[]
                     {
                         BattlefieldTransporterProtocol.LelantineFarmSubId,
                         BattlefieldTransporterProtocol
                             .LelantineFarmSingleChannelSubId
                     })
            {
                Check.True(
                    BattlefieldTransporterProtocol.TryResolveDestination(
                        npcKey,
                        npcId,
                        mapId,
                        camp,
                        BattlefieldTransporterProtocol.DialogIndex,
                        subId,
                        Arguments(),
                        out var destination),
                    $"farm entry {subId} resolves from {npcKey}");                Check.True(
                    destination.Kind ==
                        BattlefieldDestinationKind.LelantineFarm,
                    $"farm entry {subId} from {npcKey} kind");
                Check.Equal(
                    LelantineFarmProtocol.MapId,
                    destination.TargetMapId,
                    $"farm entry {subId} from {npcKey} target map");
                Check.Equal(
                    BattlefieldTransporterProtocol.LelantineFarmMinimumLevel,
                    destination.MinimumLevel,
                    $"farm entry {subId} minimum level");
                Check.Equal(
                    BattlefieldTransporterProtocol.LelantineFarmMaximumLevel,
                    destination.MaximumLevel,
                    $"farm entry {subId} maximum level");

                // Each faction lands on its own base, which is the pair the
                // farm's own Address.ini publishes.
                var (x, z) = LelantineFarmProtocol.Arrival(athenian: !sparta);
                Check.Equal(x, destination.TargetX, $"farm entry {subId} X");
                Check.Equal(z, destination.TargetZ, $"farm entry {subId} Z");
            }
        }

        // The same sub id must not resolve from the wrong faction pair, and a
        // farm click under the wrong dialog index must fail closed.
        Check.True(
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 1, 0,
                BattlefieldTransporterProtocol.DialogIndex,
                BattlefieldTransporterProtocol.LelantineFarmSubId,
                Arguments(),
                out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 0, 0,
                BattlefieldTransporterProtocol.PindusResultDialogIndex,
                BattlefieldTransporterProtocol.LelantineFarmSubId,
                Arguments(),
                out _),
            "farm entrance is bound to its own faction capital and dialog");

        // The published farm refusal lines are the ones the client draws inside
        // the activity window, so they must live on dialog index 47.
        Check.Equal(
            LelantineFarmProtocol.FarmDialogIndex,
            BattlefieldTransporterProtocol.LelantineFarmResultDialogIndex,
            "farm refusals are answered inside the activity dialog index");
        Check.True(
            BattlefieldTransporterProtocol.LelantineFarmLevelResultSubId ==
                7005 &&
            BattlefieldTransporterProtocol.LelantineFarmClosedResultSubId ==
                7004,
            "farm refusal sub ids are the client's published level and time lines");
    }

    /// <summary>
    /// FINALISE-PETS (the admin 2026-11-05): the farm admits players only during its round, Saturday
    /// 22:45-23:30 Asia/Manila (the round clock's own window), unless event.lelantine_always_open = 1.
    /// (The method keeps its old name so the check list is unchanged.)
    /// </summary>
    private static void CheckFarmWindowIsAlwaysOpen()
    {
        var calendar = Realm;
        var manila = TimeSpan.FromHours(8);
        // Saturday 2026-11-07 is a round day.
        foreach (var (local, open) in new (DateTimeOffset Local, bool Open)[]
                 {
                     (new DateTimeOffset(2026, 11, 7, 22, 44, 59, manila), false),
                     (new DateTimeOffset(2026, 11, 7, 22, 45, 0, manila), true),
                     (new DateTimeOffset(2026, 11, 7, 23, 29, 59, manila), true),
                     (new DateTimeOffset(2026, 11, 7, 23, 30, 0, manila), false),
                     (new DateTimeOffset(2026, 11, 6, 22, 50, 0, manila), false),
                     (new DateTimeOffset(2026, 11, 8, 22, 50, 0, manila), false),
                     (new DateTimeOffset(2026, 11, 7, 12, 0, 0, manila), false)
                 })
        {
            Check.True(
                BattlefieldSchedulePolicy.IsOpen(
                    BattlefieldDestinationKind.LelantineFarm,
                    calendar,
                    local) == open,
                $"farm is {(open ? "open" : "closed")} at {local:ddd HH:mm:ss} Manila");
        }

        Check.True(
            BattlefieldSchedulePolicy.IsOpen(
                BattlefieldDestinationKind.LelantineFarm,
                calendar,
                new DateTimeOffset(2026, 11, 7, 22, 50, 0, manila)) ==
            LelantineFarmRoundPolicy.IsOpen(new DateTimeOffset(2026, 11, 7, 22, 50, 0, manila), calendar), // FINALISE2: realm clock
            "farm entry follows the round clock");

        Check.Equal(
            BattlefieldTransporterProtocol.LelantineFarmMinimumLevel,
            31,
            "farm level floor matches its published refusal text");
        Check.Equal(
            BattlefieldTransporterProtocol.LelantineFarmMaximumLevel,
            200,
            "farm level ceiling is 200 (FINALISE2, the admin F-68: no cap)");
    }

    /// <summary>
    /// The farm's endpoints are its four activity actors plus its two Returning
    /// Helpers, each answerable only under its own dialog index.
    /// </summary>
    private static void CheckFarmRosterContract()
    {
        Check.Equal(
            LelantineFarmProtocol.MapId,
            42,
            "farm map id is the published Lelantine_Farm row");
        Check.Equal(
            LelantineFarmProtocol.FarmDialogIndex,
            47,
            "farm activity dialog index is the captured NpcFunFarm number");
        Check.Equal(
            LelantineFarmProtocol.TeleportDialogIndex,
            1,
            "farm return uses the stock transport dialog index");

        Check.Equal(
            LelantineFarmProtocol.Npcs.Length,
            LelantineFarmProtocol.AllowedFarmNpcIds.Length,
            "farm roster and reserved id band agree");
        Check.True(
            LelantineFarmProtocol.Npcs.Select(static npc => npc.ObjectId)
                .SequenceEqual(LelantineFarmProtocol.AllowedFarmNpcIds),
            "farm roster ids are the reserved band in roster order");

        foreach (var npc in LelantineFarmProtocol.Npcs)
        {
            Check.True(
                LelantineFarmProtocol.TryResolve(
                    npc.NpcKey,
                    npc.ObjectId,
                    out var resolved) &&
                resolved == npc,
                $"farm endpoint {npc.NpcKey} resolves by key and id");
            Check.True(
                !LelantineFarmProtocol.TryResolve(
                    npc.NpcKey,
                    npc.ObjectId + 1u,
                    out _),
                $"farm endpoint {npc.NpcKey} rejects a foreign id");
        }

        Check.True(
            LelantineFarmProtocol.IsReturningHelper("Lelantine_Farm_005") &&
            LelantineFarmProtocol.IsReturningHelper("Lelantine_Farm_008") &&
            !LelantineFarmProtocol.IsReturningHelper("Lelantine_Farm_003") &&
            !LelantineFarmProtocol.IsReturningHelper("Lelantine_Farm_004"),
            "only the two Returning Helpers own the capital teleport");

        Check.True(
            LelantineFarmProtocol.IsFarmNpcKey("Lelantine_Farm_003") &&
            !LelantineFarmProtocol.IsFarmNpcKey("Athens_056"),
            "farm keys are recognised by their published prefix");

        // The two score ledgers answer for the two camps the character row
        // carries, and nothing else.
        Check.True(
            LelantineFarmPointsPolicy.TryResolveFaction(0, out var sparta) &&
            sparta == LelantineFarmPointsPolicy.SpartaFaction &&
            LelantineFarmPointsPolicy.TryResolveFaction(1, out var athens) &&
            athens == LelantineFarmPointsPolicy.AthensFaction &&
            !LelantineFarmPointsPolicy.TryResolveFaction(2, out _),
            "farm scores are kept for exactly the two factions");
    }

    /// <summary>
    /// Only the farm's window moved: every other battlefield keeps the published
    /// realm-calendar gate.
    /// </summary>
    private static void CheckNonFarmWindowsAreUnchanged()
    {
        var calendar = Realm;
        // Pindus opens Wednesday 21:00 realm time for 45 minutes; an hour later
        // than any of its starts it must be shut.
        var wednesday = NextInstantOn(DayOfWeek.Wednesday, 23);
        Check.True(
            !BattlefieldSchedulePolicy.IsOpen(
                BattlefieldDestinationKind.Pindus,
                calendar,
                wednesday),
            "Pindus keeps its published window");
        Check.True(
            !BattlefieldSchedulePolicy.IsOpen(
                BattlefieldDestinationKind.NiMiniUpper,
                calendar,
                wednesday),
            "Ni Mini keeps its published window");
        Check.True(
            BattlefieldSchedulePolicy.IsOpen(
                BattlefieldDestinationKind.DuelArena,
                calendar,
                wednesday),
            "the Duel Arena stays always open");
    }

    /// <summary>
    /// The next occurrence of one weekday and hour in realm time.
    /// </summary>
    private static DateTimeOffset NextInstantOn(DayOfWeek day, int hour)
    {
        var probe = new DateTimeOffset(
            2026,
            9,
            21,
            hour,
            0,
            0,
            TimeSpan.Zero);
        while (probe.DayOfWeek != day)
        {
            probe = probe.AddDays(1);
        }

        return probe;
    }

    /// <summary>
    /// The 18-word argument tail the stock transport click carries. The action
    /// word at index 0 is the selected sub id and every other word is the
    /// client's own -1, which is the exact path the protocol demands.
    /// </summary>
    private static int[] Arguments(params int[] path)
    {
        var arguments = new int[
            BattlefieldTransporterProtocol.FunctionArgumentCount];
        Array.Fill(arguments, -1);
        for (var index = 0; index < path.Length; index++)
        {
            arguments[index] = path[index];
        }

        return arguments;
    }
}
