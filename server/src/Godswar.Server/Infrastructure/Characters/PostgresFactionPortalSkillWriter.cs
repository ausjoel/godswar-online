using Godswar.Server.Domain.Characters;
using Godswar.Server.Infrastructure.Database;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal static class PostgresFactionPortalSkillWriter
{
    public static async Task InsertAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, int profession,
        byte camp, string? gameplayContentRevision, CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("""
            INSERT INTO character_skills (user_id, skill_id, skill_level, source)
            SELECT @characterId, st.skill_id, 1, 'faction-starter'
            FROM gameplay_skill_combat_definitions st
            WHERE st.skill_id = @factionPortalSkillId
              AND st.revision = COALESCE(
                  @gameplayContentRevision,
                  (
                      SELECT publication.revision
                      FROM gameplay_content_publication publication
                      WHERE publication.family = 'gameplay'
                  )
              )
              AND @profession = ANY(st.class_ids)
            ON CONFLICT (user_id, skill_id) DO NOTHING;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("profession", (short)profession);
            command.Parameters.AddWithValue(
                "factionPortalSkillId",
                checked((int)FactionPortalSkillPolicy.ResolveCapitalPortalSkillId(
                    camp)));
            PostgresGameplayContentBinding.AddParameter(command, gameplayContentRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "Character creation did not seed exactly one faction portal skill.");
            }
        }
    }
}
