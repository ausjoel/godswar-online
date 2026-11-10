using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const int PriorCooledReductionGradeOneMaximum = 55;
    private const int SevenPercentCooledReductionGradeOneMaximum = 70;
    private const int RetainedCooledCriticalGradeOneMaximum = 60;

    internal static PostgresSchemaMigration
        CreateCooledHolyStoneSevenPercentReduction() => new(
        "20260821_104_cooled_holy_stone_reduction_7_percent",
        "Raise Cooled physical and magic reduction caps to seven percent",
        $$"""
        DO $cooled_holy_stone_reduction_7_percent$
        DECLARE
            current_physical smallint;
            current_magic smallint;
            current_critical smallint;
            updated_settings integer;
        BEGIN
            SELECT
                cooled_physical_reduction_grade_one_maximum,
                cooled_magic_reduction_grade_one_maximum,
                cooled_critical_reduction_grade_one_maximum
            INTO
                current_physical,
                current_magic,
                current_critical
            FROM public.holy_spirit_balance_settings
            WHERE setting_id = 1
            FOR UPDATE;

            IF NOT FOUND OR
               current_physical <>
                   {{PriorCooledReductionGradeOneMaximum}} OR
               current_magic <>
                   {{PriorCooledReductionGradeOneMaximum}} OR
               current_critical <>
                   {{RetainedCooledCriticalGradeOneMaximum}} THEN
                RAISE EXCEPTION
                    'Cooled balance migration 104 expected 55/55/60; found %/%/%',
                    current_physical,
                    current_magic,
                    current_critical
                    USING ERRCODE = 'check_violation';
            END IF;

            UPDATE public.character_items
            SET holy_socket1_value = CASE
                    WHEN holy_socket1_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket1_level BETWEEN 1 AND 10
                         AND holy_socket1_value = holy_socket1_level *
                             {{PriorCooledReductionGradeOneMaximum}}
                        THEN holy_socket1_level *
                             {{SevenPercentCooledReductionGradeOneMaximum}}
                    ELSE holy_socket1_value
                END,
                holy_socket2_value = CASE
                    WHEN holy_socket2_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket2_level BETWEEN 1 AND 10
                         AND holy_socket2_value = holy_socket2_level *
                             {{PriorCooledReductionGradeOneMaximum}}
                        THEN holy_socket2_level *
                             {{SevenPercentCooledReductionGradeOneMaximum}}
                    ELSE holy_socket2_value
                END,
                holy_socket3_value = CASE
                    WHEN holy_socket3_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket3_level BETWEEN 1 AND 10
                         AND holy_socket3_value = holy_socket3_level *
                             {{PriorCooledReductionGradeOneMaximum}}
                        THEN holy_socket3_level *
                             {{SevenPercentCooledReductionGradeOneMaximum}}
                    ELSE holy_socket3_value
                END,
                holy_socket4_value = CASE
                    WHEN holy_socket4_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket4_level BETWEEN 1 AND 10
                         AND holy_socket4_value = holy_socket4_level *
                             {{PriorCooledReductionGradeOneMaximum}}
                        THEN holy_socket4_level *
                             {{SevenPercentCooledReductionGradeOneMaximum}}
                    ELSE holy_socket4_value
                END
            WHERE holy_socket1_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket1_level BETWEEN 1 AND 10
                  AND holy_socket1_value = holy_socket1_level *
                      {{PriorCooledReductionGradeOneMaximum}}
               OR holy_socket2_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket2_level BETWEEN 1 AND 10
                  AND holy_socket2_value = holy_socket2_level *
                      {{PriorCooledReductionGradeOneMaximum}}
               OR holy_socket3_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket3_level BETWEEN 1 AND 10
                  AND holy_socket3_value = holy_socket3_level *
                      {{PriorCooledReductionGradeOneMaximum}}
               OR holy_socket4_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket4_level BETWEEN 1 AND 10
                  AND holy_socket4_value = holy_socket4_level *
                      {{PriorCooledReductionGradeOneMaximum}};

            UPDATE public.holy_spirit_balance_settings
            SET cooled_physical_reduction_grade_one_maximum =
                    {{SevenPercentCooledReductionGradeOneMaximum}},
                cooled_magic_reduction_grade_one_maximum =
                    {{SevenPercentCooledReductionGradeOneMaximum}},
                updated_by = 'migration-104'
            WHERE setting_id = 1
              AND cooled_physical_reduction_grade_one_maximum =
                  {{PriorCooledReductionGradeOneMaximum}}
              AND cooled_magic_reduction_grade_one_maximum =
                  {{PriorCooledReductionGradeOneMaximum}}
              AND cooled_critical_reduction_grade_one_maximum =
                  {{RetainedCooledCriticalGradeOneMaximum}};

            GET DIAGNOSTICS updated_settings = ROW_COUNT;
            IF updated_settings <> 1 THEN
                RAISE EXCEPTION
                    'Cooled balance migration 104 lost its settings guard'
                    USING ERRCODE = 'serialization_failure';
            END IF;
        END;
        $cooled_holy_stone_reduction_7_percent$;
        """);
}
