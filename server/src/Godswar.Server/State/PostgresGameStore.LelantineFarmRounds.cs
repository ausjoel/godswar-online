using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore : ILelantineFarmRoundStore
{
    private const long FarmRoundLockKey = 0x4C46524E;   // "LFRN"
    internal const int HeroOfLelantineTitleId = 5112;
    internal const int SkullCrusherTitleId = 5111;

    public async Task<FarmRoundTick> AdvanceFarmRoundsAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", FarmRoundLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        int settled = 0, rewarded = 0, started = 0;
        var due = new List<DateTimeOffset>();
        await using (var read = new NpgsqlCommand("""
            SELECT round_start FROM public.lelantine_farm_rounds
            WHERE settled_at IS NULL AND round_end <= @now ORDER BY round_start FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("now", now);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(reader.GetFieldValue<DateTimeOffset>(0));
            }
        }
        foreach (var roundStart in due)
        {
            rewarded += await SettleFarmRoundAsync(connection, transaction, roundStart, now, cancellationToken);
            settled++;
        }

        var (windowStart, windowEnd) = LelantineFarmRoundPolicy.LatestWindow(now);
        if (now >= windowStart && now < windowEnd)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.lelantine_farm_rounds (round_start, round_end)
                VALUES (@start, @end) ON CONFLICT (round_start) DO NOTHING;
                """, connection, transaction);
            insert.Parameters.AddWithValue("start", windowStart);
            insert.Parameters.AddWithValue("end", windowEnd);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                // A new round begins from zero: anything scored outside a window (testing) is archived.
                await ArchiveAndClearFarmScoresAsync(connection, transaction, windowStart, cancellationToken);
                started++;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new FarmRoundTick(started, settled, rewarded);
    }

    private static async Task ArchiveAndClearFarmScoresAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset round, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.lelantine_farm_donations_archive SELECT d.*, @round FROM public.lelantine_farm_donations d;
            DELETE FROM public.lelantine_farm_donations;
            INSERT INTO public.lelantine_farm_kill_points_archive SELECT k.*, @round FROM public.lelantine_farm_kill_points k;
            DELETE FROM public.lelantine_farm_kill_points;
            """, connection, transaction);
        command.Parameters.AddWithValue("round", round);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> SettleFarmRoundAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset roundStart, DateTimeOffset now,
        CancellationToken token)
    {
        var scorers = new List<(int Character, byte Faction, long Points, int Level)>();
        await using (var read = new NpgsqlCommand("""
            SELECT p.character_id, p.faction, p.points::bigint, c.fighter_job_lv
            FROM public.lelantine_farm_personal_points p
            JOIN public.character_base c ON c.id = p.character_id
            WHERE p.points > 0
            ORDER BY p.points DESC, p.character_id;
            """, connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                scorers.Add((reader.GetInt32(0), (byte)reader.GetInt16(1), reader.GetInt64(2), reader.GetInt32(3)));
            }
        }

        long sparta = scorers.Where(s => s.Faction == 0).Sum(s => s.Points);
        long athens = scorers.Where(s => s.Faction == 1).Sum(s => s.Points);
        var winner = LelantineFarmRoundPolicy.Winner(sparta, athens);
        var rank = 0;
        foreach (var scorer in scorers)
        {
            rank++;
            var outcome = LelantineFarmRoundPolicy.OutcomeFor(scorer.Faction, sparta, athens);
            var reward = LelantineFarmRoundPolicy.RewardFor(scorer.Level, outcome);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.lelantine_farm_round_rewards
                    (round_start, character_id, faction, score, outcome, rank_overall,
                     pet_experience, character_experience, talent_points)
                VALUES (@round, @character, @faction, @score, @outcome, @rank, @pet, @exp, @talent)
                ON CONFLICT (round_start, character_id) DO NOTHING;
                """, connection, transaction);
            insert.Parameters.AddWithValue("round", roundStart);
            insert.Parameters.AddWithValue("character", scorer.Character);
            insert.Parameters.AddWithValue("faction", (short)scorer.Faction);
            insert.Parameters.AddWithValue("score", scorer.Points);
            insert.Parameters.AddWithValue("outcome", (short)outcome);
            insert.Parameters.AddWithValue("rank", rank);
            insert.Parameters.AddWithValue("pet", reward.PetExperience);
            insert.Parameters.AddWithValue("exp", reward.CharacterExperience);
            insert.Parameters.AddWithValue("talent", reward.TalentPoints);
            await insert.ExecuteNonQueryAsync(token);
        }

        if (scorers.Count > 0)
        {
            // First place on the overall ranking takes the title (the client's own text: "first place on the rankings").
            await GrantFarmTitleCoreAsync(connection, transaction, scorers[0].Character, HeroOfLelantineTitleId,
                $"Lelantine Farm round {roundStart:u}", token);
        }

        await using (var finish = new NpgsqlCommand("""
            UPDATE public.lelantine_farm_rounds
            SET sparta_points = @sparta, athens_points = @athens, winner = @winner, settled_at = @now
            WHERE round_start = @round;
            """, connection, transaction))
        {
            finish.Parameters.AddWithValue("sparta", sparta);
            finish.Parameters.AddWithValue("athens", athens);
            finish.Parameters.AddWithValue("winner", winner is { } w ? (object)(short)w : DBNull.Value);
            finish.Parameters.AddWithValue("now", now);
            finish.Parameters.AddWithValue("round", roundStart);
            await finish.ExecuteNonQueryAsync(token);
        }

        await ArchiveAndClearFarmScoresAsync(connection, transaction, roundStart, token);
        return scorers.Count;
    }

    private static async Task GrantFarmTitleCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int characterId, int titleId, string note,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.character_title_grants (character_id, title_id, granted_by, note)
            VALUES (@character, @title, 'lelantine-farm', @note)
            ON CONFLICT (character_id, title_id) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("title", titleId);
        command.Parameters.AddWithValue("note", note.Length > 500 ? note[..500] : note);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task GrantFarmTitleAsync(
        int characterId, int titleId, string note, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await GrantFarmTitleCoreAsync(connection, null, characterId, titleId, note, cancellationToken);
    }

    public async Task<IReadOnlyList<FarmRoundRewardClaim>> ClaimFarmRoundRewardsAsync(
        int characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE public.lelantine_farm_round_rewards
            SET claimed_at = transaction_timestamp()
            WHERE character_id = @character AND claimed_at IS NULL
            RETURNING round_start, outcome, pet_experience, character_experience, talent_points;
            """, connection);
        command.Parameters.AddWithValue("character", characterId);
        var claims = new List<FarmRoundRewardClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(new FarmRoundRewardClaim(
                reader.GetFieldValue<DateTimeOffset>(0), (FarmRoundOutcome)reader.GetInt16(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        }
        return claims;
    }

    public async Task UnclaimFarmRoundRewardAsync(
        int characterId, DateTimeOffset roundStart, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE public.lelantine_farm_round_rewards SET claimed_at = NULL
            WHERE character_id = @character AND round_start = @round;
            """, connection);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("round", roundStart);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FarmToolGrant> GrantFarmToolsAsync(
        int characterId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockRow = new NpgsqlCommand(
            "SELECT 1 FROM public.character_base WHERE id = @character FOR UPDATE;", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("character", characterId);
            if (await lockRow.ExecuteScalarAsync(cancellationToken) is null)
            {
                return new FarmToolGrant(0, null);
            }
        }

        var roundStart = LelantineFarmRoundPolicy.LatestWindow(now).Start;
        DateTimeOffset? grantedRound = null, lastGrant = null;
        await using (var read = new NpgsqlCommand("""
            SELECT round_start, last_grant_at FROM public.lelantine_farm_tool_grants
            WHERE character_id = @character FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("character", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                grantedRound = reader.GetFieldValue<DateTimeOffset>(0);
                lastGrant = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        var stacks = new List<(long Id, short Slot, short Stack)>();
        var occupied = new HashSet<short>();
        await using (var items = new NpgsqlCommand("""
            SELECT id, slot_index, stack, prop_id FROM public.character_items
            WHERE user_id = @character AND item_location = @bag FOR UPDATE;
            """, connection, transaction))
        {
            items.Parameters.AddWithValue("character", characterId);
            items.Parameters.AddWithValue("bag", ItemLocationKitBag);
            await using var reader = await items.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                occupied.Add(reader.GetInt16(1));
                if (reader.GetInt32(3) == LelantineFarmToolPolicy.WoodenToolItemId)
                {
                    stacks.Add((reader.GetInt64(0), reader.GetInt16(1), reader.GetInt16(2)));
                }
            }
        }

        var held = stacks.Sum(static s => (int)s.Stack);
        int amount;
        if (grantedRound != roundStart)
        {
            amount = LelantineFarmToolPolicy.FirstHandout;
        }
        else if (held == 0 && lastGrant is { } last && now - last >= LelantineFarmToolPolicy.TopUpInterval)
        {
            amount = LelantineFarmToolPolicy.TopUp;
        }
        else
        {
            return new FarmToolGrant(0, null);
        }

        var target = stacks.FirstOrDefault(s => s.Stack + amount <= LelantineFarmToolPolicy.MaxStack);
        if (target.Id != 0)
        {
            await using var add = new NpgsqlCommand(
                "UPDATE public.character_items SET stack = stack + @amount, updated_at = transaction_timestamp() WHERE id = @id;",
                connection, transaction);
            add.Parameters.AddWithValue("amount", (short)amount);
            add.Parameters.AddWithValue("id", target.Id);
            await add.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            short slot = -1;
            for (short candidate = 0; candidate < KitBagItemGrantPlanner.SlotCount; candidate++)
            {
                if (!occupied.Contains(candidate)) { slot = candidate; break; }
            }
            if (slot < 0)
            {
                return new FarmToolGrant(0, null);   // bag full: try again on the next tick
            }
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.character_items (user_id, item_location, slot_index, prop_id, item_quality, item_grade,
                                                    bound, stack, item_exp, holy_suit_code)
                VALUES (@character, @bag, @slot, @item, 1, 1, 0, @amount, 0, 0);
                """, connection, transaction);
            insert.Parameters.AddWithValue("character", characterId);
            insert.Parameters.AddWithValue("bag", ItemLocationKitBag);
            insert.Parameters.AddWithValue("slot", slot);
            insert.Parameters.AddWithValue("item", LelantineFarmToolPolicy.WoodenToolItemId);
            insert.Parameters.AddWithValue("amount", (short)amount);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO public.lelantine_farm_tool_grants (character_id, round_start, grants, last_grant_at)
            VALUES (@character, @round, 1, @now)
            ON CONFLICT (character_id) DO UPDATE SET
                grants = CASE WHEN lelantine_farm_tool_grants.round_start = EXCLUDED.round_start
                              THEN lelantine_farm_tool_grants.grants + 1 ELSE 1 END,
                round_start = EXCLUDED.round_start, last_grant_at = EXCLUDED.last_grant_at;
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("character", characterId);
            upsert.Parameters.AddWithValue("round", roundStart);
            upsert.Parameters.AddWithValue("now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FarmToolGrant(amount, await GetCharacterByIdAsync(characterId, cancellationToken));
    }
}
