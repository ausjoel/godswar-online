using System.Text.Json;
using System.Xml.Linq;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.ProtocolChecks;

internal static class ExperiencePillItemContentChecks
{
    public const string CheckName = "EXP Pill preserves its exact native client identity and activation metadata";

    public static Task RunAsync()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
            directory = directory.Parent;
        Check.True(directory is not null, "repository native item metadata is available");
        var root = XDocument.Load(Path.Combine(directory!.FullName,
            "Localization/en_us/Settings/Sys/ItemBaseAttribute.xml")).Root!;
        var native = root.Elements("Item").SelectMany(group => group.Elements())
            .Single(item => (string?)item.Attribute("ID") == "4174");
        var seed = ExperiencePillItemContentBaseline.ItemTemplates.Single();
        var name = File.ReadAllLines(Path.Combine(directory.FullName, "Localization/en_us/Text/EquipName.dat"))
            .Select(line => line.Split('\t', 2)).Single(parts => parts.Length == 2 && parts[0] == "AddEXP5")[1];
        Check.True(seed.Id == 4174 && seed.NameKey == native.Name.LocalName && seed.DisplayName == name &&
            seed.Kind == "consume item" && seed.EquipmentSlot == 0 && seed.ClassIds.Length == 0 &&
            seed.MinLevel is null && seed.MaxLevel is null && seed.Hand is null && seed.SkillFlag is null,
            "the pill remains the existing AddEXP5 consumable without added equipment or level restrictions");
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(seed.StatsJson)!;
        var actual = native.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value);
        Check.True(expected.Count == actual.Count && expected.All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value) &&
            seed.Icon == "612,900" && seed.Texture == actual["Texture"] && actual["Use"] == "1" &&
            actual["Skill"] == "5149" && actual["Overlap"] == "99" && actual["BindType"] == "1",
            "native item parent, icon, binding, stack cap and activation skill remain exactly unchanged");
        return Task.CompletedTask;
    }
}
