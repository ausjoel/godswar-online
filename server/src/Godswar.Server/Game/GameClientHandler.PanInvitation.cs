using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using P = Godswar.Server.Domain.World.Content.PanInvitationProtocol;

namespace Godswar.Server.Game;

/// <summary>
/// PAN CHAIN (W3, the admin F-45 / F-37): the Pan's Envoys (function 11) and the Avatar of Pan's certificate draw
/// (function 10). Switches GODSWAR_PAN_ENVOY / GODSWAR_PAN_AVATAR_DRAW; off = nothing routed, nothing answered.
/// </summary>
/// <remarks>
/// <para>Capital envoy (Sparta_090 / Athens_090), page 1 [1000, 1, 2, 3]:
/// 1 = 400 honour (medusa_honor_points) -> part 6, once a realm day;
/// 2 = the three-slot form (21); its submission carries the slots in arguments 6-8 (page*100 + slot), the layout the
///     Thanksgiving Envoy's real-client submit showed on 2026-11-08 -> any three different parts -> the letter;
/// 3 = all six parts -> the letter and Pan's Gift Certificate.</para>
/// <para>Treasure Land envoy (Agate_009 / Agate_010), page 1 [10001, 11, 12, 13]: one Level 1 Sapphire / Emerald /
/// Crystal -> part 1, once a realm day, and at most two map parts (1-4) a realm week (IGG acid=164).</para>
/// <para>Avatar of Pan (Pans_Labyrinth_010), function 10: the client script has no button of its own, only the
/// window's OK/Cancel, whose submit was never captured. Asking for the page (choosing the function) is therefore one
/// draw: one certificate -> one GENERATED gift (pan-avatar-draw.csv), at most ten a realm day. Any other click is
/// logged raw and not answered.</para>
/// <para>No notice follows a consumed item (the admin's rule): consumptions refresh the bag silently; only the gift / part
/// received is announced as an acquisition, like the other exchanges.</para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    private static readonly ScriptedNpcDialogue PanEnvoyDialogue = new(
        FunctionNumber: P.EnvoyFunction,
        OpeningMenu: P.CapitalOpening,
        Steps: new Dictionary<int, int[]>());

    private static readonly ScriptedNpcDialogue PanTreasureLandEnvoyDialogue = new(
        FunctionNumber: P.EnvoyFunction,
        OpeningMenu: P.TreasureLandOpening,
        Steps: new Dictionary<int, int[]>());

    private static readonly ScriptedNpcDialogue PanAvatarDrawDialogue = new(
        FunctionNumber: P.AvatarFunction,
        OpeningMenu: [],
        Steps: new Dictionary<int, int[]>());

    /// <summary>The Avatar of Pan's functions: the marriage necklace (41) and the certificate draw (10), each behind its own switch.</summary>
    private static Dictionary<int, ScriptedNpcDialogue>? PanAvatarDialogues()
    {
        var dialogues = new List<ScriptedNpcDialogue>(2);
        if (MarriagePolicy.IsEnabled())
        {
            dialogues.Add(PanNecklaceDialogue);
        }

        if (P.AvatarDrawEnabled)
        {
            dialogues.Add(PanAvatarDrawDialogue);
        }

        return dialogues.Count == 0 ? null : ByFunction([.. dialogues]);
    }

    private async Task<bool> TryHandlePanInvitationAsync(NpcSpawnDefinition npc, int dialogIndex, int subId,
        GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        var envoy = dialogIndex == P.EnvoyFunction && P.EnvoyEnabled &&
            (P.IsCapitalEnvoy(npc.NpcKey) || P.IsTreasureLandEnvoy(npc.NpcKey));
        // PAN CHAIN slice 2: the Cursed Zone envoys (part 2) behind their own switch.
        var cursed = dialogIndex == P.EnvoyFunction && P.CursedEnvoyEnabled && P.IsCursedZoneEnvoy(npc.NpcKey);
        envoy |= cursed;
        var avatar = dialogIndex == P.AvatarFunction && P.AvatarDrawEnabled && PansLabyrinthProtocol.IsAvatar(npc);
        if (!envoy && !avatar)
        {
            return false;
        }

        var click = MarriagePolicy.ResolveClick(ReadMarriageWords(packet).Path);
        var args = MountFeederArguments(packet);
        Console.WriteLine($"[pan-chain] click npc={npc.NpcKey} function={dialogIndex} character={_character.Name} " +
            $"sub={subId} path=[{string.Join(',', click.Path)}] zeros={click.TrailingZeros} args=[{string.Join(',', args)}]");
        try
        {
            if (avatar)
            {
                await HandlePanAvatarDrawAsync(npc, click, cancellationToken);
            }
            else if (cursed)
            {
                await HandleCursedZonePanEnvoyAsync(npc, click, args, cancellationToken);
            }
            else if (P.IsCapitalEnvoy(npc.NpcKey))
            {
                await HandleCapitalPanEnvoyAsync(npc, click, args, cancellationToken);
            }
            else
            {
                await HandleTreasureLandPanEnvoyAsync(npc, click, cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was committed (the transaction rolled back); the session stays.
            Console.Error.WriteLine($"[pan-chain] fault npc={npc.NpcKey} function={dialogIndex}: " +
                $"{error.GetType().Name}: {error.Message}");
        }

        return true;
    }

    // ---- Capital Pan's Envoy -------------------------------------------------------------------------------

    private async Task HandleCapitalPanEnvoyAsync(NpcSpawnDefinition npc, MarriageClick click, int[] args,
        CancellationToken cancellationToken)
    {
        if (click.Path.Length == 0)
        {
            await SendEventsNpcReplyAsync(npc, P.EnvoyFunction, P.CapitalOpening, "PanEnvoyOpen", cancellationToken);
            return;
        }

        var slots = new[] { SlotOf(args[6]), SlotOf(args[7]), SlotOf(args[8]) };
        var granted = new List<(int ItemId, int Quantity)>();
        int result;
        switch (click.Path[0])
        {
            case P.ButtonPartSix:
                result = await RunEventsNpcAsync(tx => PanPartSixAsync(tx, granted), P.FollowProcedure, cancellationToken);
                break;
            case P.ButtonLetter when slots[0] < 0 && slots[1] < 0 && slots[2] < 0:
                // The button itself opens the three-slot form; a form sent back empty is "follow the procedure".
                result = click.Path.Length == 1 && click.TrailingZeros == 0 ? P.LetterForm : P.FollowProcedure;
                break;
            case P.ButtonLetter:
                result = await RunEventsNpcAsync(tx => PanLetterFormAsync(tx, slots, granted), P.FollowProcedure,
                    cancellationToken);
                break;
            case P.ButtonLetterAndCertificate:
                result = await RunEventsNpcAsync(tx => PanAllSixAsync(tx, granted), P.FollowProcedure, cancellationToken);
                break;
            default:
                Console.WriteLine($"[pan-chain] capital envoy unregistered path=[{string.Join(',', click.Path)}]");
                return;
        }

        Console.WriteLine($"[pan-chain] capital envoy character={_character?.Name} button={click.Path[0]} " +
            $"slots=[{string.Join(',', slots)}] result={result}");
        if (result != P.LetterForm)
        {
            await RefreshAfterEventsNpcAsync(granted, cancellationToken);
            if (click.Path[0] == P.ButtonPartSix && result == P.PartSixGiven &&
                _character is { } refreshed && PacketBuilder.PlayerDetail(refreshed) is { Length: > 0 } detail)
            {
                await _session.SendAsync(detail, cancellationToken, "PanEnvoyHonor", framed: false);
            }
        }

        await SendEventsNpcReplyAsync(npc, P.EnvoyFunction, [result], "PanEnvoyResult", cancellationToken);
    }

    private static async Task<int> PanPartSixAsync(EventsNpcTransaction tx, List<(int, int)> granted)
    {
        if (await tx.DailyUsedAsync("pan_part6") > 0)
        {
            return P.OncePerDay;
        }

        if (!await tx.GrantAsync((int)P.PartSix, 1))
        {
            tx.Rollback();
            return P.BagFullPageTwo;
        }

        var spent = await tx.ScalarAsync("""
            UPDATE public.character_base
            SET medusa_honor_points = medusa_honor_points - @n, medusa_reward_revision = medusa_reward_revision + 1
            WHERE id = @c AND COALESCE(medusa_honor_points, 0) >= @n
            RETURNING medusa_honor_points;
            """, ("n", P.PartSixHonorCost));
        if (spent is null)
        {
            tx.Rollback();
            return P.NotEnoughHonor;
        }

        await tx.DailyAddAsync("pan_part6", 1);
        granted.Add(((int)P.PartSix, 1));
        return P.PartSixGiven;
    }

    private static async Task<int> PanLetterFormAsync(EventsNpcTransaction tx, int[] slots, List<(int, int)> granted)
    {
        var offered = new List<uint>(3);
        foreach (var slot in slots.Where(static slot => slot >= 0).Distinct())
        {
            if (await tx.ReadSlotAsync(slot) is not { } item || !P.IsPart((uint)item.ItemId))
            {
                return P.NotAllParts;
            }

            offered.Add((uint)item.ItemId);
        }

        if (offered.Count < 3)
        {
            return offered.Distinct().Count() == offered.Count ? P.NotAllParts : P.PartsNotDifferent;
        }

        if (offered.Distinct().Count() != 3)
        {
            return P.PartsNotDifferent;
        }

        foreach (var part in offered)
        {
            if (!await tx.ConsumeAsync((int)part, 1))
            {
                tx.Rollback();
                return P.NotAllParts;
            }
        }

        if (!await tx.GrantAsync((int)P.LetterItemId, 1))
        {
            tx.Rollback();
            return P.BagFullPageThree;
        }

        granted.Add(((int)P.LetterItemId, 1));
        return P.LetterGiven;
    }

    private static async Task<int> PanAllSixAsync(EventsNpcTransaction tx, List<(int, int)> granted)
    {
        foreach (var part in P.Parts)
        {
            if (await tx.CountAsync((int)part) < 1)
            {
                return P.NotAllSix;
            }
        }

        foreach (var part in P.Parts)
        {
            if (!await tx.ConsumeAsync((int)part, 1))
            {
                tx.Rollback();
                return P.NotAllSix;
            }
        }

        if (!await tx.GrantAsync((int)P.LetterItemId, 1) || !await tx.GrantAsync((int)P.CertificateItemId, 1))
        {
            tx.Rollback();
            return P.BagFullPageTwo;
        }

        granted.Add(((int)P.LetterItemId, 1));
        granted.Add(((int)P.CertificateItemId, 1));
        return P.LetterAndCertificateGiven;
    }

    // ---- Treasure Land Pan's Envoy (part 1) ----------------------------------------------------------------

    private async Task HandleTreasureLandPanEnvoyAsync(NpcSpawnDefinition npc, MarriageClick click,
        CancellationToken cancellationToken)
    {
        if (click.Path.Length == 0)
        {
            await SendEventsNpcReplyAsync(npc, P.EnvoyFunction, P.TreasureLandOpening, "PanEnvoyPartOneOpen",
                cancellationToken);
            return;
        }

        if (!P.GemForButton.TryGetValue(click.Path[0], out var gem))
        {
            Console.WriteLine($"[pan-chain] treasure land envoy unregistered path=[{string.Join(',', click.Path)}]");
            return;
        }

        var week = P.WeekKey(EventsNpcDay(EventsNpcNow));
        var granted = new List<(int ItemId, int Quantity)>();
        var result = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.DailyUsedAsync("pan_part1") > 0)
            {
                return P.OncePerDay;
            }

            if (await PanMapPartsThisWeekAsync(tx, week) >= P.MapPartsPerWeek)
            {
                return 104; // NF_L0_MSER104: "only allowed to exchange for part 1 twice per week"
            }

            if (!await tx.ConsumeAsync((int)gem, 1))
            {
                return P.NotEnoughItems;
            }

            if (!await tx.GrantAsync((int)P.PartOne, 1))
            {
                tx.Rollback();
                return P.BagFullPageTwo;
            }

            await tx.DailyAddAsync("pan_part1", 1);
            await PanAddMapPartAsync(tx, week);
            granted.Add(((int)P.PartOne, 1));
            return P.PartOneGiven;
        }, P.FollowProcedure, cancellationToken);
        Console.WriteLine($"[pan-chain] part 1 character={_character?.Name} gem={gem} result={result}");
        await RefreshAfterEventsNpcAsync(granted, cancellationToken);
        await SendEventsNpcReplyAsync(npc, P.EnvoyFunction, [result], "PanEnvoyPartOneResult", cancellationToken);
    }

    // ---- Cursed Zone Pan's Envoy (part 2), PAN CHAIN slice 2 ------------------------------------------------

    private static readonly ScriptedNpcDialogue PanCursedZoneEnvoyDialogue = new(
        FunctionNumber: P.EnvoyFunction,
        OpeningMenu: P.CursedZoneOpening,
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// NF_L0_MSER521: "Once a day, you can exchange 1 piece of equipment, delicate or above, or one with at least two
    /// options, for Pan's Invitation Part 2". Page 1 is [500] (text + one item slot, no buttons). The OK submit was never
    /// captured: the slot is read from argument 6 (page*100 + slot), the layout of the capital form and the other item
    /// forms; a click with a path is treated as the submit (an empty path redraws page 1), and a submit without a slot
    /// answers 253.
    /// Limits: once a realm day (pan_part2) and the shared two map parts a realm week (MSER103 on refusal).
    /// </summary>
    private async Task HandleCursedZonePanEnvoyAsync(NpcSpawnDefinition npc, MarriageClick click, int[] args,
        CancellationToken cancellationToken)
    {
        var slot = SlotOf(args[6]);
        // Safe side: only a click that carries a path is a submit. An empty path (choosing the function) always
        // redraws the page, so a stray argument 6 can never cost the player an item.
        if (click.Path.Length == 0)
        {
            await SendEventsNpcReplyAsync(npc, P.EnvoyFunction, P.CursedZoneOpening, "PanEnvoyPartTwoOpen", cancellationToken);
            return;
        }

        var templates = RequireItemContent().Templates;
        bool IsEquipment(int itemId) =>
            templates.TryGet((uint)itemId, out var template) && EquipmentSlots.IsEquipmentKind(template.Kind);
        var week = P.WeekKey(EventsNpcDay(EventsNpcNow));
        var granted = new List<(int ItemId, int Quantity)>();
        var result = slot < 0
            ? P.WrongEquipment
            : await RunEventsNpcAsync(tx => PanPartTwoAsync(tx, slot, week, IsEquipment, granted), P.FollowProcedure,
                cancellationToken);
        Console.WriteLine($"[pan-chain] part 2 character={_character?.Name} npc={npc.NpcKey} slot={slot} result={result}");
        await RefreshAfterEventsNpcAsync(granted, cancellationToken);
        await SendEventsNpcReplyAsync(npc, P.EnvoyFunction, [result], "PanEnvoyPartTwoResult", cancellationToken);
    }

    private static async Task<int> PanPartTwoAsync(EventsNpcTransaction tx, int slot, DateOnly week,
        Func<int, bool> isEquipment, List<(int, int)> granted)
    {
        if (await tx.DailyUsedAsync("pan_part2") > 0)
        {
            return P.OncePerDay;
        }

        if (await PanMapPartsThisWeekAsync(tx, week) >= P.MapPartsPerWeek)
        {
            return P.PartTwoTwiceAWeek;
        }

        if (await tx.ReadEquipmentSlotAsync(slot) is not { } item || !isEquipment(item.ItemId) ||
            !P.QualifiesForPartTwo(item.Quality, item.Options))
        {
            return P.WrongEquipment;
        }

        await tx.DeleteItemAsync(item.Id);
        if (!await tx.GrantAsync((int)P.PartTwo, 1))
        {
            tx.Rollback();
            return P.BagFullPageTwo;
        }

        await tx.DailyAddAsync("pan_part2", 1);
        await PanAddMapPartAsync(tx, week);
        granted.Add(((int)P.PartTwo, 1));
        return P.PartTwoGiven;
    }

    /// <summary>Map parts (1-4) taken this realm week: one shared counter keyed by the week's Monday.</summary>
    private static async Task<int> PanMapPartsThisWeekAsync(EventsNpcTransaction tx, DateOnly week) =>
        await tx.ScalarAsync(
            "SELECT used FROM public.finalise2_npc_daily WHERE character_id = @c AND day = @w AND counter = 'pan_map_parts' FOR UPDATE;",
            ("w", week)) is int used ? used : 0;

    private static async Task PanAddMapPartAsync(EventsNpcTransaction tx, DateOnly week) =>
        await tx.ScalarAsync("""
            INSERT INTO public.finalise2_npc_daily (character_id, day, counter, used) VALUES (@c, @w, 'pan_map_parts', 1)
            ON CONFLICT (character_id, day, counter) DO UPDATE SET used = finalise2_npc_daily.used + 1, updated_at = now();
            """, ("w", week));

    // ---- Avatar of Pan: certificate draw -------------------------------------------------------------------

    private async Task HandlePanAvatarDrawAsync(NpcSpawnDefinition npc, MarriageClick click,
        CancellationToken cancellationToken)
    {
        if (click.Path.Length != 0)
        {
            // The window's OK/Cancel submit (uncaptured): logged above, nothing answered.
            return;
        }

        if (_character is not { CurrentMap: PansLabyrinthProtocol.MapId })
        {
            return;
        }

        var granted = new List<(int ItemId, int Quantity)>();
        var roll = Random.Shared.Next(PanAvatarDrawTable.TotalWeight);
        var result = await RunEventsNpcAsync(async tx =>
        {
            var used = await tx.DailyUsedAsync("pan_draws");
            if (used >= P.DrawsPerDay)
            {
                return P.DrawLimitReached;
            }

            if (!await tx.ConsumeAsync((int)P.CertificateItemId, 1))
            {
                return P.NoCertificate;
            }

            var gift = PanAvatarDrawTable.Pick(roll);
            if (!await tx.GrantAsync((int)gift.ItemId, gift.Quantity, bound: null))
            {
                tx.Rollback();
                return P.DrawBagFull;
            }

            var now = await tx.DailyAddAsync("pan_draws", 1);
            granted.Add(((int)gift.ItemId, gift.Quantity));
            return P.DrawRemainingBase + Math.Max(0, now - 1);
        }, P.NoCertificate, cancellationToken);
        Console.WriteLine($"[pan-chain] avatar draw character={_character?.Name} result={result} " +
            $"gift=[{string.Join(',', granted.Select(static g => $"{g.ItemId}x{g.Quantity}"))}] (generated table)");
        await RefreshAfterEventsNpcAsync(granted, cancellationToken);
        await SendEventsNpcReplyAsync(npc, P.AvatarFunction, [result], "PanAvatarDraw", cancellationToken);
    }
}
