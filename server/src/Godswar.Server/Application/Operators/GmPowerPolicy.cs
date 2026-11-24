using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace Godswar.Server.Application.Operators;

// W5B (the admin 2026-11-12: "Assess GM abilities and allow turning on and off for the server"): one catalogue of every
// GM power and one hot policy that says which of them this server allows.
//
//  * WHO is a GM is unchanged (OperatorAuthority: the character's "GM" flag, or the account allowlist).
//  * WHAT a GM may do is this policy: public.gm_power_policy (migration 20261112_242), one row per switched power.
//    No row = allowed, so an empty table is exactly the behaviour before this change.
//  * 'master' off refuses every power below; a GM is then an ordinary player until it is switched on again.
//  * The table is polled (GmPowerPolicyRuntime); a read failure keeps the last good snapshot and never stops the
//    game. A recognised command that is switched off is still consumed (it never reaches chat), the GM is told
//    "switched off", and the attempt is written to operator_log.
internal sealed record GmPowerDefinition(
    string Key,
    string Group,
    string Label,
    string Description,
    string Risk = "normal");

internal sealed record GmPowerSnapshot(
    FrozenSet<string> Denied,
    string Source,
    DateTimeOffset LoadedAtUtc,
    string UpdatedBy)
{
    public static GmPowerSnapshot Defaults { get; } =
        new(FrozenSet<string>.Empty, "defaults", DateTimeOffset.UnixEpoch, "");
}

internal static class GmPowerPolicy
{
    public const string Master = "master";
    public const string GateBypass = "power.gate_bypass";
    public const string LimitLift = "power.limit_lift";
    public const string CommandPrefix = "cmd.";

    public const string RefusedText = "switched off on this server (editor: GM powers)";

    private static GmPowerSnapshot _current = GmPowerSnapshot.Defaults;

    // The chat spellings that are the same command. The client masks "gm" to "**" and "gmitem" to "******".
    private static readonly FrozenDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["**"] = "gm",
            ["panel"] = "gm",
            ["gmitem"] = "item",
            ["******"] = "item",
            ["talent"] = "talentpoints",
            ["zenergy"] = "zodiacenergy"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<GmPowerDefinition> Definitions { get; } =
    [
        new(Master, "master", "GM powers (master switch)",
            "Off = every GM command, the GM Mall and the gate skip are refused for everybody. GM flags stay as they are.",
            "master"),

        new("cmd.announce", "moderation", "/announce <text>",
            "Red banner on every screen plus a World chat line."),
        new("cmd.kick", "moderation", "/kick <name>", "Disconnects a player."),
        new("cmd.mute", "moderation", "/mute <name> <minutes>", "Blocks a player's chat for up to a year."),
        new("cmd.unmute", "moderation", "/unmute <name>", "Lifts a mute."),
        new("cmd.ban", "moderation", "/ban <name> [reason]",
            "Blocks the whole account from logging in and kicks it. GM accounts cannot be banned this way.", "high"),
        new("cmd.unban", "moderation", "/unban <name>", "Lifts a ban."),

        new("cmd.goto", "travel", "/goto <name>", "Jumps to a player (not into a private dungeon run)."),
        new("cmd.summon", "travel", "/summon <name>", "Pulls a player to the GM."),
        new("cmd.move", "travel", "/move <map> <x> <z> [@name]",
            "Teleports the GM or another player, online or logged out."),
        new("cmd.leave", "travel", "/leave", "Leaves the current instance run."),
        new("cmd.testrun", "travel", "/testrun ...", "Starts a private test run of an instance."),
        new("cmd.wlclear", "travel", "/wlclear", // WL-SLIDERS / EDITOR-SMOKE: the rig aid was missing from the catalogue
            "Test servers only (GODSWAR_RIG_TEST_RUNS=1): kills every enemy of the current Wonderland island."),
        new("cmd.where", "travel", "/where [name]", "Shows a map number and position."),
        new("cmd.instances", "travel", "/instances", "Lists every running world instance and who is inside."),

        new("cmd.god", "self", "/god on|off", "Monsters and players cannot hurt the GM.", "high"),
        new("cmd.walls", "self", "/walls on|off", "Walk through walls."),
        new("cmd.speed", "self", "/speed <x>|off [@name]", "Run speed multiplier, for the GM or one online player."),
        new("cmd.power", "self", "/power <x>|off", "Attack multiplier.", "high"),
        new("cmd.armor", "self", "/armor <n>|off", "Added defence.", "high"),
        new("cmd.bufflock", "self", "/bufflock on|off", "Buffs the GM receives last 24 hours."),
        new("cmd.petlock", "self", "/petlock on|off", "The GM's pet care does not drop."),
        new("cmd.powers", "self", "/powers [off]", "Shows or clears the GM's own powers."),
        new("cmd.buff", "self", "/buff ... [@name]", "Puts a buff or status on the GM or another player."),

        new("cmd.give", "items", "/give <itemId> [qty] [bound] [@name]",
            "Any item by id, to the GM or another player.", "high"),
        new("cmd.item", "items", "/item add|mount|clearbag (also /gmitem)",
            "Items and mounts from the GM list; clearbag empties the bag.", "high"),
        new("cmd.ruby", "items", "/ruby <level> <qty>", "Rubies, level 1 to 3.", "high"),
        new("cmd.mall", "items", "/mall (GM Mall window)",
            "Opens the GM Mall shop window and lets the GM buy from it (1 silver each, or free).", "high"),
        new("cmd.craft", "items", "/craft ...", "Crafts a profession blueprint (only with professions on).", "high"),
        new("cmd.bag", "items", "/bag [name]", "Lists a bag with slot numbers."),
        new("cmd.bind", "items", "/bind <slot> on|off [@name]", "Makes an item bound or tradable.", "high"),
        new("cmd.itemset", "items", "/itemset <slot> quality|grade|stack|exp <n> [@name]",
            "Changes an item's quality, grade, stack or EXP.", "high"),
        new("cmd.dupe", "items", "/dupe <slot> [@name]", "Copies an item, sealed pets included.", "high"),

        new("cmd.level", "progression", "/level set|add <n> [@name]", "Sets or changes a level (1-200).", "high"),
        new("cmd.talentpoints", "progression", "/talentpoints add <n> [@name] (also /talent)", "Adds talent points.",
            "high"),
        new("cmd.zodiacenergy", "progression", "/zodiacenergy add <n> [@name] (also /zenergy)",
            "Adds zodiac energy.", "high"),
        new("cmd.medusatitle", "progression", "/medusatitle test",
            "Gives the Medusa title and honour and shows a fake Medusa banner to the faction."),

        new("cmd.gm", "tools", "/gm (GM window; also /panel)", "Opens the GM window in the client."),
        new("cmd.commands", "tools", "/commands", "Prints the command list."),

        new(GateBypass, "bypass", "Skip level and opening-time gates",
            "A GM passes transporters, battlefields, instances and events outside their level range or opening " +
            "times (each gate's own GM rule and the rate gm.obey_gates still apply), and the Romance Island level.",
            "normal"),
        new(LimitLift, "bypass", "No limits on /speed, /power and /armor",
            "Off = the safe ranges apply (speed 0.5-10, power 1-1000, armor 0-1,000,000). Only matters while GM " +
            "rights come from character flags.", "high")
    ];

    private static readonly FrozenSet<string> KnownKeys =
        Definitions.Select(static d => d.Key).ToFrozenSet(StringComparer.Ordinal);

    public static GmPowerSnapshot Current => Volatile.Read(ref _current);

    public static bool IsKnown(string key) => KnownKeys.Contains(key);

    /// <summary>True when the server allows this power: the master switch is on and the power is not switched off.</summary>
    public static bool Allows(string key)
    {
        var denied = Current.Denied;
        return denied.Count == 0 || (!denied.Contains(Master) && !denied.Contains(key));
    }

    /// <summary>The policy key of a chat command word ("/kick", "/**", "/talent" ...).</summary>
    public static string CommandKey(string word)
    {
        var name = (word ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();
        return CommandPrefix + (Aliases.TryGetValue(name, out var canonical) ? canonical : name);
    }

    /// <summary>
    /// The policy key of the command a chat line starts with, or null when the line is not a command.
    /// <paramref name="bodyOffset"/> is DeveloperCommandText.FindBodyOffset(text).
    /// </summary>
    public static string? CommandKeyOfLine(string text, int bodyOffset)
    {
        if (string.IsNullOrEmpty(text) || bodyOffset < 0 || bodyOffset >= text.Length || text[bodyOffset] != '/')
        {
            return null;
        }

        var end = bodyOffset;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return CommandKey(text[bodyOffset..end]);
    }

    /// <summary>Installs the rows of gm_power_policy. Unknown keys are kept out and reported.</summary>
    public static IReadOnlyList<string> Install(
        IEnumerable<KeyValuePair<string, bool>> rows,
        string source,
        string updatedBy = "")
    {
        ArgumentNullException.ThrowIfNull(rows);
        var denied = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var (key, allowed) in rows)
        {
            if (!KnownKeys.Contains(key))
            {
                unknown.Add(key);
                continue;
            }

            if (!allowed)
            {
                denied.Add(key);
            }
        }

        Volatile.Write(
            ref _current,
            new GmPowerSnapshot(
                denied.ToFrozenSet(StringComparer.Ordinal),
                source,
                DateTimeOffset.UtcNow,
                updatedBy ?? string.Empty));
        return unknown;
    }

    public static void Reset() => Volatile.Write(ref _current, GmPowerSnapshot.Defaults);

    /// <summary>The catalogue with the effective state, for GET /admin/capabilities (the editor's GM powers page).</summary>
    public static JsonObject Describe()
    {
        var snapshot = Current;
        var masterOn = !snapshot.Denied.Contains(Master);
        var powers = new JsonArray();
        foreach (var definition in Definitions)
        {
            var switchedOn = !snapshot.Denied.Contains(definition.Key);
            powers.Add(new JsonObject
            {
                ["key"] = definition.Key,
                ["group"] = definition.Group,
                ["label"] = definition.Label,
                ["description"] = definition.Description,
                ["risk"] = definition.Risk,
                ["allowed"] = switchedOn,
                ["effective"] = switchedOn && (masterOn || definition.Key == Master)
            });
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["table"] = "gm_power_policy",
            ["source"] = snapshot.Source,
            ["loadedAtUtc"] = snapshot.LoadedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["updatedBy"] = snapshot.UpdatedBy,
            ["master"] = masterOn,
            ["powers"] = powers
        };
    }
}
