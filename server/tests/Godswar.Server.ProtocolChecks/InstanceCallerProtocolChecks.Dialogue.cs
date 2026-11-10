namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerProtocolChecks
{
    private static void CheckClientAttemptDialogue()
    {
        var luaText = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Localization",
            "en_us",
            "UI",
            "Base",
            "LuaText.lua"));
        Check.True(
            luaText.Contains(
                "receives |cff39D8B83 free entries each day",
                StringComparison.Ordinal) &&
            luaText.Contains(
                "one additional entry costs |cff39D8B8one Opal",
                StringComparison.Ordinal) &&
            luaText.Contains(
                "NF_L0_R1601=\"Your Opal is ready for this exact party " +
                "and additional entry.",
                StringComparison.Ordinal) &&
            luaText.Contains(
                "NF_L0_R1602=\"Not everyone who has exhausted their own " +
                "3 free Atlantis entries is ready.",
                StringComparison.Ordinal) &&
            luaText.Contains(
                "You've reached today's |cff39D8B8Atlantis entry limit",
                StringComparison.Ordinal) &&
            luaText.Contains(
                "This instance may be entered up to 3 times per day on " +
                "Saturday and Sunday before 23:00!",
                StringComparison.Ordinal) &&
            !luaText.Contains(
                "enter once for free each day",
                StringComparison.Ordinal) &&
            !luaText.Contains(
                "only allowed to enter once a day before 23:00",
                StringComparison.Ordinal),
            "client dialogue states three free Atlantis entries, one " +
            "Opal-funded additional entry, and three free Wonderland entries");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory =
                 new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not locate GodswarServer.sln.");
    }
}
