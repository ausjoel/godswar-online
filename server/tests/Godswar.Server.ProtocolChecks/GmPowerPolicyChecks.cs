using System.Text.Json.Nodes;
using Godswar.Server.Application.Operators;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Operators;

namespace Godswar.Server.ProtocolChecks;

// W5B: the GM power policy (public.gm_power_policy). Defaults = the behaviour before the policy existed.
internal static class GmPowerPolicyChecks
{
    public const string CheckName = "GM power policy: defaults allow everything, switched-off powers are refused server-side";

    public static Task RunAsync()
    {
        try
        {
            GmPowerPolicy.Reset();
            Check.Equal("defaults", GmPowerPolicy.Current.Source, "no table read yet = compiled defaults");
            foreach (var definition in GmPowerPolicy.Definitions)
            {
                Check.True(GmPowerPolicy.Allows(definition.Key), $"default allows {definition.Key}");
                Check.True(GmPowerPolicy.IsKnown(definition.Key), $"{definition.Key} is a known key");
                Check.True(
                    System.Text.RegularExpressions.Regex.IsMatch(definition.Key, "^[a-z][a-z0-9_.]{0,47}$"),
                    $"{definition.Key} fits the table's key CHECK");
            }

            Check.Equal(
                GmPowerPolicy.Definitions.Count,
                GmPowerPolicy.Definitions.Select(static d => d.Key).Distinct(StringComparer.Ordinal).Count(),
                "power keys are unique");

            // Every chat word the server recognises maps to a catalogued power (nothing can be used that the page
            // does not list).
            var words = new[]
            {
                "/announce", "/kick", "/mute", "/unmute", "/ban", "/unban", "/goto", "/leave", "/testrun", "/summon",
                "/move", "/where", "/instances", "/gm", "/panel", "/**", "/god", "/walls", "/speed", "/power", "/armor",
                "/bufflock", "/petlock", "/powers", "/buff", "/give", "/bag", "/bind", "/dupe", "/itemset", "/commands",
                "/mall", "/craft",
                DeveloperProgressionChatCommand.LevelPrefix, DeveloperProgressionChatCommand.TalentPointsPrefix,
                DeveloperProgressionChatCommand.TalentAliasPrefix, DeveloperProgressionChatCommand.ZodiacEnergyPrefix,
                DeveloperProgressionChatCommand.ZodiacEnergyAliasPrefix,
                DeveloperItemCommand.Prefix, DeveloperItemCommand.LegacyPrefix, DeveloperItemCommand.MaskedLegacyPrefix,
                DeveloperItemCommand.RubyPrefix, "/medusatitle"
            };
            foreach (var word in words)
            {
                Check.True(GmPowerPolicy.IsKnown(GmPowerPolicy.CommandKey(word)), $"{word} has a catalogued power");
            }

            Check.Equal("cmd.gm", GmPowerPolicy.CommandKey("/**"), "the masked /gm spelling is the same power");
            Check.Equal("cmd.gm", GmPowerPolicy.CommandKey("/PANEL"), "aliases ignore case");
            Check.Equal("cmd.item", GmPowerPolicy.CommandKey("/******"), "the masked /gmitem spelling is /item");
            Check.Equal("cmd.talentpoints", GmPowerPolicy.CommandKey("/talent"), "/talent is /talentpoints");
            Check.Equal("cmd.zodiacenergy", GmPowerPolicy.CommandKey("/zenergy"), "/zenergy is /zodiacenergy");

            const string line = "Odin:/level set 80";
            Check.Equal(
                "cmd.level",
                GmPowerPolicy.CommandKeyOfLine(line, DeveloperCommandText.FindBodyOffset(line)),
                "the command of a chat line with a name prefix");
            Check.True(GmPowerPolicy.CommandKeyOfLine("hello", -1) is null, "ordinary chat has no command key");

            // One command switched off: only that one is refused.
            var unknown = GmPowerPolicy.Install(
                [KeyValuePair.Create("cmd.give", false), KeyValuePair.Create("cmd.kick", true),
                 KeyValuePair.Create("cmd.nosuch", false)],
                "database",
                "editor");
            Check.Equal(1, unknown.Count, "an unknown key is reported");
            Check.Equal("cmd.nosuch", unknown[0], "the unknown key is named");
            Check.True(!GmPowerPolicy.Allows("cmd.give"), "a switched-off command is refused");
            Check.True(GmPowerPolicy.Allows("cmd.kick"), "an explicit allow row allows");
            Check.True(GmPowerPolicy.Allows("cmd.dupe"), "a command without a row stays allowed");
            Check.True(GmPowerPolicy.Allows(GmPowerPolicy.GateBypass), "the gate skip is its own switch");
            Check.True(GmPowerPolicy.Allows("cmd.nosuch"), "an unknown key never denies (it is ignored)");

            // Powers already switched on stop working when their command is switched off.
            GmPowerPolicy.Install(
                [KeyValuePair.Create("cmd.god", false), KeyValuePair.Create("cmd.power", false)], "database");
            var masked = OperatorControls.MaskByPolicy(
                new OperatorPowers(Invulnerable: true, SpeedFactor: 3f, AttackMultiplier: 50, DefenseBonus: 9));
            Check.True(!masked.Invulnerable, "/god stops when switched off");
            Check.Equal(1, masked.AttackMultiplier, "/power stops when switched off");
            Check.Equal(3f, masked.SpeedFactor, "/speed is untouched");
            Check.Equal(9, masked.DefenseBonus, "/armor is untouched");

            // The master switch refuses everything.
            GmPowerPolicy.Install([KeyValuePair.Create(GmPowerPolicy.Master, false)], "database");
            foreach (var definition in GmPowerPolicy.Definitions)
            {
                Check.True(!GmPowerPolicy.Allows(definition.Key), $"master off refuses {definition.Key}");
            }

            Check.True(
                OperatorControls.MaskByPolicy(new OperatorPowers(Invulnerable: true, SpeedFactor: 3f)).IsNone,
                "master off clears a GM's active powers");
            var doc = GmPowerPolicy.Describe();
            Check.True(doc["master"]!.GetValue<bool>() is false, "the description reports master off");
            Check.Equal(GmPowerPolicy.Definitions.Count, ((JsonArray)doc["powers"]!).Count, "every power is described");
            var give = ((JsonArray)doc["powers"]!).First(p => p!["key"]!.GetValue<string>() == "cmd.give")!;
            Check.True(give["allowed"]!.GetValue<bool>(), "cmd.give has no row of its own");
            Check.True(!give["effective"]!.GetValue<bool>(), "but master off makes it ineffective");

            // Switching back on is hot too; the runtime's Apply is what the poll calls.
            GmPowerPolicyRuntime.Apply([KeyValuePair.Create(GmPowerPolicy.Master, true)], "poll", "editor");
            Check.True(GmPowerPolicy.Allows("cmd.give"), "master on again allows");
            GmPowerPolicyRuntime.Apply([], "poll", "editor");
            Check.Equal(0, GmPowerPolicy.Current.Denied.Count, "an empty table = today's behaviour");

            Check.Equal(TimeSpan.FromSeconds(5), GmPowerPolicyRuntime.ReadPollInterval(null), "default poll");
            Check.Equal(TimeSpan.FromSeconds(5), GmPowerPolicyRuntime.ReadPollInterval("0"), "a bad poll value keeps the default");
            Check.Equal(TimeSpan.FromSeconds(30), GmPowerPolicyRuntime.ReadPollInterval("30"), "poll override");
            Check.True(GmPowerPolicyRuntime.SchemaSql.Contains("CREATE TABLE IF NOT EXISTS public.gm_power_policy", StringComparison.Ordinal),
                "idempotent DDL, not a registered migration");
        }
        finally
        {
            GmPowerPolicy.Reset();
        }

        return Task.CompletedTask;
    }
}
