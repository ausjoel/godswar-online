"""reborn_validators.py - the reborn server's own pet start-up validation queries, copied verbatim
from godswar-server (commit b85a1244) so gwadmin-web can run them before and after every write.

If any of these returns a pet id, the server REFUSES TO START (PostgresGameStore.EnsureSeedDataAsync,
State/PostgresGameStore.cs:233-238 calls them in this order and throws InvalidDataException).
Only changes: "LIMIT 1" removed so every offending pet is listed; parameters are substituted by
reborn.py with SQL literals (@name -> {name}).
"""

# PostgresGameStore.PetGrowthPolicy.cs:51-89
GROWTH_STATE = """
SELECT pet.id
FROM character_pets pet
INNER JOIN pet_content_aptitude_definitions aptitude
    ON aptitude.revision = {petContentRevision}
   AND aptitude.aptitude = pet.aptitude
INNER JOIN pet_content_aptitude_definitions weak_aptitude
    ON weak_aptitude.revision = {petContentRevision}
   AND weak_aptitude.aptitude = 1
LEFT JOIN character_pet_stat_values stat
    ON stat.pet_id = pet.id
GROUP BY
    pet.id,
    pet.growth_revealed,
    pet.growth_activation_policy_version,
    aptitude.minimum_total_growth,
    aptitude.maximum_total_growth,
    weak_aptitude.minimum_total_growth,
    weak_aptitude.maximum_total_growth
HAVING count(stat.stat_code) <> 6
    OR count(DISTINCT stat.stat_code) <> 6
    OR pet.growth_activation_policy_version IS DISTINCT FROM
        'weak-until-phoenix-v1'
    OR count(*) FILTER (
        WHERE stat.base_growth_rate <= 0
    ) > 0
    OR COALESCE(sum(stat.base_growth_rate), 0)
        < CASE
            WHEN pet.growth_revealed
                THEN aptitude.minimum_total_growth
            ELSE weak_aptitude.minimum_total_growth
          END
    OR COALESCE(sum(stat.base_growth_rate), 0)
        > CASE
            WHEN pet.growth_revealed
                THEN aptitude.maximum_total_growth
            ELSE weak_aptitude.maximum_total_growth
          END
ORDER BY pet.id
"""

# PostgresGameStore.PetInitialSavvyPolicy.cs:7-59
INITIAL_SAVVY_STATE = """
SELECT pet.id
FROM character_pets pet
INNER JOIN pet_content_aptitude_definitions aptitude
    ON aptitude.revision = {petContentRevision}
   AND aptitude.aptitude = pet.aptitude
LEFT JOIN character_pet_stat_values stat
    ON stat.pet_id = pet.id
GROUP BY
    pet.id,
    pet.initial_savvy_baseline_total,
    pet.initial_savvy_policy_version,
    aptitude.minimum_initial_savvy,
    aptitude.maximum_initial_savvy,
    aptitude.minimum_added_savvy,
    aptitude.maximum_added_savvy
HAVING count(stat.stat_code) <> 6
    OR count(DISTINCT stat.stat_code) <> 6
    OR (
        pet.initial_savvy_baseline_total IS NULL
        AND COALESCE(sum(stat.initial_savvy), 0) = 0
    )
    OR (
        pet.initial_savvy_baseline_total IS NOT NULL
        AND (
            (
                pet.initial_savvy_policy_version
                    IS DISTINCT FROM {initialPolicyVersion}
                AND pet.initial_savvy_policy_version
                    IS DISTINCT FROM {migratedLegacySavvyPolicyVersion}
            )
            OR (
                pet.initial_savvy_policy_version =
                    {initialPolicyVersion}
                AND pet.initial_savvy_baseline_total NOT BETWEEN
                    aptitude.minimum_initial_savvy AND
                    aptitude.maximum_initial_savvy
            )
            OR (
                pet.initial_savvy_policy_version =
                    {migratedLegacySavvyPolicyVersion}
                AND pet.initial_savvy_baseline_total NOT BETWEEN
                    aptitude.minimum_added_savvy AND
                    aptitude.maximum_added_savvy
            )
            OR count(*) FILTER (
                WHERE stat.initial_savvy <= 0
            ) > 0
            OR COALESCE(sum(stat.initial_savvy), 0)
                < pet.initial_savvy_baseline_total
        )
    )
ORDER BY pet.id
"""

# PostgresGameStore.PetAddedSavvyPolicy.cs:7-87
SAVVY_BASELINE_STATE = """
SELECT pet.id
FROM character_pets pet
INNER JOIN pet_content_aptitude_definitions aptitude
    ON aptitude.revision = {petContentRevision}
   AND aptitude.aptitude = pet.aptitude
LEFT JOIN character_pet_stat_values stat
    ON stat.pet_id = pet.id
WHERE pet.initial_savvy_baseline_total IS NOT NULL
   OR pet.initial_savvy_policy_version IS NOT NULL
   OR pet.rarity_added_savvy_baseline_total IS NOT NULL
   OR pet.rarity_added_savvy_policy_version IS NOT NULL
   OR pet.initial_savvy_source_version IS NOT NULL
GROUP BY
    pet.id,
    pet.level,
    pet.initial_savvy_baseline_total,
    pet.initial_savvy_policy_version,
    pet.rarity_added_savvy_baseline_total,
    pet.rarity_added_savvy_policy_version,
    pet.initial_savvy_source_version,
    aptitude.minimum_initial_savvy,
    aptitude.maximum_initial_savvy,
    aptitude.minimum_added_savvy,
    aptitude.maximum_added_savvy
HAVING count(stat.stat_code) <> 6
    OR count(DISTINCT stat.stat_code) <> 6
    OR pet.initial_savvy_baseline_total IS NULL
    OR (
        pet.initial_savvy_policy_version
            IS DISTINCT FROM {initialPolicyVersion}
        AND pet.initial_savvy_policy_version
            IS DISTINCT FROM {migratedLegacySavvyPolicyVersion}
    )
    OR pet.rarity_added_savvy_baseline_total IS NULL
    OR (
        pet.rarity_added_savvy_policy_version
            IS DISTINCT FROM {initialPolicyVersion}
        AND pet.rarity_added_savvy_policy_version
            IS DISTINCT FROM {migratedLegacySavvyPolicyVersion}
    )
    OR pet.initial_savvy_policy_version
        IS DISTINCT FROM pet.rarity_added_savvy_policy_version
    OR pet.initial_savvy_source_version
        IS DISTINCT FROM {initialSourceVersion}
    OR pet.initial_savvy_baseline_total
        IS DISTINCT FROM pet.rarity_added_savvy_baseline_total
    OR (
        pet.initial_savvy_policy_version = {initialPolicyVersion}
        AND pet.rarity_added_savvy_baseline_total NOT BETWEEN
            aptitude.minimum_initial_savvy AND
            aptitude.maximum_initial_savvy
    )
    OR (
        pet.initial_savvy_policy_version =
            {migratedLegacySavvyPolicyVersion}
        AND pet.rarity_added_savvy_baseline_total NOT BETWEEN
            aptitude.minimum_added_savvy AND
            aptitude.maximum_added_savvy
    )
    OR count(*) FILTER (
        WHERE stat.birth_initial_savvy IS NULL
           OR stat.rarity_added_savvy IS NULL
           OR stat.base_growth_rate <= 0
           OR stat.growth_acceleration < 0
           OR stat.birth_initial_savvy
                IS DISTINCT FROM stat.rarity_added_savvy
           OR stat.initial_savvy <= 0
           OR stat.added_savvy IS DISTINCT FROM
                (
                    stat.base_growth_rate +
                    stat.growth_acceleration
                ) * pet.level
    ) > 0
    OR COALESCE(sum(stat.rarity_added_savvy), 0)
        <> pet.rarity_added_savvy_baseline_total
    OR COALESCE(sum(stat.birth_initial_savvy), 0)
        <> pet.initial_savvy_baseline_total
    OR COALESCE(sum(stat.initial_savvy), 0)
        < pet.initial_savvy_baseline_total
ORDER BY pet.id
"""
