using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Seasons;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W3 Christmas: Santa Claus (Athens_035 / Sparta_035, function 109, client NpcFunSDAN.lua), only while the
/// "christmas" season is open. Pages and numbers: <see cref="ChristmasRules"/>. Gloves: 30 bound Santa Gloves once a
/// day. Model: Antlers typed into the page-3 form go into the faction's Gigantic Reindeer Model (christmas_model,
/// keyed by season year and faction, row-locked then UPDATE .. RETURNING) for EXP and Talent Points; levels 1-5 pay
/// once per character, year and level (christmas_claims). The typed amount is read from +0x38 like every other
/// edit-box form; it is UNPROVEN for this page, so the submission is logged word by word and anything unreadable is
/// answered "Wrong number!" (1100) with nothing taken.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int ChristmasFunction = 109;

    private static readonly ScriptedNpcDialogue SantaClausDialogue = new(
        FunctionNumber: ChristmasFunction,
        OpeningMenu: [1, 2],
        Steps: new Dictionary<int, int[]> { [2] = [3, 4, 5] });

    /// <summary>The antler form Santa sent and is waiting to see submitted.</summary>
    private sealed record ChristmasForm(uint NpcId, DateTimeOffset Expires);

    private ChristmasForm? _christmasForm;

    private async Task<bool> TryHandleChristmasAsync(NpcSpawnDefinition npc, GamePacket packet,
        CancellationToken cancellationToken)
    {
        var now = EventsNpcNow;
        if (_character is null || npc.NpcKey is not ("Athens_035" or "Sparta_035") ||
            !SeasonalEvents.IsOpen(SeasonalEvents.Christmas, now))
            return false;
        // Read everything from the payload before the first await (it is a span).
        var click = ResolveNpcFunctionSelection(packet.Payload);
        var amount = DialogAmount(packet);
        var words = EventsNpcWords(packet);
        try
        {
            var pending = _christmasForm is { } form && form.NpcId == npc.InteractionId && form.Expires >= now;
            // A submission may carry only the typed number with every click slot at -1 (the layout the Marathon form
            // allows for); the range is kept tight so an ordinary window-open request is never read as one.
            if (pending && (click >= 0 || amount is >= 1 and <= ChristmasRules.AntlersPerDay))
            {
                _christmasForm = null;
                Console.WriteLine($"[christmas] antler submit character={_character.Name} click={click} typed={amount} words:{words}");
                await SubmitAntlersAsync(npc, amount, cancellationToken);
                return true;
            }

            if (click < 0)
            {
                if (pending)
                    Console.WriteLine($"[christmas] form dropped by a fresh open character={_character.Name} words:{words}");
                _christmasForm = null; // a fresh window
                await SendChristmasReplyAsync(npc, _character.Level < ChristmasRules.MinimumLevel ? [100] : [1, 2],
                    "SantaOpen", cancellationToken);
                return true;
            }

            if (_character.Level < ChristmasRules.MinimumLevel)
            {
                Console.WriteLine($"[christmas] below Lv{ChristmasRules.MinimumLevel} click={click} ignored");
                return true;
            }

            switch (click)
            {
                case 1:
                    await ClaimSantaGlovesAsync(npc, cancellationToken);
                    return true;
                case 2:
                    await SendChristmasReplyAsync(npc, [3, 4, 5], "SantaModelMenu", cancellationToken);
                    return true;
                case 3:
                {
                    var (done, points) = await ReadChristmasStateAsync(cancellationToken);
                    var page = ChristmasRules.TurnInPage(done, points, ChristmasThresholds());
                    if (page[0] % 1000 == 105)
                        _christmasForm = new ChristmasForm(npc.InteractionId, now.AddMinutes(5));
                    await SendChristmasReplyAsync(npc, page, "SantaTurnInPage", cancellationToken);
                    return true;
                }
                case 4:
                {
                    var (_, points) = await ReadChristmasStateAsync(cancellationToken);
                    var thresholds = ChristmasThresholds();
                    await SendChristmasReplyAsync(npc, ChristmasRules.ProgressPage(points, thresholds), "SantaProgress",
                        cancellationToken);
                    // The client's page prints its own 5,000-50,000; this line gives this server's numbers.
                    await SendEventsNpcNoticeAsync(
                        $"Gigantic Reindeer Model: {points:N0} Antlers, Level {ChristmasRules.Level(points, thresholds)}. " +
                        $"Levels 1-5 need {string.Join(" / ", thresholds.Select(static t => t.ToString("N0")))} Antlers.",
                        "SantaProgressNotice", cancellationToken);
                    return true;
                }
                case 5:
                {
                    var (_, points) = await ReadChristmasStateAsync(cancellationToken);
                    await SendChristmasReplyAsync(npc, ChristmasRules.ClaimMenu(points, ChristmasThresholds()),
                        "SantaClaimMenu", cancellationToken);
                    return true;
                }
                case >= 6 and <= 10:
                    await ClaimReindeerLevelAsync(npc, ChristmasRules.ClaimLevel(click), cancellationToken);
                    return true;
                default:
                    Console.WriteLine($"[christmas] unknown click={click} character={_character.Name} words:{words}");
                    return true;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was committed (the transaction rolled back); the session stays.
            Console.Error.WriteLine($"[christmas] fault npc={npc.NpcKey} click={click}: {error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    private static int[] ChristmasThresholds() =>
        ChristmasRules.Thresholds(Finalise2Rates.Amount(Finalise2RateKeys.ChristmasThresholdPercent));

    private int ChristmasYear => SeasonalEvents.SeasonYear(SeasonalEvents.Christmas, EventsNpcNow);

    private Task SendChristmasReplyAsync(NpcSpawnDefinition npc, int[] reply, string reason,
        CancellationToken cancellationToken) =>
        SendEventsNpcReplyAsync(npc, ChristmasFunction, reply, reason, cancellationToken);

    /// <summary>Antlers this character turned in today and the faction model's points this season.</summary>
    private Task<(int Done, int Points)> ReadChristmasStateAsync(CancellationToken cancellationToken)
    {
        var year = ChristmasYear;
        var camp = (short)_character!.Camp;
        return RunEventsNpcAsync(async tx =>
        {
            var done = await tx.DailyUsedAsync("christmas_antlers");
            var points = await tx.ScalarAsync(
                "SELECT points FROM public.christmas_model WHERE season_year = @y AND camp = @f;",
                ("y", year), ("f", camp)) is int value ? value : 0;
            return (done, points);
        }, (ChristmasRules.AntlersPerDay, 0), cancellationToken);
    }

    private async Task ClaimSantaGlovesAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        var result = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.DailyUsedAsync("christmas_gloves") > 0) return 103;
            if (!await tx.GrantAsync(ChristmasRules.SantaGloves, ChristmasRules.GlovesPerDay, bound: true))
            {
                tx.Rollback(); // a partial grant must not stay
                return 101;
            }

            await tx.DailyAddAsync("christmas_gloves", 1);
            return 102;
        }, 101, cancellationToken);
        Console.WriteLine($"[christmas] gloves character={_character!.Name} result={result}");
        if (result == 102) await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendChristmasReplyAsync(npc, [result], "SantaGloves", cancellationToken);
    }

    private async Task SubmitAntlersAsync(NpcSpawnDefinition npc, int amount, CancellationToken cancellationToken)
    {
        var year = ChristmasYear;
        var camp = (short)_character!.Camp;
        var thresholds = ChristmasThresholds();
        var experiencePerAntler = Finalise2Rates.Amount(Finalise2RateKeys.ChristmasAntlerExperience);
        var (result, accepted, points) = amount switch
        {
            <= 0 => (1100, 0, 0), // nothing readable typed: "Wrong number!", nothing taken
            > ChristmasRules.AntlersPerDay => (112, 0, 0),
            _ => await RunEventsNpcAsync(async tx =>
            {
                if (amount > ChristmasRules.AntlersPerDay - await tx.DailyUsedAsync("christmas_antlers"))
                    return (112, 0, 0);
                if (await tx.CountAsync(ChristmasRules.Antler) < amount) return (110, 0, 0);
                await tx.ScalarAsync(
                    "INSERT INTO public.christmas_model (season_year, camp) VALUES (@y, @f) ON CONFLICT DO NOTHING;",
                    ("y", year), ("f", camp));
                var before = (int)(await tx.ScalarAsync(
                    "SELECT points FROM public.christmas_model WHERE season_year = @y AND camp = @f FOR UPDATE;",
                    ("y", year), ("f", camp)))!;
                // The model stops at level 5; only what it still needs is taken.
                var taken = Math.Min(amount, ChristmasRules.Room(before, thresholds));
                if (taken <= 0) return (1100, 0, before);
                if (!await tx.ConsumeAsync(ChristmasRules.Antler, taken))
                {
                    tx.Rollback();
                    return (110, 0, before);
                }

                var after = (int)(await tx.ScalarAsync(
                    "UPDATE public.christmas_model SET points = points + @a, updated_at = now() " +
                    "WHERE season_year = @y AND camp = @f RETURNING points;",
                    ("a", taken), ("y", year), ("f", camp)))!;
                await tx.DailyAddAsync("christmas_antlers", taken);
                var (_, talentPoints) = ChristmasRules.TurnInReward(taken, experiencePerAntler);
                await tx.AddTalentPointsAsync(talentPoints);
                return (111, taken, after);
            }, (1100, 0, 0), cancellationToken)
        };
        Console.WriteLine($"[christmas] antlers character={_character.Name} camp={camp} year={year} typed={amount} " +
            $"taken={accepted} model={points} result={result}");
        if (result == 111) await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendChristmasReplyAsync(npc, [result], "SantaTurnIn", cancellationToken);
        if (result == 111)
        {
            var (experience, _) = ChristmasRules.TurnInReward(accepted, experiencePerAntler);
            if (experience > 0) await PayEventsNpcExperienceAsync(experience, "ChristmasAntlers", cancellationToken);
        }
    }

    private async Task ClaimReindeerLevelAsync(NpcSpawnDefinition npc, int level, CancellationToken cancellationToken)
    {
        var (_, points) = await ReadChristmasStateAsync(cancellationToken);
        var year = ChristmasYear;
        var camp = (short)_character!.Camp;
        var (bindingGold, experience) = ChristmasRules.ClaimReward(level);
        var result = ChristmasRules.Level(points, ChristmasThresholds()) < level
            ? 1000 * level + 115
            : await RunEventsNpcAsync(async tx =>
            {
                if (await tx.ScalarAsync(
                        "INSERT INTO public.christmas_claims (character_id, season_year, level, camp) " +
                        "VALUES (@c, @y, @l, @f) ON CONFLICT DO NOTHING RETURNING 1;",
                        ("y", year), ("l", (short)level), ("f", camp)) is null)
                    return 114;
                await tx.AddBindingGoldAsync(bindingGold);
                return 113;
            }, 114, cancellationToken);
        Console.WriteLine($"[christmas] claim character={_character.Name} camp={camp} year={year} level={level} " +
            $"model={points} result={result}");
        if (result == 113) await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendChristmasReplyAsync(npc, [result], "SantaClaim", cancellationToken);
        if (result == 113) await PayEventsNpcExperienceAsync(experience, "ChristmasModelLevel", cancellationToken);
    }
}
