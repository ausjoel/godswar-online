using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckWonderlandTerminalCityReviveAsync()
    {
        foreach (var timeout in new[] { true, false })
        foreach (var camp in new[] { GameDefaults.SpartaCamp, GameDefaults.AthensCamp })
        {
            await using var fixture = await CreateWonderlandHandlerFixtureAsync();
            var leader = fixture.Party.Leader;
            var character = leader.Character;
            character.Camp = camp;
            var runtime = await EnterWonderlandHandlerAsync(fixture);
            character.CurrentHp = 0;
            character.CurrentMp = 0;
            character.MarkVitalsChanged();
            leader.Registry.UpdateCharacter(leader.Session, character, advanceWorldRevision: false);
            var life = leader.Registry.GetPlayerLifeRevision(leader.Session);
            var claims = fixture.Daily.Claims.Count;
            runtime.Map.TryGetWonderlandSnapshot(out var active);
            if (!timeout)
                Check.True(leader.Registry.TryTerminateWonderlandRun(leader.Session, 227, 0, WonderlandNow(runtime)),
                    "dead original leader can terminate the exact active run");
            await leader.Registry.AdvanceMonsterWorldOnceAsync(timeout ? active.Deadline : WonderlandNow(runtime),
                CancellationToken.None);
            await CompleteAtlantisSceneReadinessAsync(leader.Handler);
            var capital = camp == GameDefaults.SpartaCamp ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
            Check.True(character.CurrentMap == capital && character.CurrentHp == 0 &&
                !leader.Registry.TryGetWorldInstance(runtime.InstanceId, out _) &&
                leader.Registry.GetPlayerLifeRevision(leader.Session) == life,
                "timeout/termination retires Wonderland and preserves dead state until the player chooses Revive");

            // A live handler has a loaded character snapshot. Keep its same
            // owned character during the normal free-revival re-entry bootstrap.
            SetHandlerField(leader.Handler, "_characterSnapshotLoaded", true);
            SetHandlerField(leader.Handler, "_characterSnapshotBootstrapPending", true);
            SetHandlerField(leader.Handler, "_characterLoadSnapshot", new HydratedCharacterLoadSnapshot(
                character, [], [], new CharacterPetShedSnapshot(2, 0), [], []));
            var store = GetHandlerField<InstanceCallerGameStore>(leader.Handler, "_store")!;
            var writes = store.VitalsWrites.Count;
            var before = leader.ReadPackets().Count;
            await InvokeAsync(leader.Handler, new GamePacket(Convert.FromHexString("0C002C274814000002000000")));
            Check.True(!leader.Session.IsDisconnected && character.CurrentMap == capital &&
                character.CurrentHp == Math.Max(1, character.MaxHp / 10) &&
                character.CurrentMp == Math.Max(0, character.MaxMp / 10) &&
                character.PositionX == GameDefaults.StartingPositionX && character.PositionZ == GameDefaults.StartingPositionZ &&
                leader.Registry.GetPlayerLifeRevision(leader.Session) == life + 1,
                "the exact live native city Revive packet restores ten-percent vitals at the faction capital once");
            Check.True(store.VitalsWrites.Count == writes + 1 && store.VitalsWrites[^1].CharacterId == character.Id &&
                store.VitalsWrites[^1].Hp == character.CurrentHp && store.VitalsWrites[^1].Mp == character.CurrentMp &&
                leader.ReadPackets().Skip(before).Any(packet => ReadOpcode(packet) == Opcodes.GameServerReady),
                "free city revival durably checkpoints restored vitals and completes native re-entry bootstrap");
            var revivalPackets = leader.ReadPackets().Skip(before).ToArray();
            var enterIndex = Array.FindIndex(revivalPackets, packet => ReadOpcode(packet) == 10019);
            var readyIndex = Array.FindIndex(revivalPackets, packet => ReadOpcode(packet) == Opcodes.GameServerReady);
            Check.True(enterIndex >= 0 && enterIndex < readyIndex && revivalPackets[enterIndex].Length >= 84 &&
                BinaryPrimitives.ReadInt32LittleEndian(revivalPackets[enterIndex].AsSpan(76)) == character.CurrentHp &&
                BinaryPrimitives.ReadInt32LittleEndian(revivalPackets[enterIndex].AsSpan(80)) == character.CurrentMp,
                "native EnterMain restores local HP/MP before re-entry readiness can select the revived avatar pose");
            var after = leader.ReadPackets().Count;
            await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448));
            Check.True(leader.Registry.GetPlayerLifeRevision(leader.Session) == life + 1 &&
                store.VitalsWrites.Count == writes + 1 && leader.ReadPackets().Count == after &&
                fixture.Daily.Claims.Count == claims && fixture.Titles.Requests.Count == 0,
                "living replay cannot revive again, reopen the retired dungeon, consume an entry, or invent a reward");
        }
    }
}
