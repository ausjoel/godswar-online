using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal enum CharacterIdentityChangeStatus
{
    Done,
    CharacterNotFound,
    NotEnoughGold,
    FashionWorn,
    InGuild,
    CoolingDown,
    Stale
}

/// <summary>The row as the change left it, for the live character and the log.</summary>
internal readonly record struct CharacterIdentityChangeResult(
    CharacterIdentityChangeStatus Status,
    int RemainingSeconds = 0,
    byte Gender = 0,
    byte Camp = 0,
    byte Hair = 0,
    byte Map = 0,
    float PositionX = 0,
    float PositionZ = 0,
    int Stone = 0,
    long PositionRevision = 0);

/// <summary>
/// Plastic surgeon and faction changer. One ownership-fenced transaction each: it
/// re-checks every condition on the locked row, charges Stone (the premium
/// "Gold"), writes the new identity and, for a faction change, moves the
/// character to the new capital and records the change for the cooldown.
/// </summary>
internal interface ICharacterIdentityStore
{
    Task<CharacterIdentityChangeResult> ChangeGenderAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        byte expectedGender,
        int price,
        CancellationToken cancellationToken);

    Task<CharacterIdentityChangeResult> ChangeFactionAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        byte expectedCamp,
        int price,
        TimeSpan cooldown,
        CancellationToken cancellationToken);
}

internal static class CharacterIdentityControls
{
    public static ICharacterIdentityStore? Store { get; set; }
}

internal sealed class PostgresCharacterIdentityStore(NpgsqlDataSource dataSource)
    : ICharacterIdentityStore
{
    /// <summary>operator_log.command value that marks a faction change (cooldown source).</summary>
    internal const string FactionLogCommand = "faction-change";
    internal const string GenderLogCommand = "gender-change";

    private const short StylishSlot = 12;
    private const short WornLocation = 0;

    // Capitals: Sparta is map 0 and camp 0, Athens map 1 and camp 1, both start at 165,-97.
    private const float StartX = 165.0f;
    private const float StartZ = -97.0f;

    // Hair ids that have a model for one gender only (Characters/<gender>_hair_NNNN.gwo).
    private static readonly HashSet<int> MaleOnlyHair =
        [230, 231, 232, 233, 234, 250, 251, 252, 253, 254];
    private static readonly HashSet<int> FemaleOnlyHair =
        [180, 181, 182, 183, 184, 210, 211, 212, 213, 214,
         240, 241, 242, 243, 244, 260, 261, 262, 263, 264];

    // Equipment whose avatar model exists for one gender only (Characters/<gender>_<part>_<item id>.gwo, measured
    // 2026-11-02 against ItemBaseAttribute.xml): the female-only sets cannot be drawn on a male character and the
    // male-only one-hand swords cannot be drawn on a female one. Worn at the time of the change, they would be
    // an entity the client cannot create, so the change is refused with the same line as for fashion.
    private static readonly int[] FemaleOnlyModelItems = [2190, 2590, 2690, 2790, 2990, 3301, 3302, 3303];
    private static readonly int[] MaleOnlyModelItems = [1100, 1102, 1103, 1104, 1105, 1106, 1107];

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard = new(dataSource);

    /// <summary>The hair to keep when the character becomes <paramref name="toMale"/>.</summary>
    internal static byte HairFor(byte hair, bool toMale)
    {
        var without = toMale ? FemaleOnlyHair : MaleOnlyHair;
        return without.Contains(hair) ? (byte)(hair % 10) : hair;
    }

    public async Task<CharacterIdentityChangeResult> ChangeGenderAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        byte expectedGender,
        int price,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await LockAsync(connection, transaction, subject, ownership, cancellationToken);
        if (locked is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.CharacterNotFound);
        }

        var row = locked.Value;
        var currentGender = (byte)(row.Gender == "female" ? 0 : 1);
        if (currentGender != expectedGender)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.Stale);
        }

        if (row.Stone < price)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.NotEnoughGold, Stone: row.Stone);
        }

        var toMaleTarget = currentGender == 0;
        if (await ScalarAsync<long>(
                connection, transaction,
                "SELECT count(*) FROM public.character_items " +
                "WHERE user_id = @id AND item_location = @worn AND (slot_index = @slot " +
                $"OR prop_id = ANY(ARRAY[{string.Join(',', toMaleTarget ? FemaleOnlyModelItems : MaleOnlyModelItems)}]));",
                subject.CharacterId, cancellationToken) > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.FashionWorn, Stone: row.Stone);
        }

        var toMale = currentGender == 0;
        var newHair = HairFor(row.Hair, toMale);
        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET gender = @gender,
                hair_style = @hair,
                "Stone" = "Stone" - @price,
                wallet_revision = wallet_revision + 1
            WHERE id = @id AND "Stone" >= @price;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("gender", toMale ? "male" : "female");
            update.Parameters.AddWithValue("hair", (short)newHair);
            update.Parameters.AddWithValue("price", price);
            update.Parameters.AddWithValue("id", subject.CharacterId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterIdentityChangeStatus.NotEnoughGold, Stone: row.Stone);
            }
        }

        await LogAsync(
            connection, transaction, subject, row.Name, GenderLogCommand,
            $"{(toMale ? "female->male" : "male->female")} hair {row.Hair}->{newHair} price={price}",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            CharacterIdentityChangeStatus.Done,
            Gender: (byte)(toMale ? 1 : 0),
            Camp: row.Camp,
            Hair: newHair,
            Map: row.Map,
            PositionX: row.PositionX,
            PositionZ: row.PositionZ,
            Stone: row.Stone - price,
            PositionRevision: row.PositionRevision);
    }

    public async Task<CharacterIdentityChangeResult> ChangeFactionAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        byte expectedCamp,
        int price,
        TimeSpan cooldown,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await LockAsync(connection, transaction, subject, ownership, cancellationToken);
        if (locked is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.CharacterNotFound);
        }

        var row = locked.Value;
        if (row.Camp != expectedCamp)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.Stale);
        }

        if (row.Consortia != 0 || await ScalarAsync<long>(
                connection, transaction,
                "SELECT count(*) FROM public.guild_members WHERE character_id = @id;",
                subject.CharacterId, cancellationToken) > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.InGuild, Stone: row.Stone);
        }

        // The last change, read from the audit rows this store writes.
        await using (var last = new NpgsqlCommand(
            "SELECT extract(epoch FROM now() - max(at)) FROM public.operator_log " +
            "WHERE command = @command AND target = @target;",
            connection,
            transaction))
        {
            last.Parameters.AddWithValue("command", FactionLogCommand);
            last.Parameters.AddWithValue("target", subject.CharacterId.ToString());
            if (await last.ExecuteScalarAsync(cancellationToken) is decimal sinceSeconds)
            {
                var remaining = (int)Math.Ceiling(cooldown.TotalSeconds - (double)sinceSeconds);
                if (remaining > 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(CharacterIdentityChangeStatus.CoolingDown, remaining, Stone: row.Stone);
                }
            }
        }

        if (row.Stone < price)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterIdentityChangeStatus.NotEnoughGold, Stone: row.Stone);
        }

        var toSparta = row.Camp != 0;
        var newCamp = (byte)(toSparta ? 0 : 1);
        var newMap = (byte)(toSparta ? 0 : 1);
        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET camp = @camp,
                "Map" = @map,
                "Pos_X" = @x,
                "Pos_Z" = @z,
                position_revision = position_revision + 1,
                "Stone" = "Stone" - @price,
                wallet_revision = wallet_revision + 1
            WHERE id = @id AND "Stone" >= @price
            RETURNING position_revision;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("camp", (short)newCamp);
            update.Parameters.AddWithValue("map", (short)newMap);
            update.Parameters.AddWithValue("x", StartX);
            update.Parameters.AddWithValue("z", StartZ);
            update.Parameters.AddWithValue("price", price);
            update.Parameters.AddWithValue("id", subject.CharacterId);
            var revision = await update.ExecuteScalarAsync(cancellationToken);
            if (revision is not long positionRevision)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterIdentityChangeStatus.NotEnoughGold, Stone: row.Stone);
            }

            var portalSkills = await SwapFactionPortalSkillsAsync(
                connection, transaction, subject.CharacterId, row.Camp, newCamp, cancellationToken);

            await LogAsync(
                connection, transaction, subject, row.Name, FactionLogCommand,
                $"camp {row.Camp}->{newCamp} map {row.Map}->{newMap} price={price} portal-skills={portalSkills}",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                CharacterIdentityChangeStatus.Done,
                Gender: (byte)(row.Gender == "female" ? 0 : 1),
                Camp: newCamp,
                Hair: row.Hair,
                Map: newMap,
                PositionX: StartX,
                PositionZ: StartZ,
                Stone: row.Stone - price,
                PositionRevision: positionRevision);
        }
    }

    /// <summary>
    /// The "return to capital" and "return to suburb" skills belong to a camp
    /// (BackhaulSkillCatalog refuses a cast from the other camp), and each character
    /// holds its own camp's copies in character_skills. A faction change therefore
    /// swaps them for the new camp's, or the character could no longer fly home.
    /// Skills are not written back from memory at logout, so the swap survives the
    /// disconnect that follows. Returns the number of rows swapped.
    /// </summary>
    private static async Task<int> SwapFactionPortalSkillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        byte oldCamp,
        byte newCamp,
        CancellationToken cancellationToken)
    {
        await using var swap = new NpgsqlCommand(
            """
            UPDATE public.character_skills s
            SET skill_id = CASE s.skill_id WHEN @oldCity THEN @newCity ELSE @newSuburb END,
                source = 'faction-change'
            WHERE s.user_id = @id
              AND s.skill_id IN (@oldCity, @oldSuburb)
              AND NOT EXISTS (
                  SELECT 1 FROM public.character_skills t
                  WHERE t.user_id = @id
                    AND t.skill_id = CASE s.skill_id WHEN @oldCity THEN @newCity ELSE @newSuburb END);
            DELETE FROM public.character_skills
            WHERE user_id = @id AND skill_id IN (@oldCity, @oldSuburb);
            """,
            connection,
            transaction);
        swap.Parameters.AddWithValue("id", characterId);
        swap.Parameters.AddWithValue("oldCity", checked((int)FactionPortalSkillPolicy.ResolveCapitalPortalSkillId(oldCamp)));
        swap.Parameters.AddWithValue("oldSuburb", checked((int)FactionPortalSkillPolicy.ResolveSuburbPortalSkillId(oldCamp)));
        swap.Parameters.AddWithValue("newCity", checked((int)FactionPortalSkillPolicy.ResolveCapitalPortalSkillId(newCamp)));
        swap.Parameters.AddWithValue("newSuburb", checked((int)FactionPortalSkillPolicy.ResolveSuburbPortalSkillId(newCamp)));
        return await swap.ExecuteNonQueryAsync(cancellationToken);
    }

    private readonly record struct LockedRow(
        string Name,
        string Gender,
        byte Camp,
        byte Hair,
        int Consortia,
        int Stone,
        byte Map,
        float PositionX,
        float PositionZ,
        long PositionRevision);

    private async Task<LockedRow?> LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var fence = await _ownershipGuard.LockCurrentAsync(
            connection, transaction, subject, ownership, cancellationToken);
        if (fence.Status == PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            return null;
        }

        fence.RequireCurrent();
        await using var read = new NpgsqlCommand(
            """
            SELECT name, gender, camp, hair_style, consortia, "Stone", "Map",
                   "Pos_X", "Pos_Z", position_revision
            FROM public.character_base
            WHERE id = @id AND account_id = @account AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        read.Parameters.AddWithValue("id", subject.CharacterId);
        read.Parameters.AddWithValue("account", subject.AccountId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LockedRow(
            reader.GetString(0),
            reader.GetString(1),
            (byte)reader.GetInt16(2),
            (byte)reader.GetInt16(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            (byte)reader.GetInt16(6),
            reader.GetFloat(7),
            reader.GetFloat(8),
            reader.GetInt64(9));
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", characterId);
        if (sql.Contains("@worn", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("worn", WornLocation);
            command.Parameters.AddWithValue("slot", StylishSlot);
        }

        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task LogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        string characterName,
        string command,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO public.operator_log (actor_account_id, actor_character, command, target, detail)
            VALUES (@account, @name, @command, @target, @detail);
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("account", subject.AccountId);
        insert.Parameters.AddWithValue("name", characterName);
        insert.Parameters.AddWithValue("command", command);
        insert.Parameters.AddWithValue("target", subject.CharacterId.ToString());
        insert.Parameters.AddWithValue("detail", detail);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Start-up wiring, next to <see cref="OperatorRuntime"/>; never stops the server.</summary>
internal static class CharacterIdentityRuntime
{
    public static void Initialize(string connectionString)
    {
        try
        {
            CharacterIdentityControls.Store = new PostgresCharacterIdentityStore(
                NpgsqlDataSource.Create(connectionString));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                "[identity] start-up failed; gender and faction changes unavailable: " +
                error.Message);
        }

        TitleFortuneControls.Initialize(connectionString); // TITLES
    }
}
