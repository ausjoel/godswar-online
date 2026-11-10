using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Pins the V9 NPC content release: the nine Sparta actors the September 28 2026
/// capture placed while no earlier release carried them.
/// </summary>
/// <remarks>
/// The publication refuses to start when a declared count or revision disagrees
/// with what it computed, so these checks catch that here instead of as a crash
/// loop in a running server. They also pin the capture's own numbers per row, so
/// a later edit cannot silently move an actor the reference placed.
/// </remarks>
internal static partial class NpcContentAuthorityChecks
{
    private static void CheckSpartaV9Release()
    {
        var previous = NpcContentBaselineV8.LoadDefinitions();
        var definitions = NpcContentBaselineV9.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);

        Check.Equal(
            NpcContentBaselineV8.ExpectedEntryCount +
                NpcContentBaselineV9.AddedEntryCount,
            definitions.Length,
            "Sparta V9 adds exactly the nine captured actors to V8");
        Check.Equal(
            NpcContentBaselineV9.ExpectedEntryCount,
            definitions.Length,
            "Sparta V9 declared entry count");
        Check.Equal(
            NpcContentBaselineV9.ExpectedRevision,
            revision.Sha256,
            "Sparta V9 golden revision");
        Check.Equal(
            definitions.Length,
            revision.EntryCount,
            "Sparta V9 revision entry count");

        // The reviewed V8 release stays immutable: this release is additive only.
        var previousRows = previous
            .Select(static npc => (npc.MapId, npc.NpcKey))
            .ToHashSet();
        foreach (var key in previousRows)
        {
            Check.True(
                definitions.Any(npc => (npc.MapId, npc.NpcKey) == key),
                $"Sparta V9 retains V8 actor {key.NpcKey}");
        }

        Check.True(
            !previous.Any(static npc =>
                npc.NpcKey is "Sparta_012" or "Sparta_024" or "Sparta_025" or
                    "Sparta_063" or "Sparta_065" or "Sparta_066" or
                    "Sparta_067" or "Sparta_117" or "Sparta_126"),
            "V8 published none of the nine captured actors");

        Check.Equal(
            NpcContentBaselineV9.AddedEntryCount,
            NpcContentBaselineV9.CapturedNpcs.Length,
            "Sparta V9 roster size");

        foreach (var captured in NpcContentBaselineV9.CapturedNpcs)
        {
            var matches = definitions
                .Where(npc => npc.NpcKey == captured.NpcKey)
                .ToArray();
            Check.Equal(
                1,
                matches.Length,
                $"Sparta V9 publishes {captured.NpcKey} exactly once");
            if (matches.Length != 1)
            {
                continue;
            }

            var npc = matches[0];
            Check.Equal(
                NpcContentBaselineV9.SpartaMapId,
                npc.MapId,
                $"{captured.NpcKey} stays on the Sparta map");
            Check.Equal(
                NpcContentBaselineV9.SpartaSceneKey,
                npc.SceneKey,
                $"{captured.NpcKey} uses the Sparta scene key");
            Check.Equal(
                captured.TemplateKey,
                npc.TemplateKey,
                $"{captured.NpcKey} keeps the captured appearance template");
            Check.Equal(
                captured.ObjectId,
                npc.ObjectId,
                $"{captured.NpcKey} keeps the captured object id");
            Check.Equal(
                npc.ObjectId,
                npc.InteractionId,
                $"{captured.NpcKey} interacts under its own object id");
            Check.Equal(
                NpcContentBaselineV9.AppearanceType,
                npc.AppearanceType,
                $"{captured.NpcKey} keeps the captured appearance word");
            Check.Equal(
                captured.X,
                npc.X,
                $"{captured.NpcKey} keeps the captured x");
            Check.Equal(
                captured.Z,
                npc.Z,
                $"{captured.NpcKey} keeps the captured z");
            Check.Equal(
                captured.Facing,
                npc.Facing,
                $"{captured.NpcKey} keeps the captured facing");
        }
    }
}
