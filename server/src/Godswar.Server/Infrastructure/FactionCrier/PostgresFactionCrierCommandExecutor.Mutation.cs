using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed partial class PostgresFactionCrierCommandExecutor
{
    private async Task UpdateCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<FactionCrierCommand> envelope,
        LockedCharacter before,
        CharacterWalletSnapshot wallet,
        PlayerExperienceProgression fighter,
        int talentAfter,
        DerivedRevisions revisions,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET fighter_job_lv = @level,
                fighter_job_exp = @experience,
                "SkillPoint" = @talentPoints,
                "Money" = @silver,
                "Stone" = @gold,
                "BindingGold" = @bindingGold,
                wallet_revision = @walletRevision,
                inventory_revision = @inventoryRevision,
                progression_reward_revision = @progressionRevision,
                faction_crier_revision = @factionCrierRevision
            WHERE id = @characterId
              AND account_id = @accountId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
              AND fighter_job_lv = @expectedLevel
              AND fighter_job_exp = @expectedExperience
              AND "SkillPoint" = @expectedTalentPoints
              AND "Money" = @expectedSilver
              AND "Stone" = @expectedGold
              AND "BindingGold" = @expectedBindingGold
              AND wallet_revision = @expectedWalletRevision
              AND inventory_revision = @expectedInventoryRevision
              AND progression_reward_revision = @expectedProgressionRevision
              AND faction_crier_revision = @expectedFactionCrierRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("level", fighter.Level);
        command.Parameters.AddWithValue("experience", fighter.Experience);
        command.Parameters.AddWithValue("talentPoints", talentAfter);
        command.Parameters.AddWithValue("silver", wallet.Silver);
        command.Parameters.AddWithValue("gold", wallet.Gold);
        command.Parameters.AddWithValue("bindingGold", wallet.BindingGold);
        command.Parameters.AddWithValue("walletRevision", revisions.Wallet);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            revisions.Inventory);
        command.Parameters.AddWithValue(
            "progressionRevision",
            revisions.Progression);
        command.Parameters.AddWithValue(
            "factionCrierRevision",
            revisions.FactionCrier);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue("realmId", envelope.Command.RealmId);
        command.Parameters.AddWithValue("expectedLevel", before.Level);
        command.Parameters.AddWithValue(
            "expectedExperience",
            before.Experience);
        command.Parameters.AddWithValue(
            "expectedTalentPoints",
            before.TalentPoints);
        command.Parameters.AddWithValue("expectedSilver", before.Silver);
        command.Parameters.AddWithValue("expectedGold", before.Gold);
        command.Parameters.AddWithValue(
            "expectedBindingGold",
            before.BindingGold);
        command.Parameters.AddWithValue(
            "expectedWalletRevision",
            before.WalletRevision);
        command.Parameters.AddWithValue(
            "expectedInventoryRevision",
            before.InventoryRevision);
        command.Parameters.AddWithValue(
            "expectedProgressionRevision",
            before.ProgressionRevision);
        command.Parameters.AddWithValue(
            "expectedFactionCrierRevision",
            before.FactionCrierRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Faction Crier character mutation was not exact.");
        }
    }

    private async Task InsertLedgersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandEnvelope<FactionCrierCommand> envelope,
        LockedCharacter before,
        FactionCrierExecutionPlan plan,
        CharacterWalletSnapshot wallet,
        DerivedRevisions revisions,
        IReadOnlyList<AppliedInventoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations.Count == 0)
        {
            throw new InvalidDataException(
                "A Faction Crier settlement has no inventory evidence.");
        }

        await using (var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id,
                account_id,
                character_id,
                inventory_revision,
                entry_ordinal,
                item_instance_id,
                mutation_kind,
                state_contract_version,
                before_state,
                after_state,
                reason_code
            )
            VALUES (
                @inboxId,
                @accountId,
                @characterId,
                @inventoryRevision,
                @entryOrdinal,
                @itemInstanceId,
                @mutationKind,
                1,
                @beforeState,
                @afterState,
                'faction_crier'
            );
            """,
            connection,
            transaction))
        {
            for (var index = 0; index < mutations.Count; index++)
            {
                var mutation = mutations[index];
                command.Parameters.Clear();
                command.Parameters.AddWithValue("inboxId", inboxId);
                command.Parameters.AddWithValue(
                    "accountId",
                    envelope.Subject.AccountId);
                command.Parameters.AddWithValue(
                    "characterId",
                    envelope.Subject.CharacterId);
                command.Parameters.AddWithValue(
                    "inventoryRevision",
                    revisions.Inventory);
                command.Parameters.AddWithValue(
                    "entryOrdinal",
                    checked((short)index));
                command.Parameters.AddWithValue(
                    "itemInstanceId",
                    mutation.ItemInstanceId);
                command.Parameters.AddWithValue(
                    "mutationKind",
                    mutation.Kind);
                AddJson(command, "beforeState", mutation.BeforeState);
                AddJson(command, "afterState", mutation.AfterState);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidDataException(
                        "The Faction Crier inventory ledger append was not exact.");
                }
            }
        }

        if (plan.CurrencyCost == 0)
        {
            return;
        }

        var (code, balanceBefore, balanceAfter) = plan.Currency switch
        {
            FactionCrierCurrency.Silver =>
                ("silver", before.Silver, wallet.Silver),
            FactionCrierCurrency.Gold =>
                ("gold", before.Gold, wallet.Gold),
            FactionCrierCurrency.BoundGold =>
                ("binding_gold", before.BindingGold, wallet.BindingGold),
            _ => throw new InvalidDataException(
                "The paid Faction Crier currency is invalid.")
        };
        if (balanceBefore - balanceAfter != plan.CurrencyCost)
        {
            throw new InvalidDataException(
                "The Faction Crier debit does not match its plan.");
        }

        await using var currency = CreateCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id,
                account_id,
                character_id,
                wallet_revision,
                currency_code,
                delta,
                balance_before,
                balance_after,
                reason_code
            )
            VALUES (
                @inboxId,
                @accountId,
                @characterId,
                @walletRevision,
                @currencyCode,
                @delta,
                @balanceBefore,
                @balanceAfter,
                'faction_crier'
            );
            """,
            connection,
            transaction);
        currency.Parameters.AddWithValue("inboxId", inboxId);
        currency.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        currency.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        currency.Parameters.AddWithValue(
            "walletRevision",
            revisions.Wallet);
        currency.Parameters.AddWithValue("currencyCode", code);
        currency.Parameters.AddWithValue("delta", -plan.CurrencyCost);
        currency.Parameters.AddWithValue("balanceBefore", balanceBefore);
        currency.Parameters.AddWithValue("balanceAfter", balanceAfter);
        if (await currency.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Faction Crier currency ledger append was not exact.");
        }
    }

    private static void AddJson(
        NpgsqlCommand command,
        string name,
        string? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Jsonb);
        parameter.Value = value is null ? DBNull.Value : value;
    }
}
