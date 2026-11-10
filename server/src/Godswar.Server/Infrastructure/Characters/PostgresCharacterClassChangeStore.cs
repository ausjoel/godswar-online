using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed class PostgresCharacterClassChangeStore(NpgsqlDataSource dataSource)
    : ICharacterClassChangeStore
{
    internal const string LogCommand = "class-change";
    internal const string PermitCommandFamily = "class_change_permit";

    /// <summary>
    /// REVIEW FIX (rollback): the two tables are created here on first use with idempotent
    /// DDL, NOT as a registered schema migration, so the previous dll still starts against a
    /// database this build has used (the migration runner refuses a history it does not
    /// know). Development/build/classchange/migration.sql is the same SQL, to run before the
    /// release with zero players (the REFERENCES clauses take a short lock on character_base).
    /// (Table comments and the ownership step are in migration.sql only: COMMENT needs ownership.)
    /// The receipts are also the talent command epoch (<see cref="ClassChangeTalentEpoch"/>):
    /// never delete them while the character exists.
    /// </summary>
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.character_class_change_receipts (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            account_id integer NOT NULL,
            changed_at timestamptz NOT NULL DEFAULT now(),
            from_profession smallint NOT NULL CHECK (from_profession BETWEEN 0 AND 3),
            to_profession smallint NOT NULL CHECK (to_profession BETWEEN 0 AND 3),
            character_level integer NOT NULL,
            permit_item_id integer,
            permit_slot smallint,
            permit_ledger_inbox_id bigint,
            talent_points_before integer NOT NULL,
            talent_points_refunded integer NOT NULL CHECK (talent_points_refunded >= 0),
            talents jsonb NOT NULL DEFAULT '[]'::jsonb,
            sealed_skills jsonb NOT NULL DEFAULT '[]'::jsonb,
            unsealed_skills jsonb NOT NULL DEFAULT '[]'::jsonb,
            granted_skills jsonb NOT NULL DEFAULT '[]'::jsonb,
            cleared_zodiac jsonb NOT NULL DEFAULT '[]'::jsonb,
            CHECK (from_profession <> to_profession)
        );
        CREATE INDEX IF NOT EXISTS ix_character_class_change_receipts_character
            ON public.character_class_change_receipts (character_id, changed_at);
        CREATE TABLE IF NOT EXISTS public.character_sealed_skills (
            user_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            skill_id integer NOT NULL REFERENCES public.skill_templates(skill_id),
            skill_level smallint NOT NULL,
            acquired_at timestamptz NOT NULL,
            source varchar(64) NOT NULL,
            sealed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, skill_id)
        );
        -- FINALISE2 (F-54): Zeus' Baptism talent resets. Each row also advances the talent command epoch.
        CREATE TABLE IF NOT EXISTS public.finalise2_talent_reset_receipts (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            character_id integer NOT NULL,
            reset_at timestamptz NOT NULL DEFAULT now(),
            item_id integer NOT NULL,
            item_slot smallint NOT NULL,
            talent_points_before integer NOT NULL,
            talent_points_refunded integer NOT NULL CHECK (talent_points_refunded >= 0),
            talents jsonb NOT NULL DEFAULT '[]'::jsonb
        );
        CREATE INDEX IF NOT EXISTS ix_finalise2_talent_reset_receipts_character
            ON public.finalise2_talent_reset_receipts (character_id);
        """;

    private const long SchemaAdvisoryLock = 0x434C415353434847; // "CLASSCHG"

    private const short WornLocation = 0;
    private const short KitBagLocation = 1;

    // Character equipment and the fashion slot (EquipmentSlots 0..14). Mount slots 15..20
    // are class-neutral (every mount and mount part is usable by all four classes) and stay.
    internal const short LastCharacterSlot = 14;

    // Starter skills come from the published gameplay content, as character creation does.
    private const string GameplayRevision =
        "(SELECT publication.revision FROM public.gameplay_content_publication publication " +
        "WHERE publication.family = 'gameplay')";

    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static int _schemaEnsured;

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard = new(dataSource);

    public async Task<CharacterClassChangeResult> ChangeClassAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        byte expectedProfession,
        byte targetProfession,
        int freeLevel,
        CancellationToken cancellationToken)
    {
        if (targetProfession > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(targetProfession));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var fence = await _ownershipGuard.LockCurrentAsync(
            connection, transaction, subject, ownership, cancellationToken);
        if (fence.Status == PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterClassChangeStatus.CharacterNotFound);
        }

        fence.RequireCurrent();

        string name;
        byte profession;
        int level;
        int pointsBefore;
        await using (var read = new NpgsqlCommand(
            """
            SELECT name, profession, fighter_job_lv, "SkillPoint"
            FROM public.character_base
            WHERE id = @id AND account_id = @account AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("id", subject.CharacterId);
            read.Parameters.AddWithValue("account", subject.AccountId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterClassChangeStatus.CharacterNotFound);
            }

            name = reader.GetString(0);
            profession = (byte)reader.GetInt16(1);
            level = reader.GetInt32(2);
            pointsBefore = reader.GetInt32(3);
        }

        if (profession != expectedProfession)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterClassChangeStatus.Stale, profession);
        }

        if (profession == targetProfession)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CharacterClassChangeStatus.SameClass, profession);
        }

        await using (var worn = new NpgsqlCommand(
            "SELECT count(*) FROM public.character_items " +
            "WHERE user_id = @id AND item_location = @worn AND slot_index <= @last;",
            connection,
            transaction))
        {
            worn.Parameters.AddWithValue("id", subject.CharacterId);
            worn.Parameters.AddWithValue("worn", WornLocation);
            worn.Parameters.AddWithValue("last", LastCharacterSlot);
            if ((long)(await worn.ExecuteScalarAsync(cancellationToken))! > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterClassChangeStatus.EquipmentWorn, profession);
            }
        }

        PermitUse? permit = null;
        if (ClassChangePolicy.RequiresPermit(level, freeLevel))
        {
            permit = await UsePermitAsync(connection, transaction, subject, cancellationToken);
            if (permit is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterClassChangeStatus.PermitMissing, profession);
            }
        }

        // Talents: the old tree's levels become talent points (IGG). REVIEW FIX (blocker):
        // the rows are kept at rank 0 instead of deleted, so their outbox_revision carries on
        // when the character shifts back and rebuys them (outbox_events is append-only and
        // unique per aggregate version), and the receipt below advances the talent command
        // epoch so the rebuy is not answered as a duplicate of the first purchase.
        var talents = new List<(int TalentId, int Rank, long Revision)>();
        await using (var read = new NpgsqlCommand(
            $"""
            SELECT ct.talent_id, ct.rank, ct.outbox_revision
            FROM public.character_talents ct
            JOIN public.gameplay_talent_definitions tt
              ON tt.id = ct.talent_id AND tt.revision = {GameplayRevision}
            WHERE ct.user_id = @id AND tt.class_id <> @target AND ct.rank > 0
            ORDER BY ct.talent_id
            FOR UPDATE OF ct;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("id", subject.CharacterId);
            read.Parameters.AddWithValue("target", (short)targetProfession);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                talents.Add((reader.GetInt32(0), reader.GetInt16(1), reader.GetInt64(2)));
            }
        }

        var talentReceipts = new List<object>(talents.Count);
        long refundTotal = 0;
        foreach (var talent in talents)
        {
            var paid = await ReadTalentPurchasesAsync(
                connection, transaction, subject, talent.TalentId, talent.Rank, cancellationToken);
            var basis = "paid";
            if (!ClassChangePolicy.TryPaidRefund(talent.Rank, talent.Revision, paid, out var refundOne))
            {
                basis = "current-price";
                refundOne = ClassChangePolicy.TalentRefund(talent.Rank);
            }

            refundTotal += refundOne;
            talentReceipts.Add(new { talent = talent.TalentId, rank = talent.Rank, refund = refundOne, basis });
        }

        var refund = ClassChangePolicy.SaturateRefund(refundTotal, pointsBefore);
        await using (var reset = new NpgsqlCommand(
            """
            UPDATE public.character_talents
            SET rank = 0, updated_at = now()
            WHERE user_id = @id AND talent_id = ANY(@talents);
            """,
            connection,
            transaction))
        {
            reset.Parameters.AddWithValue("id", subject.CharacterId);
            reset.Parameters.AddWithValue("talents", talents.Select(static t => t.TalentId).ToArray());
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        // Skills: seal every skill the new class cannot use, unseal what it sealed before,
        // then grant the new class's start skills (character creation's own rule).
        var sealedSkills = await ReadSkillsAsync(
            connection, transaction,
            $"""
            WITH moved AS (
                DELETE FROM public.character_skills cs
                USING public.gameplay_skill_combat_definitions d
                WHERE cs.user_id = @id
                  AND d.skill_id = cs.skill_id
                  AND d.revision = {GameplayRevision}
                  AND NOT (@target = ANY(d.class_ids))
                RETURNING cs.user_id, cs.skill_id, cs.skill_level, cs.acquired_at, cs.source
            ), kept AS (
                INSERT INTO public.character_sealed_skills (user_id, skill_id, skill_level, acquired_at, source)
                SELECT user_id, skill_id, skill_level, acquired_at, source FROM moved
                ON CONFLICT (user_id, skill_id) DO UPDATE
                SET skill_level = EXCLUDED.skill_level,
                    acquired_at = EXCLUDED.acquired_at,
                    source = EXCLUDED.source,
                    sealed_at = now()
                RETURNING skill_id
            )
            SELECT skill_id FROM kept ORDER BY skill_id;
            """,
            subject.CharacterId, targetProfession, cancellationToken);

        // REVIEW FIX: a sealed skill the character meanwhile owns again (a GM grant) keeps
        // the higher of the two levels instead of silently dropping the sealed copy.
        var unsealedSkills = await ReadSkillsAsync(
            connection, transaction,
            $"""
            WITH back AS (
                DELETE FROM public.character_sealed_skills s
                USING public.gameplay_skill_combat_definitions d
                WHERE s.user_id = @id
                  AND d.skill_id = s.skill_id
                  AND d.revision = {GameplayRevision}
                  AND @target = ANY(d.class_ids)
                RETURNING s.user_id, s.skill_id, s.skill_level, s.acquired_at, s.source
            ), restored AS (
                INSERT INTO public.character_skills (user_id, skill_id, skill_level, acquired_at, source)
                SELECT user_id, skill_id, skill_level, acquired_at, source FROM back
                ON CONFLICT (user_id, skill_id) DO UPDATE
                SET skill_level = GREATEST(public.character_skills.skill_level, EXCLUDED.skill_level)
                RETURNING skill_id
            )
            SELECT skill_id FROM restored ORDER BY skill_id;
            """,
            subject.CharacterId, targetProfession, cancellationToken);

        var grantedSkills = await ReadSkillsAsync(
            connection, transaction,
            $"""
            WITH granted AS (
                INSERT INTO public.character_skills (user_id, skill_id, skill_level, source)
                SELECT @id, st.skill_id, COALESCE(st.skill_level, 1), 'class-change'
                FROM public.gameplay_skill_combat_definitions st
                WHERE @target = ANY(st.class_ids)
                  AND st.revision = {GameplayRevision}
                  AND ((st.previous_skill_id IS NULL
                        AND COALESCE(st.min_level, 1) <= 1
                        AND st.skill_level = 1)
                       OR st.skill_id = 4904)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM public.character_skills existing
                      JOIN public.gameplay_skill_combat_definitions existing_template
                        ON existing_template.skill_id = existing.skill_id
                       AND existing_template.revision = st.revision
                      WHERE existing.user_id = @id
                        AND existing_template.base_name = st.base_name
                        AND COALESCE(existing_template.skill_level, 0) > COALESCE(st.skill_level, 0)
                  )
                ON CONFLICT (user_id, skill_id) DO NOTHING
                RETURNING skill_id
            )
            SELECT skill_id FROM granted ORDER BY skill_id;
            """,
            subject.CharacterId, targetProfession, cancellationToken);

        var clearedZodiac = await ClearAttackZodiacSelectionsAsync(
            connection, transaction, subject.CharacterId, targetProfession, cancellationToken);

        var pointsAfter = pointsBefore + refund;
        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET profession = @target,
                "SkillPoint" = @points
            WHERE id = @id AND profession = @from;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("target", (short)targetProfession);
            update.Parameters.AddWithValue("points", pointsAfter);
            update.Parameters.AddWithValue("id", subject.CharacterId);
            update.Parameters.AddWithValue("from", (short)profession);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterClassChangeStatus.Stale, profession);
            }
        }

        long receiptId;
        await using (var receipt = new NpgsqlCommand(
            """
            INSERT INTO public.character_class_change_receipts (
                character_id, account_id, from_profession, to_profession, character_level,
                permit_item_id, permit_slot, permit_ledger_inbox_id, talent_points_before,
                talent_points_refunded, talents, sealed_skills, unsealed_skills, granted_skills,
                cleared_zodiac)
            VALUES (@id, @account, @from, @target, @level, @permit, @slot, @ledger, @before, @refund,
                    @talents::jsonb, @sealed::jsonb, @unsealed::jsonb, @granted::jsonb, @zodiac::jsonb)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            receipt.Parameters.AddWithValue("id", subject.CharacterId);
            receipt.Parameters.AddWithValue("account", subject.AccountId);
            receipt.Parameters.AddWithValue("from", (short)profession);
            receipt.Parameters.AddWithValue("target", (short)targetProfession);
            receipt.Parameters.AddWithValue("level", level);
            receipt.Parameters.AddWithValue("permit", permit is null ? DBNull.Value : ClassChangePolicy.PermitItemId);
            receipt.Parameters.AddWithValue("slot", permit is null ? DBNull.Value : (short)permit.Value.Slot);
            receipt.Parameters.AddWithValue(
                "ledger", permit?.LedgerInboxId is { } inbox ? inbox : DBNull.Value);
            receipt.Parameters.AddWithValue("before", pointsBefore);
            receipt.Parameters.AddWithValue("refund", refund);
            receipt.Parameters.AddWithValue("talents", JsonSerializer.Serialize(talentReceipts));
            receipt.Parameters.AddWithValue("sealed", JsonSerializer.Serialize(sealedSkills));
            receipt.Parameters.AddWithValue("unsealed", JsonSerializer.Serialize(unsealedSkills));
            receipt.Parameters.AddWithValue("granted", JsonSerializer.Serialize(grantedSkills));
            receipt.Parameters.AddWithValue(
                "zodiac",
                JsonSerializer.Serialize(clearedZodiac.Select(static z => new { grid = z.Grid, skill = z.Skill })));
            receiptId = (long)(await receipt.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var log = new NpgsqlCommand(
            """
            INSERT INTO public.operator_log (actor_account_id, actor_character, command, target, detail)
            VALUES (@account, @name, @command, @target, @detail);
            """,
            connection,
            transaction))
        {
            log.Parameters.AddWithValue("account", subject.AccountId);
            log.Parameters.AddWithValue("name", name);
            log.Parameters.AddWithValue("command", LogCommand);
            log.Parameters.AddWithValue("target", subject.CharacterId.ToString());
            log.Parameters.AddWithValue(
                "detail",
                $"receipt={receiptId} profession {profession}->{targetProfession} level={level} " +
                $"permit={(permit is null ? "none" : $"4266@{permit.Value.Slot}")} talents={talents.Count} " +
                $"talent-points {pointsBefore}+{refund} sealed={sealedSkills.Count} " +
                $"unsealed={unsealedSkills.Count} granted={grantedSkills.Count} zodiac-cleared={clearedZodiac.Count}");
            await log.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            CharacterClassChangeStatus.Done,
            targetProfession,
            pointsAfter,
            refund,
            sealedSkills.Count,
            unsealedSkills.Count,
            grantedSkills.Count,
            permit is not null,
            receiptId);
    }

    /// <summary>Creates the two tables once per process (idempotent DDL, one runner at a time).</summary>
    internal static async Task EnsureSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaEnsured) != 0)
        {
            return;
        }

        // One DDL run at a time (concurrent CREATE ... IF NOT EXISTS can deadlock), across processes too.
        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _schemaEnsured) != 0)
            {
                return;
            }

            await using var schema = await connection.BeginTransactionAsync(cancellationToken);
            await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", connection, schema))
            {
                guard.Parameters.AddWithValue("key", SchemaAdvisoryLock);
                await guard.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var ddl = new NpgsqlCommand(SchemaSql, connection, schema))
            {
                await ddl.ExecuteNonQueryAsync(cancellationToken);
            }

            await schema.CommitAsync(cancellationToken);
            Volatile.Write(ref _schemaEnsured, 1);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private readonly record struct PermitUse(int Slot, long? LedgerInboxId);

    /// <summary>
    /// Uses one Class Shift Permit from the bag (lowest slot first, unexpired). REVIEW FIX
    /// (reconciliation): like every durable inventory consumer it captures the economy
    /// baseline first, bumps inventory_revision once and appends one
    /// character_inventory_ledger row (delete, or update for a stack) at the new revision,
    /// backed by a command_audit + command_inbox row (family class_change_permit), as the
    /// quest-reward grant does. A legacy character whose baseline cannot be captured any
    /// more (no baseline and revisions already above 0) keeps the old audit-only path; the
    /// reconciliation cannot check such a character's items anyway. Returns null if there
    /// is no usable permit.
    /// </summary>
    private static async Task<PermitUse?> UsePermitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        var ledgered = await PostgresCharacterEconomyBaseline.EnsureAsync(
            connection,
            transaction,
            subject.AccountId,
            subject.CharacterId,
            commandTimeoutSeconds: 30,
            cancellationToken);

        long itemRow;
        int slot;
        int stack;
        string beforeState;
        await using (var find = new NpgsqlCommand(
            """
            SELECT item.id, item.slot_index, item.stack, to_jsonb(item)::text
            FROM public.character_items item
            WHERE item.user_id = @id AND item.item_location = @bag AND item.prop_id = @permit
              AND item.stack > 0
              AND (item.expires_at IS NULL OR item.expires_at > now())
            ORDER BY item.slot_index
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            find.Parameters.AddWithValue("id", subject.CharacterId);
            find.Parameters.AddWithValue("bag", KitBagLocation);
            find.Parameters.AddWithValue("permit", ClassChangePolicy.PermitItemId);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            itemRow = reader.GetInt64(0);
            slot = reader.GetInt16(1);
            stack = reader.GetInt16(2);
            beforeState = reader.GetString(3);
        }

        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO public.character_item_audit (
                source, action, user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, item_exp, old_item)
            SELECT 'class-change', CASE WHEN item.stack = 1 THEN 'delete' ELSE 'update' END,
                   item.user_id, item.item_location, item.slot_index, item.prop_id,
                   item.item_quality, item.item_grade, item.item_exp, to_jsonb(item)
            FROM public.character_items item
            WHERE item.id = @row;
            """,
            connection,
            transaction))
        {
            audit.Parameters.AddWithValue("row", itemRow);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        string? afterState = null;
        if (stack == 1)
        {
            await using var delete = new NpgsqlCommand(
                "DELETE FROM public.character_items WHERE id = @row AND stack = 1;",
                connection,
                transaction);
            delete.Parameters.AddWithValue("row", itemRow);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException("The class shift permit delete was not exact.");
            }
        }
        else
        {
            await using var less = new NpgsqlCommand(
                """
                UPDATE public.character_items item
                SET stack = stack - 1, updated_at = now()
                WHERE item.id = @row AND item.stack > 1
                RETURNING to_jsonb(item)::text;
                """,
                connection,
                transaction);
            less.Parameters.AddWithValue("row", itemRow);
            afterState = await less.ExecuteScalarAsync(cancellationToken) as string ??
                throw new InvalidDataException("The class shift permit update was not exact.");
        }

        long revision;
        await using (var bump = new NpgsqlCommand(
            """
            UPDATE public.character_base SET inventory_revision = inventory_revision + 1
            WHERE id = @id AND account_id = @account
            RETURNING inventory_revision;
            """,
            connection,
            transaction))
        {
            bump.Parameters.AddWithValue("id", subject.CharacterId);
            bump.Parameters.AddWithValue("account", subject.AccountId);
            revision = await bump.ExecuteScalarAsync(cancellationToken) is long value
                ? value
                : throw new InvalidDataException("The inventory revision bump was not exact.");
        }

        if (!ledgered)
        {
            Console.WriteLine(
                $"[class-change] character={subject.CharacterId} has no economy baseline; " +
                "permit use recorded in character_item_audit only (legacy path)");
            return new PermitUse(slot, null);
        }

        var inboxId = await InsertPermitEvidenceAsync(
            connection, transaction, subject, itemRow, slot, revision, cancellationToken);
        await using (var ledger = new NpgsqlCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id, inventory_revision, entry_ordinal,
                item_instance_id, mutation_kind, before_state, after_state, reason_code)
            VALUES (@inbox, @account, @id, @revision, 0, @item, @kind, @before, @after, @reason);
            """,
            connection,
            transaction))
        {
            ledger.Parameters.AddWithValue("inbox", inboxId);
            ledger.Parameters.AddWithValue("account", subject.AccountId);
            ledger.Parameters.AddWithValue("id", subject.CharacterId);
            ledger.Parameters.AddWithValue("revision", revision);
            ledger.Parameters.AddWithValue("item", itemRow);
            ledger.Parameters.AddWithValue("kind", afterState is null ? "delete" : "update");
            ledger.Parameters.Add("before", NpgsqlDbType.Jsonb).Value = beforeState;
            ledger.Parameters.Add("after", NpgsqlDbType.Jsonb).Value =
                afterState is null ? DBNull.Value : afterState;
            ledger.Parameters.AddWithValue("reason", PermitCommandFamily);
            if (await ledger.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException("The class shift permit ledger row was not inserted once.");
            }
        }

        return new PermitUse(slot, inboxId);
    }

    private static async Task<long> InsertPermitEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long itemRow,
        int slot,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        // One fresh operation per use: there is no client replay to dedupe (the change commits
        // or rolls back as one transaction), and a revision alone could repeat after a repair.
        var operationId = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"class-change-permit:{subject.CharacterId}:{inventoryRevision}:{Guid.NewGuid():N}"));
        var payload = JsonSerializer.Serialize(new
        {
            itemId = ClassChangePolicy.PermitItemId,
            itemInstanceId = itemRow,
            slot,
            inventoryRevision
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var principalKey = $"account:{subject.AccountId}";
        var aggregateKey = $"character:{subject.CharacterId}";
        long auditId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @commandFamily, @operationId, @hash,
                    'committed', @payload)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("principalKey", principalKey);
            command.Parameters.AddWithValue("aggregateKey", aggregateKey);
            command.Parameters.AddWithValue("commandFamily", PermitCommandFamily);
            command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value = operationId;
            command.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = hash;
            command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
            auditId = await command.ExecuteScalarAsync(cancellationToken) is long value && value > 0
                ? value
                : throw new InvalidDataException("Class change permit audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @commandFamily, @operationId, @hash,
                    1, 'committed', @payload, @hash, @auditId)
            RETURNING id;
            """,
            connection,
            transaction);
        inbox.Parameters.AddWithValue("principalKey", principalKey);
        inbox.Parameters.AddWithValue("aggregateKey", aggregateKey);
        inbox.Parameters.AddWithValue("commandFamily", PermitCommandFamily);
        inbox.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value = operationId;
        inbox.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = hash;
        inbox.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        inbox.Parameters.AddWithValue("auditId", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken) is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException("Class change permit inbox returned no identity.");
    }

    /// <summary>
    /// The committed durable talent purchases of one talent, newest first (rank, cost paid,
    /// aggregate revision), at most <paramref name="rank"/> of them. Read-only.
    /// </summary>
    private static async Task<List<(int Rank, long Cost, long Revision)>> ReadTalentPurchasesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        int talentId,
        int rank,
        CancellationToken cancellationToken)
    {
        var purchases = new List<(int, long, long)>();
        await using var command = new NpgsqlCommand(
            """
            SELECT (inbox.result_payload ->> 'rank')::integer,
                   (inbox.result_payload ->> 'cost')::bigint,
                   (inbox.result_payload ->> 'aggregateRevision')::bigint
            FROM public.command_inbox inbox
            WHERE inbox.principal_type = 'account'
              AND inbox.principal_key = @principal
              AND inbox.aggregate_type = 'character_talent'
              AND inbox.aggregate_key = @aggregate
              AND inbox.command_family = 'talent_upgrade'
              AND inbox.result_code = 'committed'
            ORDER BY (inbox.result_payload ->> 'aggregateRevision')::bigint DESC
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("principal", subject.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregate",
            Godswar.Server.Infrastructure.Talents.TalentUpgradePersistenceCodec.AggregateKey(subject.CharacterId, talentId));
        command.Parameters.AddWithValue("limit", Math.Max(1, rank));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                continue;
            }

            purchases.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return purchases;
    }

    /// <summary>
    /// REVIEW FIX: attack zodiac grids (0-7) only hold a skill of the character's own class
    /// (<see cref="ZodiacSkillGridSelectionCatalog.IsAllowedForClass"/>), so selections the
    /// new class cannot make are cleared (-1). Grid levels and the class-neutral defense grids
    /// (8-15) are kept. The cleared selections go into the receipt.
    /// </summary>
    private static async Task<List<(int Grid, int Skill)>> ClearAttackZodiacSelectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        byte targetProfession,
        CancellationToken cancellationToken)
    {
        var selected = new List<(int Grid, int Skill)>();
        await using (var read = new NpgsqlCommand(
            """
            SELECT grid_index, selected_skill_id
            FROM public.character_zodiac_skill_grids
            WHERE user_id = @id AND selected_skill_id <> -1
            ORDER BY grid_index
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("id", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                selected.Add((reader.GetInt16(0), reader.GetInt32(1)));
            }
        }

        var cleared = selected
            .Where(z => !ZodiacSkillGridSelectionCatalog.IsDefenseGrid(z.Grid) &&
                        !ZodiacSkillGridSelectionCatalog.IsAllowedForClass(targetProfession, z.Skill))
            .ToList();
        if (cleared.Count == 0)
        {
            return cleared;
        }

        await using var clear = new NpgsqlCommand(
            """
            UPDATE public.character_zodiac_skill_grids
            SET selected_skill_id = -1, updated_at = now()
            WHERE user_id = @id AND grid_index = ANY(@grids);
            """,
            connection,
            transaction);
        clear.Parameters.AddWithValue("id", characterId);
        clear.Parameters.AddWithValue("grids", cleared.Select(static z => (short)z.Grid).ToArray());
        await clear.ExecuteNonQueryAsync(cancellationToken);
        return cleared;
    }

    private static async Task<List<int>> ReadSkillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int characterId,
        byte targetProfession,
        CancellationToken cancellationToken)
    {
        var skills = new List<int>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("target", (short)targetProfession);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            skills.Add(reader.GetInt32(0));
        }

        return skills;
    }

    // FINALISE2 (F-54): Zeus' Baptism. Every spent talent rank becomes talent points again, with the class change's
    // own refund rule (the price actually paid when on record, else today's price), the ranks go to 0 (rows kept, so
    // their outbox revisions carry on) and one item 4269 is used up, all in one transaction under the ownership fence.
    internal async Task<(bool Done, int PointsAfter, int Refund, int Talents)> ResetTalentsAsync(
        CommandSubject subject, PlayerOwnershipFence ownership, int itemId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var fence = await _ownershipGuard.LockCurrentAsync(connection, transaction, subject, ownership, cancellationToken);
        if (fence.Status == PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, 0, 0, 0);
        }

        fence.RequireCurrent();
        int pointsBefore;
        await using (var read = new NpgsqlCommand(
            "SELECT \"SkillPoint\" FROM public.character_base WHERE id = @id AND account_id = @account FOR UPDATE;",
            connection, transaction))
        {
            read.Parameters.AddWithValue("id", subject.CharacterId);
            read.Parameters.AddWithValue("account", subject.AccountId);
            if (await read.ExecuteScalarAsync(cancellationToken) is not int points)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, 0, 0, 0);
            }

            pointsBefore = points;
        }

        short slot;
        await using (var item = new NpgsqlCommand(
            "SELECT slot_index FROM public.character_items WHERE user_id = @id AND item_location = @bag " +
            "AND prop_id = @item ORDER BY slot_index LIMIT 1 FOR UPDATE;", connection, transaction))
        {
            item.Parameters.AddWithValue("id", subject.CharacterId);
            item.Parameters.AddWithValue("bag", KitBagLocation);
            item.Parameters.AddWithValue("item", itemId);
            if (await item.ExecuteScalarAsync(cancellationToken) is not short found)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, pointsBefore, 0, 0);
            }

            slot = found;
        }

        var talents = new List<(int TalentId, int Rank, long Revision)>();
        await using (var read = new NpgsqlCommand(
            "SELECT talent_id, rank, outbox_revision FROM public.character_talents " +
            "WHERE user_id = @id AND rank > 0 ORDER BY talent_id FOR UPDATE;", connection, transaction))
        {
            read.Parameters.AddWithValue("id", subject.CharacterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                talents.Add((reader.GetInt32(0), reader.GetInt16(1), reader.GetInt64(2)));
            }
        }

        var receipts = new List<object>(talents.Count);
        long total = 0;
        foreach (var talent in talents)
        {
            var paid = await ReadTalentPurchasesAsync(connection, transaction, subject, talent.TalentId, talent.Rank,
                cancellationToken);
            var basis = "paid";
            if (!ClassChangePolicy.TryPaidRefund(talent.Rank, talent.Revision, paid, out var one))
            {
                basis = "current-price";
                one = ClassChangePolicy.TalentRefund(talent.Rank);
            }

            total += one;
            receipts.Add(new { talent = talent.TalentId, rank = talent.Rank, refund = one, basis });
        }

        var refund = ClassChangePolicy.SaturateRefund(total, pointsBefore);
        await using (var reset = new NpgsqlCommand(
            "UPDATE public.character_talents SET rank = 0, updated_at = now() WHERE user_id = @id AND rank > 0;",
            connection, transaction))
        {
            reset.Parameters.AddWithValue("id", subject.CharacterId);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var use = new NpgsqlCommand(
            // Delete a single item first, then shrink a stack: one of the two touches the row.
            "DELETE FROM public.character_items WHERE user_id = @id AND item_location = @bag AND slot_index = @slot " +
            "AND stack = 1; UPDATE public.character_items SET stack = stack - 1 WHERE user_id = @id AND " +
            "item_location = @bag AND slot_index = @slot AND stack > 1;",
            connection, transaction))
        {
            use.Parameters.AddWithValue("id", subject.CharacterId);
            use.Parameters.AddWithValue("bag", KitBagLocation);
            use.Parameters.AddWithValue("slot", slot);
            await use.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = pointsBefore + refund;
        await using (var update = new NpgsqlCommand(
            "UPDATE public.character_base SET \"SkillPoint\" = @points, inventory_revision = inventory_revision + 1 " +
            "WHERE id = @id;", connection, transaction))
        {
            update.Parameters.AddWithValue("points", after);
            update.Parameters.AddWithValue("id", subject.CharacterId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var receipt = new NpgsqlCommand(
            "INSERT INTO public.finalise2_talent_reset_receipts (character_id, item_id, item_slot, talent_points_before, " +
            "talent_points_refunded, talents) VALUES (@id, @item, @slot, @before, @refund, @talents::jsonb);",
            connection, transaction))
        {
            receipt.Parameters.AddWithValue("id", subject.CharacterId);
            receipt.Parameters.AddWithValue("item", itemId);
            receipt.Parameters.AddWithValue("slot", slot);
            receipt.Parameters.AddWithValue("before", pointsBefore);
            receipt.Parameters.AddWithValue("refund", refund);
            receipt.Parameters.AddWithValue("talents", JsonSerializer.Serialize(receipts));
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, after, refund, talents.Count);
    }
}

/// <summary>Start-up wiring, next to the identity store; never stops the server.</summary>
internal static class CharacterClassChangeRuntime
{
    public static void Initialize(string connectionString)
    {
        try
        {
            CharacterClassChangeControls.Store = new PostgresCharacterClassChangeStore(
                NpgsqlDataSource.Create(connectionString));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                "[class-change] start-up failed; the Class Shifter stays menu-only: " +
                error.Message);
        }
    }

}
