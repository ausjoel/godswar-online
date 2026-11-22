using Godswar.Server.Application.Awards;
using Godswar.Server.Infrastructure.EventsNpc;

namespace Godswar.Server.Infrastructure.Awards;

/// <summary>
/// W4-B: the database side of login-window awards, the Prize Chest and queued buffs (migration
/// 20261112_241_login_awards). Every method runs inside one <see cref="EventsNpcTransaction"/>, which holds the
/// character row lock, so a claim and what it pays commit together or not at all.
/// </summary>
internal static class LoginAwardLedger
{
    /// <summary>Enabled awards whose window contains <paramref name="now"/>, oldest first.</summary>
    public static async Task<IReadOnlyList<LoginAward>> ReadOpenAwardsAsync(EventsNpcTransaction tx, DateTimeOffset now)
    {
        var rows = await tx.RowsAsync("""
            SELECT id, name, starts_at, ends_at, per_scope, delivery, items::text, silver, gold, binding_gold,
                   buffs::text, title_id, min_level
            FROM public.login_window_awards
            WHERE enabled AND starts_at <= @now AND ends_at > @now
            ORDER BY id;
            """, ("now", now.UtcDateTime));
        var awards = new List<LoginAward>(rows.Count);
        foreach (var row in rows)
        {
            var id = Convert.ToInt32(row[0]);
            if (!LoginAwardPolicy.TryParseItems((string)row[6]!, out var items, out var itemError))
            {
                Console.WriteLine($"[login-award] award {id} skipped: {itemError}");
                continue;
            }

            if (!LoginAwardPolicy.TryParseBuffs((string)row[10]!, out var buffs, out var buffError))
            {
                Console.WriteLine($"[login-award] award {id} skipped: {buffError}");
                continue;
            }

            awards.Add(new LoginAward(id, (string)row[1]!, ToInstant(row[2]!), ToInstant(row[3]!),
                (string)row[4]! == "account", (string)row[5]! == "prize_chest", items,
                Convert.ToInt32(row[7]), Convert.ToInt32(row[8]), Convert.ToInt32(row[9]), buffs,
                row[11] is null ? null : Convert.ToInt32(row[11]), Convert.ToInt32(row[12])));
        }

        return awards;
    }

    /// <summary>
    /// Claims and pays every open award this character has not had yet. An award whose items do not fit in the
    /// bag sends its items to the Prize Chest instead (the currencies still go to the wallet), so a full bag never
    /// loses an award and never blocks it.
    /// </summary>
    public static async Task<IReadOnlyList<LoginAwardGrant>> GrantOpenAwardsAsync(
        EventsNpcTransaction tx, DateTimeOffset now, int accountId, int characterId, int level)
    {
        var grants = new List<LoginAwardGrant>();
        foreach (var award in await ReadOpenAwardsAsync(tx, now))
        {
            if (!LoginAwardPolicy.IsEligible(award, now, level))
            {
                continue;
            }

            if (await GrantAsync(tx, award, accountId, characterId) is { } grant)
            {
                grants.Add(grant);
            }
        }

        return grants;
    }

    /// <summary>Claims one award for this character; null when the account or character already has it.</summary>
    public static async Task<LoginAwardGrant?> GrantAsync(
        EventsNpcTransaction tx, LoginAward award, int accountId, int characterId)
    {
        var claimKey = LoginAwardPolicy.ClaimKey(award, accountId, characterId);
        var claimed = await tx.ScalarAsync("""
            INSERT INTO public.login_window_award_claims (award_id, claim_key, account_id, character_id, delivered_to)
            VALUES (@a, @k, @acc, @c, @d)
            ON CONFLICT (award_id, claim_key) DO NOTHING
            RETURNING 1;
            """, ("a", award.Id), ("k", claimKey), ("acc", accountId),
            ("d", award.ToPrizeChest ? "prize_chest" : "bag"));
        if (claimed is null)
        {
            return null;
        }

        var toChest = award.ToPrizeChest;
        var bagWasFull = false;
        var bagItems = new List<(int ItemId, int Quantity)>();
        var source = $"login-award:{award.Id}";
        if (!toChest && award.Items.Count > 0)
        {
            var savepoint = $"login_award_{award.Id}";
            await tx.SavepointAsync(savepoint);
            foreach (var item in award.Items)
            {
                if (!await tx.GrantVariantAsync(item.ItemId, item.Quantity, item.Bound, item.Quality, item.Grade))
                {
                    bagWasFull = true;
                    break;
                }

                bagItems.Add((item.ItemId, item.Quantity));
            }

            if (bagWasFull)
            {
                await tx.RollbackToSavepointAsync(savepoint);
                bagItems.Clear();
                toChest = true;
                await tx.ScalarAsync("""
                    UPDATE public.login_window_award_claims SET delivered_to = 'prize_chest'
                    WHERE award_id = @a AND claim_key = @k;
                    """, ("a", award.Id), ("k", claimKey));
            }
            else
            {
                await tx.ReleaseSavepointAsync(savepoint);
            }
        }

        if (toChest)
        {
            foreach (var item in award.Items)
            {
                await AddChestEntryAsync(tx, PrizeChestPolicy.KindItem, item.ItemId, item.Quantity, item.Quality,
                    item.Grade, item.Bound, source);
            }
        }

        // An award meant for the chest keeps its currencies there too ("prizes including B-GOLD"); a bag award
        // whose items fell back to the chest still pays its currencies now.
        if (award.ToPrizeChest)
        {
            if (award.Silver > 0) await AddChestEntryAsync(tx, PrizeChestPolicy.KindSilver, null, award.Silver, 1, 1, true, source);
            if (award.Gold > 0) await AddChestEntryAsync(tx, PrizeChestPolicy.KindGold, null, award.Gold, 1, 1, true, source);
            if (award.BindingGold > 0) await AddChestEntryAsync(tx, PrizeChestPolicy.KindBindingGold, null, award.BindingGold, 1, 1, true, source);
        }
        else
        {
            if (award.Silver > 0) await tx.AddSilverAsync(award.Silver);
            if (award.Gold > 0) await tx.AddGoldAsync(award.Gold);
            if (award.BindingGold > 0) await tx.AddBindingGoldAsync(award.BindingGold);
        }

        var titleGranted = false;
        if (award.TitleId is { } titleId)
        {
            titleGranted = await tx.ScalarAsync("""
                INSERT INTO public.character_title_grants (character_id, title_id, granted_by, note)
                SELECT @c, @t, @by, @note
                WHERE NOT EXISTS (SELECT 1 FROM public.character_title_entitlements
                                  WHERE character_id = @c AND title_id = @t)
                  AND (SELECT count(*) FROM public.character_title_entitlements WHERE character_id = @c) < @limit
                ON CONFLICT (character_id, title_id) DO NOTHING
                RETURNING 1;
                """, ("t", titleId), ("by", source), ("note", LoginAwardPolicy.Clip(award.Name, 512)),
                ("limit", (long)Godswar.Server.Application.Characters.CharacterSnapshotLimits.OwnedTitleCount)) is not null;
        }

        foreach (var buff in award.Buffs)
        {
            await QueueBuffAsync(tx, buff.StatusId, buff.DurationSeconds, source, source);
        }

        Console.WriteLine($"[login-award] claimed award={award.Id} key={claimKey} character={characterId} " +
            $"to={(toChest ? "prize_chest" : "bag")} bag_full={bagWasFull} title={titleGranted} buffs={award.Buffs.Count}");
        return new LoginAwardGrant(award, toChest, bagWasFull, bagItems, titleGranted, award.Buffs.Count);
    }

    public static async Task AddChestEntryAsync(EventsNpcTransaction tx, string kind, int? itemId, int quantity,
        short quality, short grade, bool bound, string source)
    {
        await tx.ScalarAsync("""
            INSERT INTO public.prize_chest_entries (character_id, kind, item_id, quantity, item_quality, item_grade,
                bound, source)
            VALUES (@c, @kind, @item, @n, @q, @g, @b, @src);
            """, ("kind", kind), ("item", itemId is { } id ? id : DBNull.Value), ("n", quantity), ("q", quality),
            ("g", grade), ("b", bound ? (short)1 : (short)0), ("src", source));
    }

    public static async Task QueueBuffAsync(EventsNpcTransaction tx, int statusId, long? durationSeconds,
        string source, string createdBy)
    {
        await tx.ScalarAsync("""
            INSERT INTO public.pending_character_buffs (character_id, status_id, duration_seconds, source, created_by)
            VALUES (@c, @s, @d, @src, @by);
            """, ("s", statusId), ("d", durationSeconds is { } seconds ? seconds : DBNull.Value), ("src", source),
            ("by", createdBy));
    }

    /// <summary>
    /// Takes this character's waiting buffs (oldest first) and marks them claimed; expired ones are closed as
    /// discarded. The caller applies each and records the result (or hands it back with <see cref="ReturnBuffAsync"/>).
    /// </summary>
    public static async Task<IReadOnlyList<(long Id, int StatusId, long? DurationSeconds)>> ClaimPendingBuffsAsync(
        EventsNpcTransaction tx)
    {
        await tx.ScalarAsync("""
            UPDATE public.pending_character_buffs SET applied_at = now(), result = 'discarded: not back in time'
            WHERE character_id = @c AND applied_at IS NULL AND discard_after IS NOT NULL AND discard_after <= now();
            """);
        var rows = await tx.RowsAsync("""
            UPDATE public.pending_character_buffs SET applied_at = now(), result = 'claimed'
            WHERE character_id = @c AND applied_at IS NULL
            RETURNING id, status_id, duration_seconds;
            """);
        return rows.Select(static row => (Convert.ToInt64(row[0]), Convert.ToInt32(row[1]),
                row[2] is null ? (long?)null : Convert.ToInt64(row[2])))
            .OrderBy(static row => row.Item1).ToArray();
    }

    public static async Task<IReadOnlyList<PrizeChestEntry>> ReadPrizeChestAsync(EventsNpcTransaction tx)
    {
        var rows = await tx.RowsAsync("""
            SELECT id, kind, coalesce(item_id, 0), quantity, item_quality, item_grade, bound
            FROM public.prize_chest_entries
            WHERE character_id = @c AND claimed_at IS NULL
            ORDER BY id;
            """);
        return rows.Select(static row => new PrizeChestEntry(Convert.ToInt64(row[0]), (string)row[1]!,
            Convert.ToInt32(row[2]), Convert.ToInt32(row[3]), Convert.ToInt16(row[4]), Convert.ToInt16(row[5]),
            Convert.ToInt32(row[6]) != 0)).ToArray();
    }

    /// <summary>
    /// Moves waiting prizes into the bag and wallet, oldest first. An item that does not fit stays in the chest
    /// (its partial write is undone) and the rest are still tried, so a big stack never blocks a small one.
    /// <paramref name="onlyEntryId"/> draws a single row (a cell dragged out of the award tab).
    /// </summary>
    public static async Task<PrizeChestDraw> DrawPrizeChestAsync(EventsNpcTransaction tx, long? onlyEntryId = null)
    {
        var pending = await tx.RowsAsync("""
            SELECT id, kind, coalesce(item_id, 0), quantity, item_quality, item_grade, bound
            FROM public.prize_chest_entries
            WHERE character_id = @c AND claimed_at IS NULL
            ORDER BY id
            FOR UPDATE;
            """);
        var items = new List<(int ItemId, int Quantity)>();
        int silver = 0, gold = 0, bindingGold = 0, waiting = 0;
        foreach (var row in pending)
        {
            var entry = new PrizeChestEntry(Convert.ToInt64(row[0]), (string)row[1]!, Convert.ToInt32(row[2]),
                Convert.ToInt32(row[3]), Convert.ToInt16(row[4]), Convert.ToInt16(row[5]), Convert.ToInt32(row[6]) != 0);
            if (onlyEntryId is { } only && entry.Id != only)
            {
                waiting++;
                continue;
            }

            var paid = true;
            switch (entry.Kind)
            {
                case PrizeChestPolicy.KindItem:
                {
                    var savepoint = $"prize_chest_{entry.Id}";
                    await tx.SavepointAsync(savepoint);
                    if (await tx.GrantVariantAsync(entry.ItemId, entry.Quantity, entry.Bound, entry.Quality, entry.Grade))
                    {
                        await tx.ReleaseSavepointAsync(savepoint);
                        items.Add((entry.ItemId, entry.Quantity));
                    }
                    else
                    {
                        await tx.RollbackToSavepointAsync(savepoint);
                        paid = false;
                    }

                    break;
                }
                case PrizeChestPolicy.KindSilver:
                    await tx.AddSilverAsync(entry.Quantity);
                    silver += entry.Quantity;
                    break;
                case PrizeChestPolicy.KindGold:
                    await tx.AddGoldAsync(entry.Quantity);
                    gold += entry.Quantity;
                    break;
                case PrizeChestPolicy.KindBindingGold:
                    await tx.AddBindingGoldAsync(entry.Quantity);
                    bindingGold += entry.Quantity;
                    break;
                default:
                    paid = false;
                    break;
            }

            if (!paid)
            {
                waiting++;
                continue;
            }

            await tx.ScalarAsync("UPDATE public.prize_chest_entries SET claimed_at = now() WHERE id = @id AND character_id = @c;",
                ("id", entry.Id));
        }

        return new PrizeChestDraw(items, silver, gold, bindingGold, waiting);
    }

    private static DateTimeOffset ToInstant(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
        _ => throw new InvalidCastException("timestamptz expected")
    };
}
