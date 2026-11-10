using Godswar.Server.Infrastructure.EventsNpc;
using S = Godswar.Server.Application.Seasons.HalloweenMayDaySeason;

namespace Godswar.Server.Game;

/// <summary>
/// W3 seasons may day: the GENERATED May Day Envoy (Athens_MayDay / Sparta_MayDay) owns function 55 (NpcFunWorkday.lua,
/// 五一活动使者: submit Fertiliser, claim the reward) and 56 (NpcFunWorker.lua, 肥料供应商: one Fertiliser when none is
/// held). Entry point and gating: <see cref="TryHandleHalloweenMayDayAsync"/>. Rules:
/// <see cref="Application.Seasons.HalloweenMayDaySeason"/>.
/// </summary>
internal sealed partial class GameClientHandler
{
    private static readonly ScriptedNpcDialogue MayDayEnvoyDialogue = new(
        FunctionNumber: S.MayDayEnvoyFunction,
        OpeningMenu: [101, 102],
        Steps: new Dictionary<int, int[]>());

    /// <summary>The Fertiliser Supplier draws no menu: asking for its entries is the claim (210 = unavailable).</summary>
    private static readonly ScriptedNpcDialogue FertiliserSupplierDialogue = new(
        FunctionNumber: S.FertiliserSupplierFunction,
        OpeningMenu: [210],
        Steps: new Dictionary<int, int[]>());

    // ---- May Day Envoy, function 55 ---------------------------------------------------------------------------------

    private async Task MayDayEnvoyAsync(NpcSpawnDefinition npc, int[] path, DateTimeOffset now, CancellationToken token)
    {
        const int f = S.MayDayEnvoyFunction;
        if (path.Length == 0)
        {
            await HalloweenMayDayReplyAsync(npc, f, [101, 102], "MayDayOpen", token);
            return;
        }

        if (_character!.Level < S.MayDayMinimumLevel)
        {
            await HalloweenMayDayReplyAsync(npc, f, [230], "MayDayLevel", token);
            return;
        }

        switch (path[^1])
        {
            case 101:
                await SubmitFertiliserAsync(npc, now, token);
                return;
            case 102:
                await ClaimMayDayRewardAsync(npc, now, token);
                return;
            default:
                await HalloweenMayDayReplyAsync(npc, f, [210], "MayDayUnknownClick", token); // "Incorrect operation!"
                return;
        }
    }

    private async Task SubmitFertiliserAsync(NpcSpawnDefinition npc, DateTimeOffset now, CancellationToken token)
    {
        var year = S.SeasonYear(now);
        var camp = (short)_character!.Camp;
        var target = S.TreeTarget;
        var (result, taken, points) = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.ScalarAsync("SELECT winner_camp FROM public.may_day_result WHERE season_year = @y;",
                    ("y", year)) is not (null or DBNull))
                return (280, 0, 0);
            var held = await tx.CountAsync(S.Fertiliser);
            if (held <= 0) return (260, 0, 0);
            var used = await tx.DailyUsedAsync("may_day_fertiliser");
            if (used >= S.FertiliserDailyCap) return (250, 0, 0);
            await tx.ScalarAsync(
                "INSERT INTO public.may_day_tree (season_year, camp) VALUES (@y, @f) ON CONFLICT DO NOTHING;",
                ("y", year), ("f", camp));
            var before = Convert.ToInt32(await tx.ScalarAsync(
                "SELECT points FROM public.may_day_tree WHERE season_year = @y AND camp = @f FOR UPDATE;",
                ("y", year), ("f", camp)));
            var take = S.FertiliserTaken(held, used, before);
            if (take <= 0) return (280, 0, before); // the tree is full (the result row follows below on a race)
            if (!await tx.ConsumeAsync(S.Fertiliser, take))
            {
                tx.Rollback();
                return (260, 0, before);
            }

            var after = Convert.ToInt32(await tx.ScalarAsync(
                "UPDATE public.may_day_tree SET points = points + @a, updated_at = now() " +
                "WHERE season_year = @y AND camp = @f RETURNING points;", ("a", take), ("y", year), ("f", camp)));
            if (after >= target)
            {
                // The first tree to grow wins; the other faction's later completion finds the row and changes nothing.
                await tx.ScalarAsync(
                    "INSERT INTO public.may_day_result (season_year, winner_camp, reason) VALUES (@y, @f, 'grown') " +
                    "ON CONFLICT DO NOTHING;", ("y", year), ("f", camp));
            }

            await tx.ScalarAsync(
                "INSERT INTO public.may_day_contributions (character_id, season_year, camp, points) VALUES (@c, @y, @f, @a) " +
                "ON CONFLICT (character_id, season_year) DO UPDATE SET points = public.may_day_contributions.points + @a, " +
                "updated_at = now();", ("y", year), ("f", camp), ("a", take));
            await tx.DailyAddAsync("may_day_fertiliser", take);
            return (270, take, after);
        }, (210, 0, 0), token);
        Console.WriteLine($"[halloween-mayday] fertiliser character={_character.Name} camp={camp} year={year} " +
            $"taken={taken} tree={points}/{target} result={result}");
        if (result == 270) await RefreshAfterEventsNpcAsync([], token);
        await HalloweenMayDayReplyAsync(npc, S.MayDayEnvoyFunction, [result], "MayDaySubmit", token);
        if (result is 270 or 280) await SendMayDayProgressNoticeAsync(year, camp, token);
    }

    /// <summary>The en_us window speaks of key pieces; this line says what really happened, in plain English.</summary>
    private async Task SendMayDayProgressNoticeAsync(int year, short camp, CancellationToken token)
    {
        var (own, other, winner) = await RunEventsNpcAsync(async tx =>
        {
            var mine = await tx.ScalarAsync("SELECT points FROM public.may_day_tree WHERE season_year = @y AND camp = @f;",
                ("y", year), ("f", camp)) is { } a and not DBNull ? Convert.ToInt32(a) : 0;
            var theirs = await tx.ScalarAsync(
                "SELECT points FROM public.may_day_tree WHERE season_year = @y AND camp <> @f;",
                ("y", year), ("f", camp)) is { } b and not DBNull ? Convert.ToInt32(b) : 0;
            var won = await tx.ScalarAsync("SELECT winner_camp FROM public.may_day_result WHERE season_year = @y;",
                ("y", year)) is { } w and not DBNull ? Convert.ToInt32(w) : (int?)null;
            return (mine, theirs, won);
        }, (0, 0, (int?)null), token);
        var target = S.TreeTarget;
        var text = $"May Day faction tree: yours {own:N0} / {target:N0} Fertiliser, the enemy's {other:N0} / {target:N0}.";
        if (winner is { } decided)
            text += decided == camp || decided < 0
                ? " Your faction has won: claim your reward from the May Day Envoy."
                : " The enemy faction has won: claim your consolation reward from the May Day Envoy.";
        await SendEventsNpcNoticeAsync(text, "MayDayProgress", token);
    }

    private async Task ClaimMayDayRewardAsync(NpcSpawnDefinition npc, DateTimeOffset now, CancellationToken token)
    {
        var year = S.SeasonYear(now);
        var eventDay = S.IsEventDay(now, 5, 1);
        var target = S.TreeTarget;
        long experience = 0;
        var (result, pack) = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.ScalarAsync("SELECT winner_camp FROM public.may_day_result WHERE season_year = @y;",
                    ("y", year)) is null or DBNull)
            {
                if (!eventDay) return (480, 0);
                // 1 May with no tree grown (GENERATED): the first claim fixes the result from the standings, and
                // submissions stop (280) from then on.
                var sparta = await tx.ScalarAsync(
                    "SELECT points FROM public.may_day_tree WHERE season_year = @y AND camp = 0;", ("y", year))
                    is { } s and not DBNull ? Convert.ToInt32(s) : 0;
                var athens = await tx.ScalarAsync(
                    "SELECT points FROM public.may_day_tree WHERE season_year = @y AND camp = 1;", ("y", year))
                    is { } a and not DBNull ? Convert.ToInt32(a) : 0;
                await tx.ScalarAsync(
                    "INSERT INTO public.may_day_result (season_year, winner_camp, reason) VALUES (@y, @w, 'event day') " +
                    "ON CONFLICT DO NOTHING;", ("y", year), ("w", (short)S.EventDayWinner(sparta, athens)));
            }

            var winner = Convert.ToInt32(await tx.ScalarAsync(
                "SELECT winner_camp FROM public.may_day_result WHERE season_year = @y;", ("y", year)));
            var row = await tx.ScalarAsync(
                "SELECT camp::int * 2 + (CASE WHEN claimed THEN 1 ELSE 0 END) FROM public.may_day_contributions " +
                "WHERE character_id = @c AND season_year = @y FOR UPDATE;", ("y", year));
            if (row is null or DBNull) return (440, 0);
            var packed = Convert.ToInt32(row);
            if ((packed & 1) == 1) return (410, 0);
            var camp = (short)(packed >> 1); // the faction the Fertiliser was given for
            var grown = await tx.ScalarAsync(
                "SELECT points FROM public.may_day_tree WHERE season_year = @y AND camp = @f;", ("y", year), ("f", camp))
                is { } p and not DBNull && Convert.ToInt32(p) >= target;
            var (packId, exp) = S.ClaimReward(winner == camp || winner < 0, grown, _character!.Level);
            if (!await tx.GrantAsync(packId, 1, bound: true))
            {
                tx.Rollback();
                return (420, 0);
            }

            await tx.ScalarAsync(
                "UPDATE public.may_day_contributions SET claimed = true, updated_at = now() " +
                "WHERE character_id = @c AND season_year = @y;", ("y", year));
            experience = exp;
            return (430, packId);
        }, (480, 0), token);
        Console.WriteLine($"[halloween-mayday] claim character={_character!.Name} year={year} pack={pack} " +
            $"exp={experience} result={result}");
        if (result == 430) await RefreshAfterEventsNpcAsync([], token);
        await HalloweenMayDayReplyAsync(npc, S.MayDayEnvoyFunction, [result], "MayDayClaim", token);
        if (result == 430 && experience > 0) await PayEventsNpcExperienceAsync(experience, "MayDayTree", token);
    }

    // ---- Fertiliser Supplier, function 56 ---------------------------------------------------------------------------

    private async Task FertiliserSupplierAsync(NpcSpawnDefinition npc, DateTimeOffset now, CancellationToken token)
    {
        const int f = S.FertiliserSupplierFunction;
        if (_character!.Level < S.MayDayMinimumLevel)
        {
            await HalloweenMayDayReplyAsync(npc, f, [240], "FertiliserLevel", token); // draws NF_L0_WKD230 (Lv30)
            return;
        }

        var year = S.SeasonYear(now);
        var result = await RunEventsNpcAsync(async tx =>
        {
            // GENERATED: nothing more to plant once the result is fixed or today's cap is used ("come back later").
            if (await tx.ScalarAsync("SELECT winner_camp FROM public.may_day_result WHERE season_year = @y;",
                    ("y", year)) is not (null or DBNull))
                return 210;
            if (await tx.DailyUsedAsync("may_day_fertiliser") >= S.FertiliserDailyCap) return 210;
            if (await tx.CountAsync(S.Fertiliser) > 0) return 230; // "You have some ... in your backpack"
            if (await tx.GrantAsync(S.Fertiliser, 1, bound: true)) return 260;
            tx.Rollback();
            return 220;
        }, 210, token);
        Console.WriteLine($"[halloween-mayday] supplier character={_character.Name} result={result}");
        if (result == 260) await RefreshAfterEventsNpcAsync([], token);
        await HalloweenMayDayReplyAsync(npc, f, [result], "FertiliserSupplier", token);
    }
}
