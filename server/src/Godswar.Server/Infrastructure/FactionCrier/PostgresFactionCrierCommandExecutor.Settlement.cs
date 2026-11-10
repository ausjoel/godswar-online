using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed partial class PostgresFactionCrierCommandExecutor
{
    private async Task InsertSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<FactionCrierCommand> envelope,
        FactionCrierExecutionPlan plan,
        FactionCrierExecutionReceipt receipt,
        DerivedRevisions revisions,
        long inboxId,
        long auditId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        if (plan.Operation == FactionCrierOperation.DailyClaim)
        {
            await InsertDailyClaimAsync(
                connection,
                transaction,
                envelope,
                plan,
                revisions,
                inboxId,
                auditId,
                eventId,
                cancellationToken);
            return;
        }
        if (plan.Operation == FactionCrierOperation.WeeklyReclaim)
        {
            await InsertWeeklyReclaimAsync(
                connection,
                transaction,
                envelope,
                plan,
                revisions,
                inboxId,
                auditId,
                eventId,
                cancellationToken);
            return;
        }

        await using var command = CreateCommand(
            """
            INSERT INTO public.faction_crier_exchange_settlements (
                account_id,
                character_id,
                realm_id,
                operation,
                sub_id,
                consumed_item_ids,
                granted_item_id,
                currency_code,
                currency_cost,
                awarded_experience,
                awarded_talent_points,
                balance_revision,
                item_content_revision,
                command_inbox_id,
                audit_id,
                outbox_event_id,
                wallet_revision,
                inventory_revision,
                progression_revision,
                faction_crier_revision
            )
            VALUES (
                @accountId,
                @characterId,
                @realmId,
                @operation,
                @subId,
                @consumedItemIds,
                @grantedItemId,
                @currencyCode,
                @currencyCost,
                @awardedExperience,
                @awardedTalentPoints,
                @balanceRevision,
                @itemContentRevision,
                @inboxId,
                @auditId,
                @eventId,
                @walletRevision,
                @inventoryRevision,
                @progressionRevision,
                @factionCrierRevision
            );
            """,
            connection,
            transaction);
        AddSettlementIdentity(command, envelope, revisions, inboxId, auditId, eventId);
        command.Parameters.AddWithValue(
            "operation",
            plan.Operation == FactionCrierOperation.RenewNameplate
                ? "renewal"
                : "turn_in");
        command.Parameters.AddWithValue("subId", plan.NativeSuccessSubId > 0
            ? envelope.Command.SubId
            : throw new InvalidDataException("Missing Faction Crier result."));
        command.Parameters.Add(
            "consumedItemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            plan.ConsumedItemIds.ToArray();
        command.Parameters.Add(
            "grantedItemId",
            NpgsqlDbType.Integer).Value =
            plan.GrantedItemId is { } granted
                ? granted
                : DBNull.Value;
        command.Parameters.AddWithValue(
            "currencyCode",
            CurrencyCode(plan.Currency));
        command.Parameters.AddWithValue("currencyCost", plan.CurrencyCost);
        command.Parameters.AddWithValue(
            "awardedExperience",
            receipt.AwardedExperience);
        command.Parameters.AddWithValue(
            "awardedTalentPoints",
            receipt.AwardedTalentPoints);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Faction Crier exchange settlement was not exact.");
        }
    }

    private async Task InsertDailyClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<FactionCrierCommand> envelope,
        FactionCrierExecutionPlan plan,
        DerivedRevisions revisions,
        long inboxId,
        long auditId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            ClaimInsertSql("faction_crier_daily_claims", "claim_day"),
            connection,
            transaction);
        AddSettlementIdentity(command, envelope, revisions, inboxId, auditId, eventId);
        command.Parameters.AddWithValue(
            "period",
            plan.ClaimDay ?? throw new InvalidDataException(
                "The daily claim has no day."));
        command.Parameters.AddWithValue(
            "plateItemId",
            plan.GrantedItemId ?? throw new InvalidDataException(
                "The daily claim has no Nameplate."));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Faction Crier daily claim was not exact.");
        }
    }

    private async Task InsertWeeklyReclaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<FactionCrierCommand> envelope,
        FactionCrierExecutionPlan plan,
        DerivedRevisions revisions,
        long inboxId,
        long auditId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            ClaimInsertSql(
                "faction_crier_weekly_reclaims",
                "week_start",
                includeGoldCost: true),
            connection,
            transaction);
        AddSettlementIdentity(command, envelope, revisions, inboxId, auditId, eventId);
        command.Parameters.AddWithValue(
            "period",
            plan.ClaimWeekStart ?? throw new InvalidDataException(
                "The weekly reclaim has no week."));
        command.Parameters.AddWithValue(
            "plateItemId",
            plan.GrantedItemId ?? throw new InvalidDataException(
                "The weekly reclaim has no Nameplate."));
        command.Parameters.AddWithValue("goldCost", plan.CurrencyCost);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Faction Crier weekly reclaim was not exact.");
        }
    }

    private static string ClaimInsertSql(
        string table,
        string periodColumn,
        bool includeGoldCost = false) =>
        $"""
        INSERT INTO public.{table} (
            account_id,
            character_id,
            realm_id,
            {periodColumn},
            plate_item_id,
            {(includeGoldCost ? "gold_cost," : string.Empty)}
            balance_revision,
            item_content_revision,
            command_inbox_id,
            audit_id,
            outbox_event_id,
            wallet_revision,
            inventory_revision,
            progression_revision,
            faction_crier_revision
        )
        VALUES (
            @accountId,
            @characterId,
            @realmId,
            @period,
            @plateItemId,
            {(includeGoldCost ? "@goldCost," : string.Empty)}
            @balanceRevision,
            @itemContentRevision,
            @inboxId,
            @auditId,
            @eventId,
            @walletRevision,
            @inventoryRevision,
            @progressionRevision,
            @factionCrierRevision
        );
        """;

    private void AddSettlementIdentity(
        NpgsqlCommand command,
        CommandEnvelope<FactionCrierCommand> envelope,
        DerivedRevisions revisions,
        long inboxId,
        long auditId,
        Guid eventId)
    {
        command.Parameters.AddWithValue("accountId", envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue("realmId", envelope.Command.RealmId);
        command.Parameters.AddWithValue("balanceRevision", _balance.Revision);
        command.Parameters.AddWithValue(
            "itemContentRevision",
            _itemContentRevision);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue("eventId", eventId);
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
    }

    private static string CurrencyCode(FactionCrierCurrency currency) =>
        currency switch
        {
            FactionCrierCurrency.None => "none",
            FactionCrierCurrency.Silver => "silver",
            FactionCrierCurrency.BoundGold => "binding_gold",
            FactionCrierCurrency.Gold => "gold",
            _ => throw new ArgumentOutOfRangeException(nameof(currency))
        };
}
