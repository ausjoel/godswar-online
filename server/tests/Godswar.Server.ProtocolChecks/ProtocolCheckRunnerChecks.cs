using System.Text.Json;

namespace Godswar.Server.ProtocolChecks;

internal static class ProtocolCheckRunnerChecks
{
    public const string CheckName = "Protocol-check outcomes and required selection";

    public static async Task RunAsync()
    {
        var executions = 0;
        (string Name, Func<Task> Run)[] checks =
        [
            ("available", () => { executions++; return Task.CompletedTask; }),
            ("optional database", () => throw new CheckSkippedException("fixture unavailable")),
            ("broken", () => throw new InvalidOperationException("intentional failure"))
        ];
        using var output = new StringWriter();
        using var error = new StringWriter();
        Check.Equal(2, await ProtocolCheckRunner.RunAsync(checks,
            ["available", "misspelled"], output, error),
            "every requested filter must match even when another filter is valid");
        Check.Equal(0, executions, "invalid selection runs no partial suite");
        Check.Equal(0, await ProtocolCheckRunner.RunAsync(checks,
            ["optional database"], output, error), "optional skips remain nonfatal");
        Check.True(!output.ToString().Contains("PASS optional database", StringComparison.Ordinal),
            "a skipped check never receives a PASS receipt");
        Check.Equal(1, await ProtocolCheckRunner.RunAsync(checks,
            ["optional database", "--require-no-skips"], output, error),
            "required selection fails when its prerequisite is absent");
        Check.Equal(1, await ProtocolCheckRunner.RunAsync(checks,
            ["broken"], output, error), "ordinary failures are never converted into skips");
        Check.Equal(0, await ProtocolCheckRunner.RunAsync(checks,
            ["AVAILABLE", "--require-no-skips"], output, error),
            "existing case-insensitive substring labels remain supported");
        Check.Equal(2, await ProtocolCheckRunner.RunAsync(checks,
            ["--results-json"], output, error), "missing report path is an argument error");

        var directory = Path.Combine(Path.GetTempPath(), "godswar-check-runner-" + Guid.NewGuid().ToString("N"));
        var reportPath = Path.Combine(directory, "result.json");
        try
        {
            await ProtocolCheckRunner.RunAsync(checks,
                ["available", "optional database", "--results-json", reportPath], output, error);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var rows = report.RootElement.GetProperty("checks");
            Check.Equal(2, rows.GetArrayLength(), "structured output records all selected outcomes");
            Check.Equal("passed", rows[0].GetProperty("outcome").GetString()!, "pass is explicit");
            Check.Equal("skipped", rows[1].GetProperty("outcome").GetString()!, "skip is explicit");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
