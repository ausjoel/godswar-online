using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// The Lelantine Farm defence window ("利兰丁农场保卫战"), the client's
/// <c>NpcFunFarm.lua</c> under dialog index 47.
/// </summary>
/// <remarks>
/// <para>
/// Only the Advance Troop Captain owns this window. The capture answers
/// <c>C2S 10067 {5615}</c> with <c>S2C 10067 {5615, 0x200, 47,
/// "Lelantine_Farm_003"}</c> and then <c>S2C 10070 {5615, 47, 101, 102, 103,
/// 104}</c> for the root page. The Quartermaster (<c>Lelantine_Farm_004</c>) is
/// a shop instead: the same capture answers it with
/// <c>S2C 10067 {5616, 4, 0, "Lelantine_Farm_004"}</c>, the shop flags word,
/// and follows it with an opcode-10071 catalogue of the five tuck nets.
/// </para>
/// <para>
/// Every number below is a captured value. Where the capture does not show what
/// a particular click answers - the client sent the same <c>-1</c> page request
/// for every button, so the capture cannot attribute a reply to a specific
/// click - this handler answers with the group the capture does show for that
/// flow rather than inventing a new one.
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task HandleLelantineFarmAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            _account is null ||
            dialogIndex != LelantineFarmProtocol.FarmDialogIndex ||
            !LelantineFarmProtocol.IsAdvanceTroopCaptain(route.NpcKey) ||
            !LelantineFarmProtocol.TryResolve(route.NpcKey, npcId, out var npc))
        {
            Console.Error.WriteLine(
                "[farm] rejected malformed farm action " +
                $"character={_character?.Name ?? "<none>"} npc={npcId} " +
                $"key={route.NpcKey} dialog={dialogIndex} sub={subId}");
            return;
        }

        // The click carries the selected button at frame offset 20, which is
        // payload offset 16 - the same word every scripted NPC dialogue reads.
        var selection = arguments.Count > 0 ? arguments[0] : subId;
        if (selection < 0)
        {
            selection = subId;
        }

        if (selection < 0)
        {
            // The client is asking for the page it just opened, which is the
            // captured root page: S2C 10070 {5615, 47, 101, 102, 103, 104}.
            await SendFarmReplyAsync(
                npcId,
                LelantineFarmProtocol.CaptainMenuSubIds,
                cancellationToken);
            return;
        }

        Console.WriteLine(
            $"[farm] click character={_character.Name} npc={npcId} " +
            $"key={npc.NpcKey} faction={(npc.Athenian ? "athens" : "sparta")} " +
            $"sub={selection} subId={subId} " +
            $"amount={DialogAmount(packet)} " +
            $"args=[{string.Join(",", arguments)}] length={packet.Length}");

        switch (selection)
        {
            // Root-page entries. The capture answers the root page itself with
            // the four entries below, so the client asking for a page it has not
            // navigated yet gets the root page again.
            case LelantineFarmProtocol.FarmDonatePageSecond:
                await SendFarmReplyAsync(
                    npcId,
                    [LelantineFarmProtocol.FarmDonateAllPage],
                    cancellationToken);
                return;

            // "捐赠宠物卵" answers the donation page; its bulk button then books
            // the donation. Captured: {47, 111} at 21:00:52, and the donation
            // result lines are the script's own 51/52/53.
            case LelantineFarmProtocol.FarmDonateAll:
                await HandleFarmDonationAsync(
                    npc,
                    npcId,
                    quantity: null,
                    bagSlot: null,
                    cancellationToken);
                return;
            // "查看战场排名" is the personal read - the client draws it with
            // "战场数据统计" and "你当前分数排名" - and "查看阵营积分" is the
            // faction read, whose labels name both camps. The capture answers
            // them with different groups ({11,12,13} and {23,24}), so they stay
            // separate here.
            case LelantineFarmProtocol.FarmRankingPage:
                await SendFarmScoreAsync(
                    npcId,
                    npc,
                    factionScore: false,
                    cancellationToken);
                return;

            case LelantineFarmProtocol.FarmFactionPointPage:
                await SendFarmScoreAsync(
                    npcId,
                    npc,
                    factionScore: true,
                    cancellationToken);
                return;

            // "战场介绍" opens the briefing buttons. The capture returns
            // {31, 32, 33, 34} for that flow, then the matching paragraph as each
            // button is picked.
            case LelantineFarmProtocol.FarmIntroPage:
                await SendFarmReplyAsync(
                    npcId,
                    LelantineFarmProtocol.CaptainBriefingButtons,
                    cancellationToken);
                return;

            case 31:
                await SendFarmReplyAsync(npcId, [60], cancellationToken);
                return;

            case 32:
                await SendFarmReplyAsync(npcId, [62], cancellationToken);
                return;

            case 33:
                await SendFarmReplyAsync(npcId, [63], cancellationToken);
                return;

            case 34:
                await SendFarmReplyAsync(npcId, [64], cancellationToken);
                return;

            default:
                break;
        }

        // The window's own confirm button. A submission's click path begins with
        // the confirm word rather than with a quantity; the number the player
        // typed into the page's input box rides on the measured amount word
        // (DialogAmount's payload + 0x38, the offset captured on the guild
        // altar's identical input pages, -1 when nothing was typed), and the egg
        // they put in the item control rides on the measured bag coordinate
        // (TryResolveSubmittedEggSlot).
        if (selection == LelantineFarmProtocol.WindowConfirmSubId)
        {
            if (!LelantineFarmProtocol.TryResolveSubmittedEggSlot(
                    arguments,
                    out var eggSlot))
            {
                // The script's own answer to an empty box:
                // "请在框中放入犬宝宝卵！".
                await SendFarmReplyAsync(
                    npcId,
                    [LelantineFarmProtocol.DonationNoEgg],
                    cancellationToken);
                Console.WriteLine(
                    $"[farm] donation submission had no egg " +
                    $"character={_character.Name} npc={npcId} " +
                    $"args=[{string.Join(",", arguments)}]");
                return;
            }

            var amount = DialogAmount(packet);
            if (!LelantineFarmPointsPolicy.IsAcceptedQuantity(amount))
            {
                // The script's own answer to a count it cannot take:
                // "每次输入值必须在1~99".
                await SendFarmReplyAsync(
                    npcId,
                    [LelantineFarmProtocol.DonationOutOfRange],
                    cancellationToken);
                Console.WriteLine(
                    $"[farm] donation submission refused " +
                    $"character={_character.Name} npc={npcId} slot={eggSlot} " +
                    $"amount={amount}");
                return;
            }

            await HandleFarmDonationAsync(
                npc,
                npcId,
                amount,
                eggSlot,
                cancellationToken);
            return;
        }

        Console.WriteLine(
            $"[farm] unmatched branch npc={npcId} key={npc.NpcKey} sub={selection}");
    }

    /// <summary>
    /// Books a hound-egg donation and answers with the script's result line.
    /// </summary>
    /// <param name="quantity">
    /// The count the player typed, or <see langword="null"/> for the "donate
    /// every egg you carry" button.
    /// </param>
    /// <param name="bagSlot">
    /// The kit-bag slot the player placed the egg in, or <see langword="null"/>
    /// for the "donate every egg you carry" button, which takes the eggs the bag
    /// holds in slot order.
    /// </param>
    private async Task HandleFarmDonationAsync(
        LelantineFarmNpc npc,
        uint npcId,
        int? quantity,
        int? bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            _account is null ||
            !LelantineFarmPointsPolicy.TryResolveFaction(
                _character.Camp,
                out var faction) ||
            _farmPoints is null)
        {
            await SendFarmReplyAsync(
                npcId,
                [LelantineFarmProtocol.DonationNoEgg],
                cancellationToken);
            return;
        }

        var eggId = LelantineFarmPointsPolicy.HoundEggItemId;
        if (!LelantineFarmPointsPolicy.IsDonationEgg(eggId))
        {
            await SendFarmReplyAsync(
                npcId,
                [LelantineFarmProtocol.DonationNoEgg],
                cancellationToken);
            return;
        }

        // The script's own input box accepts 1..99, so the "donate everything"
        // button hands over at most that many in one booking and leaves the rest
        // for a second click rather than failing the whole batch.
        var requested = quantity ?? Math.Min(
            CountKitBagItem(eggId),
            LelantineFarmPointsPolicy.MaximumDonationQuantity);
        if (requested <= 0)
        {
            await SendFarmReplyAsync(
                npcId,
                [LelantineFarmProtocol.DonationNoEgg],
                cancellationToken);
            return;
        }

        if (!LelantineFarmPointsPolicy.IsAcceptedQuantity(requested))
        {
            await SendFarmReplyAsync(
                npcId,
                [LelantineFarmProtocol.DonationOutOfRange],
                cancellationToken);
            return;
        }

        var result = await _farmPoints.DonateHoundEggsAsync(
            _account.Id,
            _character.Id,
            faction,
            eggId,
            bagSlot,
            requested,
            cancellationToken);
        if (!result.Donated)
        {
            if (result.Character is { } refreshed)
            {
                InstallUpdatedCharacter(refreshed);
                await SendKitBagRefreshAsync(cancellationToken);
            }

            if (result.Status == FarmDonationStatus.NoEggInBox)
            {
                await SendFarmReplyAsync(
                    npcId,
                    [LelantineFarmProtocol.DonationNoEgg],
                    cancellationToken);
                Console.WriteLine(
                    $"[farm] donation had no egg in the box " +
                    $"character={_character.Name} slot={bagSlot}");
                return;
            }

            // The script's remaining refusal says the bag does not hold enough
            // eggs of that aptitude: "你包裹中当前资质宠物卵数量不足"
            // (NF_L0_FRAM452), which is about the eggs' aptitude as much as their
            // count, so it answers a stack that is short of the requested count
            // and a stack whose eggs carry an aptitude the activity does not
            // score.
            await SendFarmReplyAsync(
                npcId,
                [LelantineFarmProtocol.DonationNotEnough],
                cancellationToken);
            Console.WriteLine(
                $"[farm] donation refused character={_character.Name} " +
                $"slot={bagSlot} requested={requested} status={result.Status}");
            return;
        }

        InstallUpdatedCharacter(result.Character!);
        await SendKitBagRefreshAsync(cancellationToken);
        await SendFarmReplyAsync(
            npcId,
            [LelantineFarmProtocol.DonationAccepted],
            cancellationToken);
        var petExperience = await PayFarmDonationPetExperienceAsync(
            result.PointsAwarded,
            cancellationToken);
        Console.WriteLine(
            $"[farm] donation accepted character={_character.Name} " +
            $"faction={faction} slot={bagSlot} eggs={result.EggsDonated} " +
            $"points={result.PointsAwarded} " +
            $"factionTotal={result.FactionPoints} " +
            $"characterTotal={result.CharacterDonatedPoints} " +
            $"pet-exp={petExperience}");

        // A donation worth more than the activity's announcing threshold is
        // proclaimed to the whole map: the screen-centre note whose text the
        // client builds from its own table, naming the donor, their camp and the
        // points.
        if (LelantineFarmProtocol.IsAnnouncedDonation(result.PointsAwarded))
        {
            var recipients = await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.LelantineDonationBroadcast(
                    _character.Name,
                    faction == LelantineFarmPointsPolicy.AthensFaction,
                    result.PointsAwarded),
                cancellationToken,
                excludeSession: null,
                "LelantineFarmDonationBroadcast");
            Console.WriteLine(
                $"[farm] donation proclaimed character={_character.Name} " +
                $"faction={faction} points={result.PointsAwarded} " +
                $"map={_character.CurrentMap} recipients={recipients}");
        }
    }

    /// <summary>
    /// FINALISE-PETS: a donation also feeds the summoned pet (GENERATED
    /// <see cref="LelantineFarmPointsPolicy.PetExperiencePerPoint"/> per point: Cowardly 10,000, Rational
    /// 30,000, Brave 150,000 per egg). No summoned pet = the points stand and the pet experience is
    /// forfeited. Returns the experience the pet actually received (0 when none).
    /// </summary>
    private async Task<long> PayFarmDonationPetExperienceAsync(
        int pointsAwarded,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null || pointsAwarded <= 0)
        {
            return 0;
        }

        var experience = (int)Math.Min(
            100_000_000L,
            (long)pointsAwarded * LelantineFarmPointsPolicy.PetExperiencePerPoint);
        try
        {
            var pet = await _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(
                _account.Id,
                _character.Id,
                Guid.NewGuid(),
                experience,
                cancellationToken);
            if (pet is { HasPetProjection: true })
            {
                await _session.SendAsync(
                    PacketBuilder.PetExperience(pet.PetId!.Value, pet.TotalExperience!.Value),
                    cancellationToken,
                    "FarmDonationPetExperience");
                return pet.AwardedExperience;
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The eggs and points are already committed; a pet-experience fault must not undo them or end the
            // session.
            Console.Error.WriteLine(
                $"[farm] donation pet experience failed character={_character.Name} " +
                $"points={pointsAwarded}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Answers the activity's 即时比分查询.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The activity publishes two distinct reads: the faction score, whose
    /// client keys are "斯巴达阵营积分" and "雅典阵营积分", and the personal
    /// read, whose keys are "战场数据统计" and "你当前分数排名". The capture
    /// answers them as two different reply groups -
    /// <c>{47, 23, 24}</c> and <c>{47, 11, 12, 13}</c> - so they are served
    /// separately here too rather than collapsed into one page.
    /// </para>
    /// <para>
    /// The score values ride in the reply's argument words, which is where the
    /// capture carries a large number on the statistics answer; the argument
    /// word is also what the client draws beside those labels.
    /// </para>
    /// </remarks>
    private async Task SendFarmScoreAsync(
        uint npcId,
        LelantineFarmNpc npc,
        bool factionScore,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !LelantineFarmPointsPolicy.TryResolveFaction(
                _character.Camp,
                out var faction))
        {
            await SendFarmReplyAsync(npcId, [207], cancellationToken);
            return;
        }

        var scores = await _farmPoints.ReadFarmScoresAsync(
            _character.Id,
            faction,
            cancellationToken);
        if (scores is null)
        {
            await SendFarmReplyAsync(npcId, [207], cancellationToken);
            return;
        }

        if (factionScore)
        {
            // Both camps' totals, which is what the two faction-score labels
            // draw. Each camp's total is the sum of its members' personal scores;
            // both come from the same read.
            await SendFarmReplyAsync(
                npcId,
                [
                    LelantineFarmProtocol.EncodeScore(
                        LelantineFarmProtocol.SpartaScoreSelector,
                        scores.SpartaPoints),
                    LelantineFarmProtocol.EncodeScore(
                        LelantineFarmProtocol.AthensScoreSelector,
                        scores.AthensPoints)
                ],
                cancellationToken);
            Console.WriteLine(
                $"[farm] faction score query character={_character.Name} " +
                $"faction={faction} sparta={scores.SpartaPoints} " +
                $"athens={scores.AthensPoints} npc={npcId}");
            return;
        }

        // The personal read: the character's own score, the battlefield's
        // highest personal score, and where the character's own score stands
        // against it. The three values ride on the three selectors the client
        // draws them on, in the order the page stacks them.
        await SendFarmReplyAsync(
            npcId,
            [
                LelantineFarmProtocol.EncodeScore(
                    LelantineFarmProtocol.PersonalScoreSelector,
                    scores.CharacterPoints),
                LelantineFarmProtocol.EncodeScore(
                    LelantineFarmProtocol.HighestScoreSelector,
                    scores.HighestPoints),
                LelantineFarmProtocol.EncodeScore(
                    LelantineFarmProtocol.RankingSelector,
                    scores.CharacterRank)
            ],
            cancellationToken);
        Console.WriteLine(
            $"[farm] personal score query character={_character.Name} " +
            $"faction={faction} personal={scores.CharacterPoints} " +
            $"highest={scores.HighestPoints} rank={scores.CharacterRank} " +
            $"kills={scores.CharacterKillPoints} " +
            $"donated={scores.CharacterDonatedPoints} npc={npcId}");
    }

    private async Task SendFarmReplyAsync(
        uint npcId,
        IReadOnlyList<int> subIds,
        CancellationToken cancellationToken) =>
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                LelantineFarmProtocol.FarmDialogIndex,
                [.. subIds]),
            cancellationToken,
            "LelantineFarmResponse");

    /// <summary>
    /// Counts how many of one item the live character's kit bag carries.
    /// </summary>
    private int CountKitBagItem(int itemId)
    {
        var total = 0;
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            var entry = KitBagSlots.GetItem(_character?.KitBag ?? string.Empty, slot);
            if (!entry.IsEmpty && entry.Id == itemId)
            {
                total += entry.Stack;
            }
        }

        return total;
    }
}
