using System.Globalization;
using System.Xml.Linq;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CapitalNpcServiceProtocolChecks
{
    private static void CheckVendorClientItemLoadability(uint[] stockIds)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
            directory = directory.Parent;
        Check.True(directory is not null, "repository client item metadata is available");
        var document = XDocument.Load(Path.Combine(directory!.FullName,
            "Localization", "en_us", "Settings", "Sys", "ItemBaseAttribute.xml"));
        var loaded = ReadNativeClientItemIds(document);
        var missing = stockIds.Except(loaded).ToArray();
        Check.True(missing.Length == 0,
            $"every transmitted vendor item is loaded by native XML traversal; missing: {string.Join(',', missing)}");

        // Reproduce the original valid-XML failure without changing a repository file.
        // A recursive Descendants() lookup would find 9017 and wrongly accept it.
        var malformed = new XDocument(document);
        var divinium = malformed.Descendants().Single(element =>
            (string?)element.Attribute("ID") == "9017");
        divinium.Remove();
        malformed.Root!.Add(divinium);
        var rejected = stockIds.Except(ReadNativeClientItemIds(malformed)).ToArray();
        Check.True(rejected.SequenceEqual(new uint[] { 9017 }),
            "root-level Divinium is detected as unavailable although its XML row still exists");
        divinium.Remove();
        malformed.Root.Element("Item")!.Add(divinium);
        Check.True(!stockIds.Except(ReadNativeClientItemIds(malformed)).Any(),
            "placing Divinium inside Item restores native availability without changing stock");
    }

    private static HashSet<uint> ReadNativeClientItemIds(XDocument document)
    {
        Check.True(document.Root?.Name.LocalName == "ItemBaseAttribute",
            "native item document has the expected root");
        // Origin.exe 43EB10: top-level nodes are containers, never item rows.
        // Weapons has one additional grouping level (43EBB7-43EC5D); other
        // containers pass only their direct children to 43ACC0 at 43ECBA.
        return document.Root!.Elements()
            .SelectMany(group => group.Name.LocalName == "Weapons"
                ? group.Elements().SelectMany(section => section.Elements())
                : group.Elements())
            .Select(element => (string?)element.Attribute("ID"))
            .Where(id => id is not null)
            .Select(id => uint.Parse(id!, CultureInfo.InvariantCulture))
            .ToHashSet();
    }
}
