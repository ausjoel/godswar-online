using Godswar.Server.Application.Gates;
using Godswar.Server.Application.Troy;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W2 F-27 TROY: the Trojan Expedition first slice. The Trojan War Coordinator (Athens_125 / Sparta_125) and the Event
/// Awarders (Athens_124 / Sparta_124) share function 67 (NpcFunTroy.lua); the capital Event Transporter's 900 button
/// is the way in. Three hot switches (troy.coordinator, troy.entry, troy.awards); each one at 0 leaves the captured
/// pages exactly as before. Rules and encodings: Application/Troy/TroyEvent.cs; notes: Development/troy/README.md.
/// </summary>
internal sealed partial class GameClientHandler
{
    // Slice 2: Agamemnon (Troy_003) and the Trojan War Transporter (Troy_001) on map 44 draw the same menu.
    private static bool IsTroyCoordinator(NpcSpawnDefinition npc) =>
        npc.NpcKey is "Athens_125" or "Sparta_125" or "Troy_001" or "Troy_003";

    private static bool IsTroyAwarder(NpcSpawnDefinition npc) => npc.NpcKey is "Athens_124" or "Sparta_124";

    /// <summary>The Event Transporter's menu: the captured five, plus 900 while troy.entry is on.</summary>
    private static ScriptedNpcDialogue WithTroyEntry(ScriptedNpcDialogue transporter) =>
        TroyEvent.EntryOn && !transporter.OpeningMenu.Contains(TroyEvent.TransporterEntry)
            ? transporter with { OpeningMenu = [.. transporter.OpeningMenu, TroyEvent.TransporterEntry] }
            : transporter;

    /// <summary>Hook in HandleScriptedNpcDialogueAsync. False = not a Troy click, or its switch is off.</summary>
    private async Task<bool> TryHandleTroyAsync(NpcSpawnDefinition npc, int dialogIndex, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        var path = DialogPath(packet);
        try
        {
            if (dialogIndex == 1 && npc.NpcKey is "Athens_072" or "Sparta_072" && TroyEvent.EntryOn &&
                path is [TroyEvent.TransporterEntry])
            {
                await HandleTroyEntryAsync(npc, cancellationToken);
                return true;
            }

            // Slice 2: the chests and transporters on map 44 answer their opening request (no buttons in the script).
            if (dialogIndex == TroyEvent.Function && path.Length == 0 && IsTroyMapNpc(npc))
            {
                return await HandleTroyMapNpcOpenAsync(npc, cancellationToken);
            }

            if (dialogIndex != TroyEvent.Function || path.Length == 0)
            {
                return false;
            }

            var coordinator = IsTroyCoordinator(npc);
            var awarder = IsTroyAwarder(npc);
            if (!coordinator && !awarder)
            {
                return false;
            }

            int[]? reply = path switch
            {
                [TroyEvent.RewardsEntry, TroyEvent.CheckScore] when TroyEvent.AwardsOn => await TroyRankingAsync(cancellationToken),
                [TroyEvent.RewardsEntry, TroyEvent.ClaimGuild] when TroyEvent.AwardsOn => await TroyClaimGuildAsync(cancellationToken),
                [TroyEvent.RewardsEntry, TroyEvent.ClaimPersonal] when TroyEvent.AwardsOn => await TroyClaimPersonalAsync(cancellationToken),
                // The coordinator draws the awarder's own 101 button (NF_L0_TR102 under the TR101 claim-deadline line),
                // so it opens the awarder's captured page (101 -> 201/202/203). Inferred, not captured for _125.
                [TroyEvent.RewardsEntry] when coordinator && TroyEvent.CoordinatorOn =>
                    [TroyEvent.CheckScore, TroyEvent.ClaimGuild, TroyEvent.ClaimPersonal],
                [TroyEvent.BackdropEntry] when coordinator && TroyEvent.CoordinatorOn => [TroyEvent.Backdrop],
                [TroyEvent.HpRecoveryEntry] when coordinator && TroyEvent.CoordinatorOn => await TroyHpRecoveryAsync(cancellationToken),
                [TroyEvent.MainCityEntry] when coordinator && TroyEvent.CoordinatorOn => await TroyMainCityAsync(npc, cancellationToken),
                _ => null
            };
            if (reply is null)
            {
                return false;
            }

            if (reply.Length > 0)
            {
                await SendFinalise2ReplyAsync(npc, TroyEvent.Function, reply, "TroyReply", cancellationToken);
            }

            Console.WriteLine($"[troy] npc={npc.NpcKey} character={_character.Name} path=[{string.Join(',', path)}] " +
                $"reply=[{string.Join(',', reply)}]");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was committed (the transaction rolled back); the session stays.
            Console.Error.WriteLine($"[troy] fault npc={npc.NpcKey} path=[{string.Join(',', path)}]: " +
                $"{error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    private DateTime TroyRealmNow => _realmCalendar.ToRealmTime(DateTimeOffset.UtcNow).DateTime;

    private static IReadOnlyList<Application.Events.EventWindow> TroyWindows => GateRules.RoundWindows(TroyEvent.GateKey);

    // ---- entry ---------------------------------------------------------------------------------------------
    private async Task HandleTroyEntryAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        var character = _character!;
        var gmSkips = GateRules.GmMaySkip(TroyEvent.GateKey) && await HasOperatorAuthorityAsync(cancellationToken);
        var band = GateRules.Band(TroyEvent.GateKey, TroyEvent.MinimumLevel, GateCatalog.LevelCap);
        int? refusal = !gmSkips && !TroyEvent.IsOpen(_realmCalendar, DateTimeOffset.UtcNow) ? TroyEvent.TransporterClosed
            : !gmSkips && (character.Level < band.Minimum || character.Level > band.Maximum) ? TroyEvent.TransporterLevel
            : null;
        if (refusal is { } line)
        {
            await SendFinalise2ReplyAsync(npc, 1, [line], "TroyEntryRefused", cancellationToken);
            Console.WriteLine($"[troy] entry refused character={character.Name} level={character.Level} reply={line}");
            return;
        }

        if (character.CurrentMap == TroyEvent.MapId)
        {
            return;
        }

        var outcome = await TryBeginMapTransitionCoreAsync((byte)TroyEvent.MapId, TroyEvent.ArrivalX, TroyEvent.ArrivalZ,
            $"troy-entry:{npc.NpcKey}", cancellationToken, publishFightStateReset: true);
        Console.WriteLine($"[troy] entry character={character.Name} level={character.Level} outcome={outcome}");
    }

    /// <summary>Players still in Troy when the gate closes go to their capital (checked with the farm's 20 s tick).</summary>
    private async Task SendTroyStragglerHomeAsync(CancellationToken cancellationToken)
    {
        if (!TroyEvent.EntryOn || _character is not { } character || character.CurrentMap != TroyEvent.MapId ||
            character.CurrentHp <= 0 || _pendingMapTransition is not null ||
            TroyEvent.IsOpen(_realmCalendar, DateTimeOffset.UtcNow))
        {
            return;
        }

        if (GateRules.GmMaySkip(TroyEvent.GateKey) && await HasOperatorAuthorityAsync(cancellationToken))
        {
            return;
        }

        var outcome = await SendTroyHomeAsync("troy-over", cancellationToken);
        if (outcome == SceneTransitionOutcome.CommittedAwaitingReadiness && !_session.IsDisconnected)
        {
            await _session.SendAsync(PacketBuilder.ServerNote("The Trojan Expedition is over. You were sent back to your capital."),
                cancellationToken, "TroyOver");
        }
    }

    private Task<SceneTransitionOutcome> SendTroyHomeAsync(string source, CancellationToken cancellationToken)
    {
        var sparta = _character!.Camp == GameDefaults.SpartaCamp;
        return TryBeginMapTransitionAsync(sparta ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap,
            GameDefaults.StartingPositionX, GameDefaults.StartingPositionZ, source, cancellationToken);
    }

    // ---- coordinator ---------------------------------------------------------------------------------------
    private async Task<int[]> TroyMainCityAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        var outcome = await SendTroyHomeAsync($"troy-main-city:{npc.NpcKey}", cancellationToken);
        Console.WriteLine($"[troy] main city character={_character!.Name} outcome={outcome}");
        return [];
    }

    private async Task<int[]> TroyHpRecoveryAsync(CancellationToken cancellationToken)
    {
        var character = _character!;
        if (character.CurrentHp <= 0)
        {
            return [];
        }

        var left = await RunEventsNpcAsync(tx => tx.TrySpendSilverAsync(TroyEvent.HpRecoverySilver), (int?)null,
            cancellationToken);
        if (left is null)
        {
            return [TroyEvent.NotEnoughSilver];
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken); // the new Silver balance
        var live = _character!;
        int hp, mp;
        lock (live.VitalsSync)
        {
            live.CurrentHp = live.MaxHp;
            live.CurrentMp = live.MaxMp;
            live.MarkVitalsChanged();
            hp = live.CurrentHp;
            mp = live.CurrentMp;
        }

        await _session.SendAsync(PacketBuilder.PlayerVitalsUpdate(LocalPlayerObjectId, hp, mp), cancellationToken,
            "TroyHpRecoverySelf");
        await _registry.BroadcastToMapAsync(live.CurrentMap, PacketBuilder.PlayerVitalsUpdate(CurrentPlayerObjectId, hp, mp),
            cancellationToken, _session, "TroyHpRecoveryWorld");
        return [TroyEvent.HpMpFull];
    }

    // ---- scoring -------------------------------------------------------------------------------------------
    /// <summary>A kill on map 44 while the event is open scores for the killer and, through them, their guild.</summary>
    private async Task RecordTroyKillAsync(MonsterDamageResult damageResult, CancellationToken cancellationToken)
    {
        await RecordTroyHeroKillAsync(damageResult, cancellationToken); // slice 2: the hero sequence and its chests
        if (!TroyEvent.AwardsOn || _character is not { } character ||
            damageResult.Monster.Definition.MapId != TroyEvent.MapId ||
            EventsNpcRuntime.Store is not { } store ||
            !TroyEvent.IsOpen(_realmCalendar, DateTimeOffset.UtcNow))
        {
            return;
        }

        var template = _gameplayCatalogs.Content.MonsterTemplates.FirstOrDefault(candidate =>
            string.Equals(candidate.TemplateKey, damageResult.Monster.Definition.TemplateKey, StringComparison.Ordinal));
        var points = TroyEvent.PointsFor(template?.IsElite == true, template?.IsBoss == true);
        if (points <= 0)
        {
            return;
        }

        var day = TroyEvent.ScoringDay(TroyWindows, TroyRealmNow);
        try
        {
            var total = await store.ExecuteScalarAsync("""
                INSERT INTO public.troy_scores (event_day, character_id, guild_id, points)
                VALUES (@d, @c, (SELECT guild_id FROM public.guild_members WHERE character_id = @c), @p)
                ON CONFLICT (event_day, character_id) DO UPDATE
                    SET points = LEAST(public.troy_scores.points::bigint + EXCLUDED.points, 2000000)::integer,
                        guild_id = COALESCE(public.troy_scores.guild_id, EXCLUDED.guild_id), updated_at = now()
                RETURNING points;
                """, cancellationToken, ("d", day), ("c", character.Id), ("p", points));
            Console.WriteLine($"[troy] kill character={character.Name} monster=\"{damageResult.Monster.Definition.DisplayName}\" " +
                $"points={points} total={total} day={day:yyyy-MM-dd}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[troy] kill not credited character={character.Name}: {error.Message}");
        }
    }

    // ---- awarders ------------------------------------------------------------------------------------------
    private const string TroyTitleSweepSql =
        "DELETE FROM public.character_title_grants WHERE title_id = @t AND granted_by = 'troy' " +
        "AND granted_at < now() - make_interval(days => @days);";

    private const string TroyTopGuildSql = """
        SELECT guild_id FROM public.troy_scores WHERE event_day = @d AND guild_id IS NOT NULL
        GROUP BY guild_id HAVING SUM(points) > 0 ORDER BY SUM(points) DESC, guild_id LIMIT 1;
        """;

    private const string TroyGuildScoreSql =
        "SELECT COALESCE(SUM(points), 0)::bigint FROM public.troy_scores WHERE event_day = @d AND guild_id = @g;";

    private const string TroyGrantTitleSql = """
        INSERT INTO public.character_title_grants (character_id, title_id, granted_by, note)
        SELECT character_id, @t, 'troy', 'Top Greek Guild, Trojan Expedition ' || @d::text
        FROM public.troy_scores WHERE event_day = @d AND guild_id = @g AND points > 0 AND (@only = 0 OR character_id = @only)
        ON CONFLICT (character_id, title_id) DO UPDATE SET granted_at = transaction_timestamp()
            WHERE public.character_title_grants.granted_by = 'troy';
        """;

    /// <summary>The day "Check on the score" shows: the running event, else the latest one.</summary>
    private DateOnly TroyDisplayDay()
    {
        var now = TroyRealmNow;
        if (TroyEvent.IsOpen(_realmCalendar, DateTimeOffset.UtcNow))
        {
            return TroyEvent.ScoringDay(TroyWindows, now);
        }

        return GateRules.LatestWindow(TroyWindows, now) is { } window && GateRules.Mode(TroyEvent.GateKey) != GateSchedule.Always
            ? DateOnly.FromDateTime(window.Start)
            : DateOnly.FromDateTime(now);
    }

    private DateOnly? TroyClaimDay() => TroyEvent.ClaimDay(TroyWindows, TroyRealmNow,
        GateRules.Mode(TroyEvent.GateKey) == GateSchedule.Always);

    private static async Task<long?> TroyGuildOfAsync(EventsNpcTransaction tx) =>
        await tx.ScalarAsync("SELECT guild_id FROM public.guild_members WHERE character_id = @c;") is long guild ? guild : null;

    private Task<int[]> TroyRankingAsync(CancellationToken cancellationToken)
    {
        var day = TroyDisplayDay();
        return RunEventsNpcAsync<int[]>(async tx =>
        {
            await tx.ScalarAsync(TroyTitleSweepSql, ("t", TroyEvent.TopGuildTitle), ("days", TroyEvent.TitleDays));
            var top = await tx.ScalarAsync("""
                SELECT string_agg(s::text, ',' ORDER BY s DESC, g) FROM (
                    SELECT guild_id AS g, SUM(points)::bigint AS s FROM public.troy_scores
                    WHERE event_day = @d AND guild_id IS NOT NULL GROUP BY guild_id HAVING SUM(points) > 0
                    ORDER BY s DESC, g LIMIT 10) AS ranked;
                """, ("d", day)) as string;
            var scores = string.IsNullOrEmpty(top) ? [] : top.Split(',').Select(long.Parse).ToList();
            var guild = await TroyGuildOfAsync(tx);
            long? guildScore = guild is { } g ? (long)(await tx.ScalarAsync(TroyGuildScoreSql, ("d", day), ("g", g)))! : null;
            var own = await tx.ScalarAsync("SELECT points FROM public.troy_scores WHERE event_day = @d AND character_id = @c;",
                ("d", day)) is int points ? points : 0;
            return TroyEvent.RankingPage(scores, guildScore, own);
        }, [TroyEvent.NoRankings], cancellationToken);
    }

    private async Task<int[]> TroyClaimGuildAsync(CancellationToken cancellationToken)
    {
        if (TroyClaimDay() is not { } day)
        {
            return [TroyEvent.ClaimDeadline];
        }

        return await RunEventsNpcAsync<int[]>(async tx =>
        {
            await tx.ScalarAsync(TroyTitleSweepSql, ("t", TroyEvent.TopGuildTitle), ("days", TroyEvent.TitleDays));
            if (await TroyGuildOfAsync(tx) is not { } guild)
            {
                return new[] { TroyEvent.NoGuild };
            }

            var score = (long)(await tx.ScalarAsync(TroyGuildScoreSql, ("d", day), ("g", guild)))!;
            if (score <= 0)
            {
                return [TroyEvent.NoGuild];
            }

            // "You are not the director": the guild leader (lord / duty 6 会长) claims. Deputies: question for the admin.
            if (await tx.ScalarAsync("""
                    SELECT (g.lord_character_id = @c OR m.duty = 6) FROM public.guild_members AS m
                    JOIN public.guilds AS g ON g.id = m.guild_id WHERE m.character_id = @c;
                    """) is not true)
            {
                return [TroyEvent.NotDirector];
            }

            var silver = TroyEvent.GuildSilver(score);
            var gold = TroyEvent.GuildGold(silver);
            if (await tx.ScalarAsync("""
                    INSERT INTO public.troy_guild_claims (event_day, guild_id, claimed_by, score, silver, gold)
                    VALUES (@d, @g, @c, @s, @silver, @gold) ON CONFLICT DO NOTHING RETURNING 1;
                    """, ("d", day), ("g", guild), ("s", score), ("silver", silver), ("gold", gold)) is null)
            {
                return [TroyEvent.GuildClaimed];
            }

            await tx.ScalarAsync("""
                UPDATE public.guilds SET silver = LEAST(silver + @silver, 2147483647),
                    gold = LEAST(gold + @gold, 2147483647), updated_at = now() WHERE id = @g;
                """, ("g", guild), ("silver", silver), ("gold", gold));
            var top = await tx.ScalarAsync(TroyTopGuildSql, ("d", day)) is long best && best == guild;
            if (top)
            {
                await tx.ScalarAsync(TroyGrantTitleSql, ("t", TroyEvent.TopGuildTitle), ("d", day), ("g", guild), ("only", 0));
            }

            Console.WriteLine($"[troy] guild reward guild={guild} score={score} silver={silver} gold={gold} top={top}");
            return top ? [TroyEvent.GuildRewardLine(silver), TroyEvent.TopGuildLine] : [TroyEvent.GuildRewardLine(silver)];
        }, [TroyEvent.NoGuild], cancellationToken);
    }

    private async Task<int[]> TroyClaimPersonalAsync(CancellationToken cancellationToken)
    {
        if (TroyClaimDay() is not { } day)
        {
            return [TroyEvent.ClaimDeadline];
        }

        var pills = 0;
        var reply = await RunEventsNpcAsync<int[]>(async tx =>
        {
            await tx.ScalarAsync(TroyTitleSweepSql, ("t", TroyEvent.TopGuildTitle), ("days", TroyEvent.TitleDays));
            var row = await tx.ScalarAsync(
                "SELECT personal_claimed FROM public.troy_scores WHERE event_day = @d AND character_id = @c AND points > 0;",
                ("d", day));
            if (row is null)
            {
                return new[] { TroyEvent.NotEntitled };
            }

            if (row is true)
            {
                return [TroyEvent.PersonalClaimed];
            }

            var points = (int)(await tx.ScalarAsync("""
                UPDATE public.troy_scores SET personal_claimed = true, updated_at = now()
                WHERE event_day = @d AND character_id = @c RETURNING points;
                """, ("d", day)))!;
            pills = TroyEvent.ExpPills(points);
            if (pills <= 0)
            {
                tx.Rollback();
                return [TroyEvent.NotEntitled];
            }

            // Exp Pills stack to 99 (template Overlap); a large score fills several slots.
            if (!await tx.GrantAsync(TroyEvent.ExpPillItem, pills, bound: true))
            {
                tx.Rollback();
                return [TroyEvent.BagFull];
            }

            // A member of the day's top guild who scored gets the week-long title here too, so it does not wait for
            // the leader's claim.
            if (await tx.ScalarAsync("SELECT guild_id FROM public.troy_scores WHERE event_day = @d AND character_id = @c;",
                    ("d", day)) is long guild &&
                await tx.ScalarAsync(TroyTopGuildSql, ("d", day)) is long best && best == guild)
            {
                await tx.ScalarAsync(TroyGrantTitleSql, ("t", TroyEvent.TopGuildTitle), ("d", day), ("g", guild),
                    ("only", tx.CharacterId));
            }

            return [TroyEvent.PersonalRewardLine(pills)];
        }, [TroyEvent.NotEntitled], cancellationToken);
        if (reply is [var line] && line % 1000 == 16 && line > 1000)
        {
            await RefreshAfterEventsNpcAsync([(TroyEvent.ExpPillItem, pills)], cancellationToken);
        }

        return reply;
    }
}
