using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisPartyAdmissionAsync()
    {
        var destination = ResolveAtlantisOpalDestination();
        foreach (var partySize in Enumerable.Range(1, 5))
        {
            await using var fixture = await CreateAtlantisOpalFixtureAsync(
                dailyEntries: null,
                opalPayments: null,
                partySize: partySize);
            var leader = fixture.Leader;
            foreach (var level in new[] { 90, 140, 141, 160 })
            {
                foreach (var character in fixture.Characters)
                {
                    character.Level = level;
                }

                foreach (var paymentMode in new[]
                         {
                             InstanceCallerEntryPaymentMode.FreeOnly,
                             InstanceCallerEntryPaymentMode.OpalRetry
                         })
                {
                    var status = leader.Registry.TryCaptureLegacyInstanceParty(
                        leader.Session,
                        destination with { PaymentMode = paymentMode },
                        leader.Character.PositionX,
                        leader.Character.PositionZ,
                        InstanceCallerProtocol.MaximumInteractionDistance,
                        out var party);
                    Check.True(
                        status == LegacyInstanceEntryStatus.Ready &&
                        party.Members.Count == partySize &&
                        party.LeaderCharacterId == leader.Character.Id &&
                        party.Members.Select(member => member.CharacterId)
                            .ToHashSet()
                            .SetEquals(fixture.Characters.Select(
                                character => character.Id)),
                        $"Atlantis {paymentMode} admits {partySize} " +
                        $"member(s) at level {level} with no upper admission cap");
                }
            }

            var memberToValidate = fixture.Characters[^1];
            memberToValidate.Level = 89;
            foreach (var paymentMode in new[]
                     {
                         InstanceCallerEntryPaymentMode.FreeOnly,
                         InstanceCallerEntryPaymentMode.OpalRetry
                     })
            {
                Check.True(
                    leader.Registry.TryCaptureLegacyInstanceParty(
                        leader.Session,
                        destination with { PaymentMode = paymentMode },
                        leader.Character.PositionX,
                        leader.Character.PositionZ,
                        InstanceCallerProtocol.MaximumInteractionDistance,
                        out _) == LegacyInstanceEntryStatus.LevelRequirementNotMet,
                    $"Atlantis {paymentMode} rejects a level 89 member in a " +
                    $"{partySize}-member party");
            }
            memberToValidate.Level = 90;

            if (fixture.Followers.Count > 0)
            {
                Check.True(
                    leader.Registry.TryCaptureLegacyInstanceParty(
                        fixture.Followers[0].Session,
                        destination,
                        leader.Character.PositionX,
                        leader.Character.PositionZ,
                        InstanceCallerProtocol.MaximumInteractionDistance,
                        out _) == LegacyInstanceEntryStatus.LeaderRequired,
                    "Atlantis still requires the party leader to enter");
            }
        }

        await CheckAtlantisHighLevelLaunchAsync();
    }

    private static async Task CheckAtlantisHighLevelLaunchAsync()
    {
        foreach (var partySize in new[] { 1, 5 })
        {
            foreach (var level in new[] { 141, 160 })
            {
                var daily = new ScriptedLegacyInstanceDailyEntryStore();
                var payments = new ScriptedLegacyInstanceOpalPaymentStore();
                await using var fixture = await CreateAtlantisOpalFixtureAsync(
                    daily, payments, partySize: partySize);
                foreach (var character in fixture.Characters)
                {
                    character.Level = level;
                }
                var leader = fixture.Leader;
                var source = GetSourceInstanceId(leader);

                await EnterAtlantisAsync(fixture, InstanceCallerProtocol.AtlantisEnterSubId);
                var instanceId = GetSourceInstanceId(leader);
                Check.True(instanceId != source && AllSessionsShareCurrentInstance(fixture) &&
                    fixture.Characters.All(static character => character.CurrentMap == 205) &&
                    daily.Claims.Single().CharacterIds.Count == partySize &&
                    payments.Charges.Count == 0,
                    $"actual Atlantis entry admits {partySize} level {level} player(s) under the free-entry policy");
                Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(instanceId, out var run) &&
                    run.State == AtlantisRunState.Active && run.TeamPoints == 0 &&
                    run.Deadline - run.StartedAt == TimeSpan.FromMinutes(40),
                    "high-level entry starts its exact Atlantis score clock");

                await CompleteAtlantisSceneReadinessAsync(leader.Handler);
                foreach (var follower in fixture.Followers)
                {
                    await CompleteAtlantisSceneReadinessAsync(follower.Handler);
                }
                await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt, CancellationToken.None);
                var monsters = leader.Registry.GetMapMonsterSnapshots(leader.Session, 205)
                    .Where(monster => IsAtlantisScoringObject(monster.ObjectId)).ToArray();
                Check.True(monsters.Length == 12 && monsters.All(monster =>
                        monster.IsAlive && monster.IsSpawned && monster.SpawnGeneration == 1 &&
                        monster.RespawnAt is null && monster.MaximumHealth > 0 &&
                        monster.Definition.Tier == checked((uint)level)),
                    $"{partySize} level {level} player(s) start the real twelve-monster first wave");
            }
        }
    }
}
