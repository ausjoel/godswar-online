using Godswar.Server.Application.Rates;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// FINALISE (the admin 2026-11-05): a centre-screen notice when a world boss comes back after
/// it was killed. Bosses on the Athenian home maps (Marathon, Parnitha, Parnitha Port)
/// are announced to Athenians only, those on the Spartan home maps (Peloponnese, Nemea,
/// Nemea Forest) to Spartans only, every other world boss to both factions. "World boss"
/// is the existing world-boss catalog (table gameplay_world_boss_definitions, one per map).
/// Only a respawn after a kill is announced, never the spawn at server start. Off with the
/// hot rate event.world_boss_notice = 0.
/// </summary>
internal static class WorldBossNoticePolicy
{
    // Map ids from the world-boss catalog scene keys and the client's QuestRegion/Message
    // names: 11 Marathon_All, 3 Parnitha_1 (Parnitha), 12 Parnitha_2 (Parnitha Port);
    // 13 Peloponnese_All, 5 Nemea_1 (Nemea), 14 Nemea_2 (Nemea Forest).
    // FINALISE2 F-84 (the admin 2026-11-05: "details of world boss locations and factions here:" the GodsArena wiki Elites
    // page): its "Athens fraction" lists Marathon, Parnitha, Parnitha Port, Megara (18) and Plataea (19); its "Sparta
    // fraction" lists Peloponnese, Nemea, Nemean Forest, Argolis (16) and Derveni (15); everything else is "Neutral".
    public static readonly IReadOnlySet<short> AthensOnlyMaps = new HashSet<short> { 3, 11, 12, 18, 19 }; // FINALISE2
    public static readonly IReadOnlySet<short> SpartaOnlyMaps = new HashSet<short> { 5, 13, 14, 15, 16 }; // FINALISE2

    private static readonly Dictionary<string, string> SceneNames = new(StringComparer.Ordinal)
    {
        ["Parnitha_1"] = "Parnitha",
        ["Parnitha_2"] = "Parnitha Port",
        ["Nemea_1"] = "Nemea",
        ["Nemea_2"] = "Nemea Forest",
        ["Oracle_of_Delphi_All"] = "Delphi Forest",
        ["Isthmus_of_Corinth_All"] = "Isthmus",
    };

    public static bool Reaches(short mapId, byte camp) =>
        AthensOnlyMaps.Contains(mapId) ? camp == GameDefaults.AthensCamp
        : SpartaOnlyMaps.Contains(mapId) ? camp == GameDefaults.SpartaCamp
        : camp is GameDefaults.AthensCamp or GameDefaults.SpartaCamp;

    public static string MapName(string sceneKey)
    {
        if (SceneNames.TryGetValue(sceneKey, out var name)) return name;
        var trimmed = sceneKey.EndsWith("_All", StringComparison.Ordinal) ? sceneKey[..^4] : sceneKey;
        return trimmed.Replace('_', ' ');
    }

    /// <summary>Printable ASCII only, no colour markup, short enough for the native frame.</summary>
    public static string Text(string bossName, string sceneKey) => Text(bossName, sceneKey, -1, float.NaN, float.NaN);

    /// <summary>FINALISE2 F-84: the notice names where the boss stands and whose lands those are.</summary>
    public static string Text(string bossName, string sceneKey, short mapId, float x, float z)
    {
        static string Clean(string value) =>
            new string(value.Where(static c => c is >= ' ' and <= '~' and not '|').ToArray()).Trim();

        var boss = Clean(bossName);
        if (boss.Length == 0) boss = "A world boss";
        var where = float.IsFinite(x) && float.IsFinite(z) ? $" at ({Math.Round(x):0}, {Math.Round(z):0})" : string.Empty; // FINALISE2
        var lands = mapId < 0 ? string.Empty
            : AthensOnlyMaps.Contains(mapId) ? " (Athenian lands)"
            : SpartaOnlyMaps.Contains(mapId) ? " (Spartan lands)"
            : " (open to both factions)"; // FINALISE2
        var text = $"World boss {boss} has appeared in {Clean(MapName(sceneKey))}{where}!{lands}";
        return text.Length <= PacketBuilder.CenteredAnnouncementMaximumTextLength
            ? text
            : text[..PacketBuilder.CenteredAnnouncementMaximumTextLength];
    }
}

internal sealed partial class GameSessionRegistry
{
    private readonly record struct WorldBossNotice(short MapId, string Text);

    private List<WorldBossNotice>? CaptureWorldBossNotices(
        short mapId,
        MonsterRuntimeTick tick,
        List<WorldBossNotice>? notices)
    {
        foreach (var update in tick.Updates)
        {
            if (update.Kind != MonsterRuntimeUpdateKind.Respawned ||
                !_gameplayCatalogs.WorldBosses.TryGet(mapId, out var boss) ||
                !string.Equals(boss.TemplateKey, update.Monster.Definition.TemplateKey, StringComparison.Ordinal))
            {
                continue;
            }

            (notices ??= []).Add(new WorldBossNotice(
                mapId,
                WorldBossNoticePolicy.Text(boss.DisplayName, boss.SceneKey, mapId,
                    update.Monster.X, update.Monster.Z))); // FINALISE2 F-84
        }

        return notices;
    }

    private async Task PublishWorldBossNoticesAsync(
        List<WorldBossNotice>? notices,
        CancellationToken cancellationToken)
    {
        if (notices is null) return;
        if (ServerRates.Current.Absolute(ServerRateKeys.WorldBossNotice) != 1) return;
        foreach (var notice in notices)
        {
            byte[] packet;
            try
            {
                packet = PacketBuilder.CenteredAnnouncement(notice.Text);
            }
            catch (ArgumentException error)
            {
                Console.WriteLine($"[world-boss] notice skipped map={notice.MapId}: {error.Message}");
                continue;
            }

            var delivered = 0;
            foreach (var context in _sessions.Values.ToArray())
            {
                if (!WorldBossNoticePolicy.Reaches(notice.MapId, context.Character.Camp)) continue;
                try
                {
                    await context.Session.SendAsync(packet, cancellationToken, "WorldBossNotice");
                    delivered++;
                    // the admin 2026-11-10: world boss spawns also stay in the chat box (board.world_boss_chat_line).
                    if (Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                            Godswar.Server.Application.Finalise2.Finalise2RateKeys.ChatWorldBossLine))
                        await SendChatLineAsync(context, notice.Text, "WorldBossChat", cancellationToken);
                }
                catch (Exception ex) when (
                    ex is ObjectDisposedException or IOException or InvalidOperationException)
                {
                    // A closing session must not stop the notice for the rest.
                }
            }

            Console.WriteLine($"[world-boss] notice map={notice.MapId} recipients={delivered} text=\"{notice.Text}\"");
        }
    }
}
