using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// F-138 newbie growth rewards (see <see cref="RookieGrowthCatalog"/> for the wire). The offered entries are sent as
/// one SYN after world entry; Complete (or the exe's own "done" report for the UI tips) is checked here and paid once
/// per character and entry inside the character-locked events transaction (rookie_growth_claims is the guard).
/// Hot switch prize.rookie_growth.
/// </summary>
internal sealed partial class GameClientHandler
{
    private enum RookieGrowthOutcome { Paid, Closed, BagFull, Unavailable }

    private const short RookieKitBagLocation = 1;
    private const int RookieKitBagSlots = 96;
    private readonly RookieGrowthMask _rookieMask = new();

    private async Task<bool> TryHandleRookieGrowthPacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (packet.Opcode != RookieGrowthCatalog.Opcode) return false;
        if (_character is not { } character || !Finalise2Rates.On(Finalise2RateKeys.RookieGrowth)) return true;
        if (RookieGrowthCatalog.ParseRequest(packet.Buffer) is not { } request)
        {
            Console.WriteLine($"[rookie] short packet character={character.Name} len={packet.Buffer.Length}");
            return true;
        }

        var (sub, id) = request;
        if (!RookieGrowthCatalog.Rules.ContainsKey(id) ||
            !RookieGrowthCatalog.Rewards.TryGetValue(id, out var reward) ||
            !RookieGrowthCatalog.IsPayable(reward))
        {
            Console.WriteLine($"[rookie] refused character={character.Name} sub={sub} id={id}: not offered");
            return true;
        }

        if (!_rookieMask.Contains(id))
        {
            // Not in the client's mask (no SYN yet, or already closed): a COM/DEL now would corrupt it.
            Console.WriteLine($"[rookie] ignored character={character.Name} sub={sub} id={id}: not open");
            return true;
        }

        if (sub == RookieGrowthCatalog.SubDelete)
        {
            await RunEventsNpcAsync(async tx => await tx.ScalarAsync(
                "INSERT INTO public.rookie_growth_claims (character_id, entry_id, state) VALUES (@c, @e, @s) " +
                "ON CONFLICT DO NOTHING RETURNING entry_id;",
                ("e", (short)id), ("s", RookieGrowthCatalog.StateDeleted)), (object?)null, cancellationToken);
            if (_rookieMask.TryClose(id))
            {
                await _session.SendAsync(RookieGrowthCatalog.Entry(LocalPlayerObjectId, RookieGrowthCatalog.SubDelete, id),
                    cancellationToken, "RookieGrowthDelete");
            }

            Console.WriteLine($"[rookie] deleted character={character.Name} id={id}");
            return true;
        }

        if (!RookieGrowthCatalog.IsMet(id, sub, character))
        {
            Console.WriteLine($"[rookie] not met character={character.Name} sub={sub} id={id}");
            if (sub == RookieGrowthCatalog.SubRequestComplete)
            {
                // GENERATED text (a chat line, no pop-up).
                await SendEventsNpcNoticeAsync("Growth reward: this step is not done yet.", "RookieGrowthNotYet",
                    cancellationToken);
            }

            return true;
        }

        var zodiacCap = ZodiacEnergyCatalog.GetStorageLimit(Math.Clamp((int)character.ZodiacLevel, 1, 30));
        var outcome = await RunEventsNpcAsync(
            tx => PayRookieGrowthAsync(tx, reward, zodiacCap), RookieGrowthOutcome.Unavailable, cancellationToken);
        Console.WriteLine($"[rookie] claim character={character.Name} sub={sub} id={id} outcome={outcome}");
        switch (outcome)
        {
            case RookieGrowthOutcome.Paid:
                // The listed EXP exactly: no event rate (the client shows its own XML amount).
                await PayEventsNpcExperienceAsync(reward.Experience, "RookieGrowth", cancellationToken, eventRate: false);
                var experience = (uint)Math.Clamp(_character?.Experience ?? 0, 0, uint.MaxValue);
                if (_rookieMask.TryClose(id))
                {
                    await _session.SendAsync(
                        RookieGrowthCatalog.Entry(LocalPlayerObjectId, RookieGrowthCatalog.SubComplete, id, experience),
                        cancellationToken, "RookieGrowthComplete");
                }

                // Last, so the server's bag pages and wallet overwrite the client's local copy of the reward.
                await RefreshAfterEventsNpcAsync([], cancellationToken);
                break;
            case RookieGrowthOutcome.Closed:
                // Closed in the database while still open on this client (e.g. claimed from another login): take it
                // off the window without paying.
                if (_rookieMask.TryClose(id))
                {
                    await _session.SendAsync(RookieGrowthCatalog.Entry(LocalPlayerObjectId, RookieGrowthCatalog.SubDelete, id),
                        cancellationToken, "RookieGrowthClosed");
                }

                break;
            case RookieGrowthOutcome.BagFull:
                // GENERATED text (a chat line, no pop-up). The entry stays open and can be claimed again.
                await SendEventsNpcNoticeAsync("Growth reward: your bag is full.", "RookieGrowthBagFull", cancellationToken);
                break;
        }

        return true;
    }

    /// <summary>Records the claim and pays every non-EXP field, or nothing at all.</summary>
    private static async Task<RookieGrowthOutcome> PayRookieGrowthAsync(EventsNpcTransaction tx, RookieGrowthReward reward,
        int zodiacCap)
    {
        var recorded = await tx.ScalarAsync(
            "INSERT INTO public.rookie_growth_claims (character_id, entry_id, state) VALUES (@c, @e, @s) " +
            "ON CONFLICT DO NOTHING RETURNING entry_id;",
            ("e", (short)reward.Id), ("s", RookieGrowthCatalog.StateClaimed));
        if (recorded is null) return RookieGrowthOutcome.Closed;

        foreach (var item in reward.Items)
        {
            var granted = item.IsPlain
                ? await tx.GrantAsync(item.ItemId, item.Count, item.Bound)
                : await GrantRookieEquipmentAsync(tx, item);
            if (!granted)
            {
                tx.Rollback();
                return RookieGrowthOutcome.BagFull;
            }
        }

        if (reward.Money > 0) await tx.AddSilverAsync(reward.Money);
        if (reward.SkillPoint > 0) await tx.AddTalentPointsAsync(reward.SkillPoint);
        if (reward.BindingGold > 0) await tx.AddBindingGoldAsync(reward.BindingGold);
        if (reward.Reputation > 0)
        {
            // Reputation = the honour balance (as the Exchange Mentor reads it).
            await tx.ScalarAsync("""
                UPDATE public.character_base
                SET medusa_honor_points = LEAST(COALESCE(medusa_honor_points, 0)::bigint + @n, 2147483647)::integer,
                    medusa_reward_revision = medusa_reward_revision + 1
                WHERE id = @c;
                """, ("n", (long)reward.Reputation));
        }

        if (reward.ConsEnergy > 0)
        {
            // Zodiac energy, held under the storage ceiling of the character's zodiac level (GENERATED clamp).
            await tx.ScalarAsync(
                "UPDATE public.character_base SET zodiac_energy = LEAST(zodiac_energy::bigint + @n, @cap)::integer " +
                "WHERE id = @c;", ("n", (long)reward.ConsEnergy), ("cap", (long)zodiacCap));
        }

        return RookieGrowthOutcome.Paid;
    }

    /// <summary>
    /// One equipment piece with its appended attributes and levels (the Path to Godhood gear), in the lowest free
    /// kit-bag slot - the slot the stock client fills with its own copy. Overlap is 1 for all of them.
    /// </summary>
    private static async Task<bool> GrantRookieEquipmentAsync(EventsNpcTransaction tx, RookieGrowthItem item)
    {
        var slot = await tx.ScalarAsync(
            "SELECT s FROM generate_series(0, @n - 1) AS s WHERE NOT EXISTS (SELECT 1 FROM public.character_items " +
            "WHERE user_id = @c AND item_location = @l AND slot_index = s) ORDER BY s LIMIT 1;",
            ("n", RookieKitBagSlots), ("l", RookieKitBagLocation));
        if (slot is not int free) return false;

        object Append(int index) => index < item.Appends.Length ? (short)item.Appends[index] : DBNull.Value;
        await tx.ScalarAsync("""
            INSERT INTO public.character_items (user_id, item_location, slot_index, prop_id, attribute1, attribute2,
                attribute3, attribute4, attribute5, item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
            VALUES (@c, @l, @s, @i, @a1, @a2, @a3, @a4, @a5, @q, @g, @b, @n, 0, 0);
            """,
            ("l", RookieKitBagLocation), ("s", (short)free), ("i", item.ItemId),
            ("a1", Append(0)), ("a2", Append(1)), ("a3", Append(2)), ("a4", Append(3)), ("a5", Append(4)),
            ("q", item.Quality), ("g", item.Grade), ("b", item.Bound ? (short)1 : (short)0),
            ("n", (short)Math.Min((int)item.Count, 99)));
        return true;
    }

    /// <summary>After world entry: the offered entries this character has not claimed or deleted.</summary>
    private async Task SendRookieGrowthSyncAsync(CancellationToken cancellationToken)
    {
        if (_character is not { } character || !Finalise2Rates.On(Finalise2RateKeys.RookieGrowth) ||
            EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        try
        {
            var closedText = await store.ExecuteScalarAsync(
                "SELECT COALESCE(string_agg(entry_id::text, ','), '') FROM public.rookie_growth_claims WHERE character_id = @c;",
                cancellationToken, ("c", character.Id)) as string ?? string.Empty;
            var closed = closedText.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            var offered = RookieGrowthCatalog.Offered(closed);
            _rookieMask.Reset(offered);
            await _session.SendAsync(RookieGrowthCatalog.Sync(LocalPlayerObjectId, offered), cancellationToken,
                "RookieGrowthSync");
            Console.WriteLine($"[rookie] sync character={character.Name} offered={offered.Count} closed={closed.Count}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[rookie] sync failed character={character.Name}: {error.Message}");
        }
    }
}
