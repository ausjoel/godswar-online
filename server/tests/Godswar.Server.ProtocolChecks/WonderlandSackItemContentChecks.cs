using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.ProtocolChecks;

internal static class WonderlandSackItemContentChecks
{
    public const string CheckName = "Wonderland original boss sacks match native client item metadata";

    public static Task RunAsync()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
            directory = directory.Parent;
        Check.True(directory is not null, "repository client metadata is available");
        var root = XDocument.Load(Path.Combine(directory!.FullName,
            "Localization/en_us/Settings/Sys/ItemBaseAttribute.xml")).Root!;
        var nativeItems = root.Elements().Where(group => group.Name.LocalName != "Weapons")
            .SelectMany(group => group.Elements()).ToArray();
        var names = File.ReadAllLines(Path.Combine(directory.FullName, "Localization/en_us/Text/EquipName.dat"))
            .Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.First()[1]);
        var seeds = WonderlandSackItemContentBaseline.ItemTemplates;
        Check.Equal(12, seeds.Count, "all original Wonderland boss container identities are available");
        foreach (var seed in seeds)
        {
            var native = nativeItems.Single(item => (string?)item.Attribute("ID") ==
                seed.Id.ToString(CultureInfo.InvariantCulture));
            Check.True(native.Name.LocalName == seed.NameKey && names[seed.NameKey] == seed.DisplayName,
                $"sack {seed.Id} preserves the original key and name");
            var actual = native.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value);
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(seed.StatsJson)!;
            Check.True(actual.Count == expected.Count && expected.All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value),
                $"sack {seed.Id} preserves every client attribute, icon, binding, stack cap and activation link");
        }
        return Task.CompletedTask;
    }
}
