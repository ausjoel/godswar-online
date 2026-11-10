using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckMonsterKillProgressionAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-progression-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync("progression-check", "");
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "ProgressionHero",
                    Camp = GameDefaults.SpartaCamp,
                    TalentPoints = 10,
                    TalentExperience = 0
                });

            var first = await store.ApplyMonsterKillRewardAsync(
                account.Id,
                character.Id,
                experience: 80,
                talentExperience: 2)
                ?? throw new InvalidOperationException("first progression update returned no character");
            Check.Equal(80, first.CurrentExperience, "first kill persists fighter EXP");
            Check.Equal(1, first.CurrentLevel, "first kill remains below the level-one threshold");
            Check.Equal(2, first.CurrentTalentExperience, "first kill persists Talent EXP");
            Check.Equal(0, first.TalentPointsGained, "first kill does not prematurely create a Talent Point");
            Check.Equal(10, first.CurrentTalentPoints, "first kill retains spendable Talent Points");

            var carry = await store.ApplyMonsterKillRewardAsync(
                account.Id,
                character.Id,
                experience: 160,
                talentExperience: 99)
                ?? throw new InvalidOperationException("carry progression update returned no character");
            Check.Equal(2, carry.CurrentLevel, "fighter EXP advances a level at the original threshold");
            Check.Equal(40, carry.CurrentExperience, "fighter EXP carries its remainder into the next level");
            Check.Equal(252, carry.NextLevelExperience, "level two uses the original next-level threshold");
            Check.Equal(1, carry.LevelUps.Count, "progression reports every crossed level");
            Check.Equal(40, carry.LevelUps[0].CurrentExperience, "level-up packet receives carried fighter EXP");
            Check.Equal(1, carry.CurrentTalentExperience, "Talent EXP carries its remainder at 100");
            Check.Equal(1, carry.TalentPointsGained, "Talent EXP carry creates one Talent Point");
            Check.Equal(11, carry.CurrentTalentPoints, "spendable Talent Point total increments");

            var reloaded = await store.GetFirstCharacterAsync(account.Id)
                ?? throw new InvalidOperationException("progression character was not reloaded");
            Check.Equal(2, reloaded.Level, "fighter level survives relogin");
            Check.Equal(40, reloaded.Experience, "carried fighter EXP survives relogin");
            Check.Equal(1, reloaded.TalentExperience, "Talent EXP remainder survives relogin");
            Check.Equal(11, reloaded.TalentPoints, "converted Talent Point survives relogin");

            Check.Equal(200, PlayerExperienceCatalog.GetNextLevelExperience(1), "level-one EXP threshold");
            Check.Equal(252, PlayerExperienceCatalog.GetNextLevelExperience(2), "level-two EXP threshold");
            Check.Equal(584435250, PlayerExperienceCatalog.GetNextLevelExperience(200), "level-cap EXP table entry");
            Check.Equal(80, MonsterRewardCatalog.Resolve(1, 1).Experience, "captured tier-one reward");
            Check.Equal(120, MonsterRewardCatalog.Resolve(11, 1).Experience, "tier-eleven reward follows original curve");
            Check.Equal(
                10,
                MonsterRewardCatalog.NormalTalentExperience,
                "captured normal monster Talent EXP baseline");
            var defaultRewardPolicy =
                MonsterRewardPolicySnapshot.Default;
            var tier120BaseReward = MonsterRewardCatalog.Resolve(
                monsterTier: 120,
                playerLevel: 120,
                defaultRewardPolicy);
            var inclusiveGapReward = MonsterRewardCatalog.Resolve(
                monsterTier: 120,
                playerLevel: 140,
                defaultRewardPolicy);
            Check.True(
                tier120BaseReward.Experience > 0,
                "eligible tier has a positive fighter EXP baseline");
            Check.Equal(
                tier120BaseReward.Experience,
                inclusiveGapReward.Experience,
                "configured lower-level gap is inclusive at twenty levels");
            Check.Equal(
                MonsterRewardCatalog.NormalTalentExperience,
                inclusiveGapReward.TalentExperience,
                "inclusive lower-level gap retains Talent EXP");
            Check.Equal(
                tier120BaseReward.Experience,
                MonsterRewardCatalog.ResolvePetExperience(
                    monsterTier: 120,
                    playerLevel: 140,
                    defaultRewardPolicy),
                "inclusive lower-level gap retains Pet EXP");

            var beyondDefaultGap = MonsterRewardCatalog.Resolve(
                monsterTier: 119,
                playerLevel: 140,
                defaultRewardPolicy);
            Check.Equal(
                0,
                beyondDefaultGap.Experience,
                "twenty-one-level gap awards no fighter EXP");
            Check.Equal(
                0,
                beyondDefaultGap.TalentExperience,
                "twenty-one-level gap awards no Talent EXP");
            Check.Equal(
                0,
                MonsterRewardCatalog.ResolvePetExperience(
                    monsterTier: 119,
                    playerLevel: 140,
                    defaultRewardPolicy),
                "twenty-one-level gap awards no Pet EXP");

            var narrowRewardPolicy = new MonsterRewardPolicySnapshot(
                MaximumLowerLevelGap: 7,
                GlobalExperienceMultiplierBasisPoints: 10_000,
                Revision: 1,
                UpdatedAtUtc: DateTimeOffset.UnixEpoch,
                UpdatedBy: "progression-check");
            narrowRewardPolicy.Validate();
            var tier133BaseReward = MonsterRewardCatalog.Resolve(
                monsterTier: 133,
                playerLevel: 133,
                narrowRewardPolicy);
            Check.Equal(
                tier133BaseReward.Experience,
                MonsterRewardCatalog.Resolve(
                    monsterTier: 133,
                    playerLevel: 140,
                    narrowRewardPolicy).Experience,
                "non-default lower-level gap remains inclusive");
            Check.Equal(
                0,
                MonsterRewardCatalog.Resolve(
                    monsterTier: 132,
                    playerLevel: 140,
                    narrowRewardPolicy).Experience,
                "non-default lower-level gap rejects its next level");
            Check.Equal(
                0,
                MonsterRewardCatalog.ResolvePetExperience(
                    monsterTier: 132,
                    playerLevel: 140,
                    narrowRewardPolicy),
                "non-default lower-level gap also gates Pet EXP");
            Check.Equal(0, MonsterRewardCatalog.Resolve(200, 200).TalentExperience, "level-cap kills award no progression");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckOnlineProgressionBoostDurationAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-online-boost-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            int accountId;
            int characterId;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync("online-boost-check", "");
                var character = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "OnlineBoostHero",
                        Camp = GameDefaults.SpartaCamp
                    });
                accountId = account.Id;
                characterId = character.Id;
            }

            var grantedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var statePath = Path.Combine(dataPath, "state.json");
            var legacyState = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                ?? throw new InvalidOperationException("JSON online-boost test could not parse state.json");
            legacyState["characterExperienceBoosts"] = new JsonArray
            {
                new JsonObject
                {
                    ["characterId"] = characterId,
                    ["statusId"] = ExperienceStatusIds.GuildDoubleExperience16Hours,
                    ["kind"] = ExperienceBoostKinds.Guild,
                    ["bonusBasisPoints"] = 10_000,
                    ["priority"] = 1,
                    ["activatedAt"] = grantedAt,
                    ["expiresAt"] = grantedAt.AddHours(16),
                    ["source"] = "legacy-exp"
                },
                new JsonObject
                {
                    ["characterId"] = characterId,
                    ["statusId"] = ExperienceStatusIds.HighTalentBoost100Percent,
                    ["kind"] = ExperienceBoostKinds.Talent,
                    ["bonusBasisPoints"] = 10_000,
                    ["priority"] = 4,
                    ["activatedAt"] = grantedAt,
                    ["expiresAt"] = grantedAt.AddHours(8),
                    ["source"] = "legacy-talent"
                },
                new JsonObject
                {
                    ["characterId"] = characterId,
                    ["statusId"] = ExperienceStatusIds.Weekend,
                    ["kind"] = ExperienceBoostKinds.Weekend,
                    ["bonusBasisPoints"] = 20_000,
                    ["priority"] = 1,
                    ["activatedAt"] = grantedAt,
                    ["expiresAt"] = grantedAt.AddHours(8),
                    ["source"] = "personal-weekend-grant"
                }
            };
            var accountNode = legacyState["accounts"]?.AsArray().Single()?.AsObject()
                ?? throw new InvalidOperationException("JSON online-boost account is missing");
            accountNode["donatorTier"] = (short)DonatorTier.OctagramPatron;
            accountNode["donatorExpiresAt"] = grantedAt.AddDays(1);
            await File.WriteAllTextAsync(statePath, legacyState.ToJsonString(JsonDefaults.Indented));

            var firstOnlineAt = grantedAt.AddDays(3);
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var restored = await store.GetExperienceBoostStateAsync(
                    accountId,
                    characterId,
                    GameDefaults.SpartaCamp,
                    mapId: 0,
                    firstOnlineAt);
                Check.Equal(3, restored.ActiveBoosts.Count, "legacy personal boosts restore after wall-clock expiry");
                Check.Equal(30_000, restored.TotalBonusBasisPoints, "personal EXP grants remain active after offline gap");
                Check.Equal(10_000, restored.TotalTalentBonusBasisPoints, "personal Talent grant remains active after offline gap");
                Check.True(
                    restored.ActiveBoosts.All(boost =>
                        boost.Kind != ExperienceBoostKinds.Donator),
                    "expired donator membership remains calendar-based");
                Check.Equal(
                    57_600u,
                    restored.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Guild)
                        .RemainingSeconds(firstOnlineAt),
                    "legacy sixteen-hour EXP grant restores its original duration");
                Check.Equal(
                    28_800u,
                    restored.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Talent)
                        .RemainingSeconds(firstOnlineAt),
                    "legacy eight-hour Talent grant restores its original duration");

            }

            // Reopening the provider and advancing wall time by a week models
            // logout plus server restart: no offline duration is consumed.
            var secondOnlineAt = firstOnlineAt.AddDays(7);
            await using (var restartedStore = new JsonGameStore(dataPath))
            {
                await restartedStore.EnsureSeedDataAsync();
                var resumed = await restartedStore.GetExperienceBoostStateAsync(
                    accountId,
                    characterId,
                    GameDefaults.SpartaCamp,
                    mapId: 0,
                    secondOnlineAt);
                Check.Equal(
                    57_600u,
                    resumed.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Guild)
                        .RemainingSeconds(secondOnlineAt),
                    "EXP duration pauses through logout and restart");
                Check.Equal(
                    28_800u,
                    resumed.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Talent)
                        .RemainingSeconds(secondOnlineAt),
                    "Talent duration pauses through logout and restart");
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
