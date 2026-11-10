using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition?> TryLearnPlayerSkillBookAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var book = await ReadPlayerSkillBookAsync(
            connection,
            transaction,
            item.PropId,
            cancellationToken);
        if (book is null)
        {
            return null;
        }
        if (_gameplayContentRevision is null)
        {
            throw new InvalidDataException(
                "Player skill-book activation has no pinned gameplay " +
                "content revision.");
        }

        if (item.Stack < 1 ||
            book.SkillLevel is not (>= 1 and <= byte.MaxValue) ||
            book.SkillId < 0 ||
            string.IsNullOrWhiteSpace(book.BaseName))
        {
            return PlayerSkillBookRejection(
                PetDurableReceiptStatus.PlayerSkillBookInvalidState,
                bagSlot);
        }
        if (!book.ClassIds.Contains(
                checked((short)character.Profession)))
        {
            return PlayerSkillBookRejection(
                PetDurableReceiptStatus.PlayerSkillBookWrongClass,
                bagSlot);
        }
        if (character.Level < (book.MinLevel ?? 1) ||
            character.Level > (book.MaxLevel ?? int.MaxValue))
        {
            return PlayerSkillBookRejection(
                PetDurableReceiptStatus.PlayerSkillBookLevelRestricted,
                bagSlot);
        }

        var familySkills = await LockPlayerSkillFamilyAsync(
            connection,
            transaction,
            characterId,
            book.BaseName,
            cancellationToken);
        if (familySkills.Count > 1)
        {
            throw new InvalidDataException(
                "The character has duplicate learned rows for one player " +
                "skill family.");
        }
        if (familySkills.Any(skill =>
                skill.SkillId == book.SkillId ||
                skill.SkillLevel >= book.SkillLevel))
        {
            return PlayerSkillBookRejection(
                PetDurableReceiptStatus.PlayerSkillBookAlreadyLearned,
                bagSlot);
        }
        if (book.PreviousSkillId is { } previousSkillId &&
            !familySkills.Any(skill =>
                skill.SkillId == previousSkillId))
        {
            return PlayerSkillBookRejection(
                PetDurableReceiptStatus.PlayerSkillBookPriorTierRequired,
                bagSlot);
        }
        if (book.PreviousSkillId is null && familySkills.Count != 0)
        {
            return PlayerSkillBookRejection(
                PetDurableReceiptStatus.PlayerSkillBookAlreadyLearned,
                bagSlot);
        }

        await ReplacePlayerSkillFamilyAsync(
            connection,
            transaction,
            characterId,
            book,
            cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            characterId,
            bagSlot,
            item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);
        var evidence = new PlayerSkillLearnEvidence(
            item.ItemId,
            book.ItemId,
            bagSlot,
            book.SkillId,
            book.SkillLevel.GetValueOrDefault(),
            book.PreviousSkillId,
            book.BaseName,
            checked((short)character.Profession),
            character.Level,
            _itemContent.Templates.Revision.Sha256,
            book.Revision);
        if (!evidence.IsValid)
        {
            throw new InvalidDataException(
                "The committed player skill-book evidence is invalid.");
        }

        return new PetTransition(
            PetDurableReceiptStatus.PlayerSkillLearned,
            KitBagSlot: bagSlot,
            InventoryMutations:
            [
                new InventoryMutation(
                    item.ItemId,
                    consumed.MutationKind,
                    item.BeforeState,
                    consumed.AfterState,
                    "player_skill_book_consumed",
                    inventoryRevision)
            ],
            PlayerSkillLearn: evidence);
    }

    private async Task<PlayerSkillBookDefinition?>
        ReadPlayerSkillBookAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int itemId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                book.revision,
                item_id,
                skill_id,
                base_name,
                skill_level,
                class_ids,
                min_level,
                max_level,
                previous_skill_id,
                EXISTS (
                    SELECT 1
                    FROM public.gameplay_skill_combat_definitions skill
                    WHERE skill.revision = book.revision
                      AND skill.skill_id = book.skill_id
                      AND skill.base_name = book.base_name
                      AND skill.skill_level IS NOT DISTINCT FROM
                          book.skill_level
                      AND skill.class_ids = book.class_ids
                      AND skill.min_level IS NOT DISTINCT FROM
                          book.min_level
                      AND skill.max_level IS NOT DISTINCT FROM
                          book.max_level
                      AND skill.previous_skill_id IS NOT DISTINCT FROM
                          book.previous_skill_id
                ) AS definition_matches
            FROM public.gameplay_skill_book_definitions book
            WHERE book.item_id = @itemId
              AND book.revision = COALESCE(
                  @gameplayContentRevision,
                  (
                      SELECT publication.revision
                      FROM public.gameplay_content_publication publication
                      WHERE publication.family = 'gameplay'
                  )
              );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", itemId);
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var definitionMatches = reader.GetBoolean(9);
        var definition = new PlayerSkillBookDefinition(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt16(4),
            reader.GetFieldValue<short[]>(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The pinned player skill-book item is duplicated.");
        }
        if (!definitionMatches)
        {
            throw new InvalidDataException(
                "The player skill-book and combat definitions disagree.");
        }
        return definition;
    }

    private async Task<IReadOnlyList<LockedPlayerSkill>>
        LockPlayerSkillFamilyAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            string baseName,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT skill.skill_id, skill.skill_level
            FROM public.character_skills skill
            WHERE skill.user_id = @characterId
              AND skill.skill_id IN (
                  SELECT book.skill_id
                  FROM public.gameplay_skill_book_definitions book
                  WHERE book.base_name = @baseName
                    AND book.revision = COALESCE(
                        @gameplayContentRevision,
                        (
                            SELECT publication.revision
                            FROM public.gameplay_content_publication publication
                            WHERE publication.family = 'gameplay'
                        )
                    )
              )
            ORDER BY skill.skill_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("baseName", baseName);
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var skills = new List<LockedPlayerSkill>();
        while (await reader.ReadAsync(cancellationToken))
        {
            skills.Add(new LockedPlayerSkill(
                reader.GetInt32(0),
                reader.GetInt16(1)));
        }
        return skills;
    }

    private async Task ReplacePlayerSkillFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        PlayerSkillBookDefinition book,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_skills
            WHERE user_id = @characterId
              AND skill_id IN (
                  SELECT family.skill_id
                  FROM public.gameplay_skill_book_definitions family
                  WHERE family.base_name = @baseName
                    AND family.revision = COALESCE(
                        @gameplayContentRevision,
                        (
                            SELECT publication.revision
                            FROM public.gameplay_content_publication publication
                            WHERE publication.family = 'gameplay'
                        )
                    )
              );

            INSERT INTO public.character_skills (
                user_id,
                skill_id,
                skill_level,
                source
            )
            VALUES (
                @characterId,
                @skillId,
                @skillLevel,
                'skill-book'
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("baseName", book.BaseName);
        command.Parameters.AddWithValue("skillId", book.SkillId);
        command.Parameters.AddWithValue(
            "skillLevel",
            book.SkillLevel!.Value);
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        var expectedAffectedRows = book.PreviousSkillId.HasValue ? 2 : 1;
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            expectedAffectedRows)
        {
            throw new InvalidDataException(
                "The player skill family was not replaced exactly once.");
        }
    }

    private static PetTransition PlayerSkillBookRejection(
        PetDurableReceiptStatus status,
        int bagSlot) =>
        new(status, KitBagSlot: bagSlot);

    private sealed record PlayerSkillBookDefinition(
        string Revision,
        int ItemId,
        int SkillId,
        string BaseName,
        short? SkillLevel,
        short[] ClassIds,
        int? MinLevel,
        int? MaxLevel,
        int? PreviousSkillId);

    private sealed record LockedPlayerSkill(
        int SkillId,
        short SkillLevel);
}
