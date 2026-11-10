using System.Text.Json;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class FactionCrierDatabaseFoundationChecks
{
    public const string CheckName =
        "Database-authoritative Faction Crier foundation";

    public static Task RunAsync()
    {
        var migration =
            PostgresSchemaMigrationCatalog.CreateFactionCrierFoundation();
        Check.Equal(
            "20260821_100_faction_crier_foundation",
            migration.Id,
            "Faction Crier owns forward migration 100");
        Check.Equal(
            "D6112B96E93655E0B7850F5968E2F1F5B733C7E84086C1A372540370E6272A54",
            migration.Checksum,
            "Faction Crier migration 100 remains forward-only and pinned");
        foreach (var fragment in RequiredMigrationFragments)
        {
            Check.True(
                migration.Sql.Contains(fragment, StringComparison.Ordinal),
                $"Faction Crier migration owns {fragment}");
        }

        var reviewed = FactionCrierRewardPolicy.CreateReviewedDefault();
        reviewed.Validate();
        Check.True(
            reviewed.MinimumLevel == 20 &&
            reviewed.WeeklyReclaimGoldCost == 230 &&
            reviewed.RenewalGoldCost == 105 &&
            reviewed.Tiers.Count == 7 &&
            reviewed.Tiers[^1] ==
                new FactionCrierBalanceTier(
                    131, 200, 250_000, 38, 140_000, 400_000) &&
            reviewed.Options.Count == 25 &&
            reviewed.Options.Any(static value =>
                value.Currency == FactionCrierCurrency.BoundGold &&
                value.Cost == 1_377 &&
                value.Multiplier == 24),
            "reviewed balance covers levels 20..200 and exact premium offers");

        var calendarMigration = PostgresSchemaMigrationCatalog.All.Single(
            value => value.Id ==
                "20260821_102_faction_crier_nzst_calendar");
        Check.Equal(
            "27363E6169172F8A52C19E8D6E010ACDAAA043CD8F50F01F1AED0E086C0F1174",
            calendarMigration.Checksum,
            "Faction Crier calendar migration remains forward-only and pinned");
        Check.True(
            calendarMigration.Sql.Contains(
                "server_utc_offset_minutes = -480",
                StringComparison.Ordinal) &&
            calendarMigration.Sql.Contains(
                "SELECT 1,",
                StringComparison.Ordinal) &&
            calendarMigration.Sql.Contains(
                "720,",
                StringComparison.Ordinal) &&
            calendarMigration.Sql.Contains(
                "WHERE setting_id = 1",
                StringComparison.Ordinal) &&
            calendarMigration.Sql.Contains(
                "AND revision = 0",
                StringComparison.Ordinal) &&
            calendarMigration.Sql.Contains(
                "published_count <> 1",
                StringComparison.Ordinal),
            "migration 102 publishes one immutable UTC+12 successor");

        var seeds = FactionCrierNameplateItemContentBaseline.ItemTemplates;
        Check.Equal(6, seeds.Count, "reviewed Nameplate template count");
        for (var ordinal = 1; ordinal <= seeds.Count; ordinal++)
        {
            var seed = seeds[ordinal - 1];
            using var stats = JsonDocument.Parse(seed.StatsJson);
            var root = stats.RootElement;
            Check.True(
                seed.Id == 3819 + ordinal &&
                seed.NameKey == $"Nameplate{ordinal}" &&
                seed.DisplayName == $"Nameplate {ordinal}" &&
                seed.Kind == "consume item" &&
                seed.EquipmentSlot == 0 &&
                seed.ClassIds.Length == 0 &&
                seed.Texture ==
                    "./Localization/en_us/UI/Texture/Icon.gwo" &&
                seed.Icon == ExpectedIcons[ordinal - 1] &&
                root.GetProperty("Distribution").GetString() == "150,200" &&
                root.GetProperty("Overlap").GetString() == "99" &&
                root.GetProperty("BindType").GetString() == "1" &&
                root.EnumerateObject().Count() == 9,
                $"Nameplate {ordinal} retains exact stock metadata");
        }

        return Task.CompletedTask;
    }

    private static readonly string[] ExpectedIcons =
    [
        "216,936", "252,936", "288,936",
        "324,936", "360,936", "396,936"
    ];

    private static readonly string[] RequiredMigrationFragments =
    [
        "ADD COLUMN \"BindingGold\" integer NOT NULL DEFAULT 0",
        "ADD COLUMN faction_crier_revision bigint NOT NULL DEFAULT 0",
        "currency_code IN ('silver', 'gold', 'binding_gold')",
        "ALTER COLUMN source TYPE varchar(128)",
        "CREATE TABLE public.faction_crier_balance_revisions",
        "CREATE TABLE public.faction_crier_balance_tiers",
        "CHECK (minimum_level = 20)",
        "ck_faction_crier_balance_tier_stock_ranges",
        "CREATE TABLE public.faction_crier_balance_options",
        "ck_faction_crier_balance_option_stock_shape",
        "tier.base_experience::bigint *",
        "tier.base_talent_points::bigint *",
        "CREATE TABLE public.faction_crier_balance_settings",
        "reject_sealed_faction_crier_balance_insert",
        "(0, 131, 200, 250000, 38, 140000, 400000)",
        "(0, 133, 'binding_gold', 1377, 24",
        "CREATE TABLE public.faction_crier_daily_claims",
        "UNIQUE (character_id, realm_id, claim_day)",
        "CREATE TABLE public.faction_crier_weekly_reclaims",
        "UNIQUE (character_id, realm_id, week_start)",
        "CONSTRAINT ck_faction_crier_weekly_values CHECK (",
        "CREATE TABLE public.faction_crier_exchange_settlements",
        "FOREIGN KEY (character_id, account_id)",
        "sub_id BETWEEN 101 AND 106",
        "ck_faction_crier_exchange_stock_items",
        "granted_item_id = 3779 + sub_id",
        "ARRAY[3820,3822,3824]",
        "faction_crier_revision bigint NOT NULL",
        "(3820, 'Nameplate1', 'Nameplate 1', '216,936')",
        "'Overlap', '99'",
        "'BindType', '1'"
    ];
}
