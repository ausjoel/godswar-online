// ROUND3: native mount payment, equipment admission and exact lifetime fencing in both runtimes.
using System.Reflection;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string OperatorMountCheckName = "Operator mounts require native equipment and payment with exact expiry ownership";

    public static async Task RunOperatorMountsAsync()
    {
        var content = TestItemContent.Content;
        var mapped = content.DeveloperMounts.Grantable
            .Select(row => content.Mounts.TryGetRideDefinition(row.ItemId, out var ride) ? ride.StatusId : 0u).ToHashSet();
        var missing = GenericStatusCatalog.All.Where(row => row.RideId is not null &&
                GenericStatusCatalog.UnsupportedReason(row) is null && !mapped.Contains((uint)row.StatusId))
            .Select(row => row.StatusId).Order().ToArray();
        Check.True(missing.Length == 0, "mount mapping gaps: " + string.Join(",", missing));
        foreach (var row in GenericStatusCatalog.All.Where(row => row.RideId is not null))
        {
            if (row.StatusId == 1200)
            {
                Check.True(!GenericStatusCatalog.TryResolve(row.StatusId, out _, out _), "unmapped Leatherback remains refused");
                continue;
            }
            Check.True(GenericStatusCatalog.TryResolve(row.StatusId, out var definition, out _) &&
                definition.RequiresEquippedMount && definition.Duration == TimeSpan.FromSeconds(600),
                $"audited mount {row.StatusId} has a bounded native activation");
            Check.True(mapped.Contains((uint)row.StatusId), $"advertised mount {row.StatusId} has an equipable native item");
        }
        Check.True(GenericStatusCatalog.TryResolve(1100, out var mountEffect, out _), "Greek mount resolves");
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var socket = await RuntimePolicySessionSocket.CreateAsync();
            var character = Player(2500, GameDefaults.SpartaCamp);
            character.CurrentMp = 200;
            var store = new PvpVitalsRecordingStore();
            var registry = new GameSessionRegistry(store: store, zodiacEnergyOptions: null,
                monsterRuntimeMode: MonsterRuntimeMode.Ecs, itemContent: content,
                playerRuntimeMode: mode, gameplayCatalogs: GameplayContentTestFixtures.Runtime);
            Join(registry, socket, character);
            var handler = new GameClientHandler(socket.Session, store, registry,
                CharacterSnapshotReaderTestFixtures.Unused, WorldContentReaderTestFixtures.Empty, itemContent: content);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(GameClientHandler).GetField("_character", flags)!.SetValue(handler, character);
            typeof(GameClientHandler).GetField("_account", flags)!.SetValue(handler, new AccountIdentity(character.AccountId, "mount-check"));
            async Task<JsonObject> Apply(SkillStatusEffectDefinition definition) => JsonNode.Parse(await (Task<string>)
                typeof(GameClientHandler).GetMethod("ApplyOperatorBuffForAdminAsync", flags)!
                    .Invoke(handler, [definition, CancellationToken.None])!)!.AsObject();
            async Task Refused(string reason)
            {
                var before = character.CurrentMp;
                var saves = store.Saves.Count;
                Check.True(!(await Apply(mountEffect))["ok"]!.GetValue<bool>(), reason);
                Check.Equal(before, character.CurrentMp, "refusal does not spend MP");
                Check.Equal(saves, store.Saves.Count, "refusal does not persist a payment");
            }
            try
            {
                await Refused("missing mount is refused");
                character.Equipment = EquipmentSlots.SetSlot(character.Equipment, character.Profession,
                    EquipmentSlots.Mount, "[14221,,,,,,1,1,0,1,0]");
                await Refused("a different equipped model is refused");
                character.Equipment = EquipmentSlots.SetSlot(character.Equipment, character.Profession,
                    EquipmentSlots.Mount, "[14220,,,,,,1,1,0,1,0]");
                var equipment = character.Equipment;
                character.Level = 1;
                await Refused("mount level requirement is enforced");
                character.Level = 120;
                character.CurrentHp = 0;
                await Refused("dead character cannot mount");
                character.CurrentHp = character.MaxHp;
                character.CurrentMp = 49;
                await Refused("insufficient MP is refused");
                character.CurrentMp = 200;
                Check.True(!await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, mountEffect,
                    DateTimeOffset.UtcNow, "forbidden-generic-injection", CancellationToken.None),
                    "generic status application cannot bypass native mount admission");
                Check.True(GenericStatusCatalog.TryResolve(330, out var stun, out _), "stun resolves");
                await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, stun with { OperatorApplied = true },
                    DateTimeOffset.UtcNow, "mount-control-check", CancellationToken.None);
                await Refused("casting restriction blocks administrative mounting");
                var stunToken = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session,
                    DateTimeOffset.UtcNow, CancellationToken.None), stun.StatusId);
                await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, stunToken, DateTimeOffset.UtcNow, CancellationToken.None);
                var life = registry.GetPlayerLifeRevision(socket.Session);
                Check.True(content.Mounts.TryGetEquippedRideDefinition(character, out var mount), "native mount resolves");
                foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds((double)uint.MaxValue + 1) })
                    Check.True(await registry.TryActivateMountRideAndPublishAsync(socket.Session, character.Id, life, mount,
                        DateTimeOffset.UtcNow, CancellationToken.None, duration) is null, "invalid lifetime refused before payment");
                Check.Equal(200, character.CurrentMp, "malformed lifetimes cost no MP");
                var result = await Apply(mountEffect with { Duration = TimeSpan.FromSeconds(1) });
                Check.True(result["applied"]!.GetValue<bool>(), "eligible operator mount activates immediately");
                Check.Equal(150, character.CurrentMp, "mount charges exactly 50 MP");
                Check.Equal(150, store.Saves.Last().CurrentMp, "native payment is durable before success");
                Check.Equal(equipment, character.Equipment, "mount activation grants or changes no equipment");
                Check.True(registry.GetRuntimeStatusAggregate(socket.Session, DateTimeOffset.UtcNow).IsRiding,
                    "operator mount sets native riding appearance");
                Check.Equal(1.2f, registry.GetRuntimeStatusAggregate(socket.Session, DateTimeOffset.UtcNow).MovementSpeedMultiplier,
                    "speed comes from equipped mount quality");
                await Refused("duplicate activation cannot toggle or charge twice");
                var token = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session,
                    DateTimeOffset.UtcNow, CancellationToken.None), mountEffect.StatusId);
                Check.True((await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token,
                    DateTimeOffset.UtcNow, CancellationToken.None))["removed"]!.GetValue<bool>(), "exact operator token dismounts");
                Check.True(!registry.GetRuntimeStatusAggregate(socket.Session, DateTimeOffset.UtcNow).IsRiding,
                    "exact removal clears model and speed");
                await Refused("native ride cooldown survives removal");
                Check.True(await registry.TryActivateMountRideAndPublishAsync(socket.Session, character.Id, life, mount,
                    DateTimeOffset.UtcNow, CancellationToken.None) is not null, "later native ride retains its persistent lifetime");
                await Task.Delay(1200);
                Check.True(registry.GetRuntimeStatusAggregate(socket.Session, DateTimeOffset.UtcNow).IsRiding,
                    "old finite timer cannot remove a later native ride");
                Check.True(!(await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, DateTimeOffset.UtcNow,
                    CancellationToken.None))["ok"]!.GetValue<bool>(), "stale token cannot remove later native ride");
                var nativeToken = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session,
                    DateTimeOffset.UtcNow, CancellationToken.None), mountEffect.StatusId);
                Check.True(!(await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, nativeToken, DateTimeOffset.UtcNow,
                    CancellationToken.None))["ok"]!.GetValue<bool>(), "fresh token cannot steal native ride ownership");
                Check.True(await GameClientHandler.DismountMountRideAndPublishAsync(socket.Session, registry, character, life,
                    CancellationToken.None), "normal native dismount remains available");
                Check.True(await registry.TryActivateMountRideAndPublishAsync(socket.Session, character.Id, life, mount,
                    DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(1)) is not null, "second finite ride starts");
                await Task.Delay(1200);
                Check.True(!registry.GetRuntimeStatusAggregate(socket.Session, DateTimeOffset.UtcNow).IsRiding,
                    "finite ride expiry clears riding in both runtimes");
                Func<CancellationToken, Task> noop = _ => Task.CompletedTask;
                Check.True(await (Task<bool>)typeof(GameClientHandler).GetMethod("TryBeginPendingSkillCastAsync", flags)!
                    .Invoke(handler, [(uint)MountCatalog.RideSkillId, TimeSpan.FromSeconds(30), "mount-test", noop, noop,
                        CancellationToken.None, null])!, "unrelated pending cast starts");
                await Refused("pending cast is preserved rather than overwritten by admin activation");
            }
            finally
            {
                await (Task)typeof(GameClientHandler).GetMethod("StopPendingSkillCastsAsync", flags)!.Invoke(handler, [])!;
                registry.Remove(socket.Session);
            }
        }
    }
}
