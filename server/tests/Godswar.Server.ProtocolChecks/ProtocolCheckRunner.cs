using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Godswar.Server.ProtocolChecks;

internal sealed class CheckSkippedException(string reason) : Exception(reason);

internal enum ProtocolCheckOutcome { Passed, Failed, Skipped }

internal sealed record ProtocolCheckResult(
    string Name,
    ProtocolCheckOutcome Outcome,
    long DurationMs,
    string? Reason);

internal sealed record ProtocolCheckReport(
    int SchemaVersion,
    int ExitCode,
    bool RequireNoSkips,
    string[] UnmatchedFilters,
    ProtocolCheckResult[] Checks);

internal static class ProtocolCheckRunner
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<(string Name, Func<Task> Run)> checks,
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        var filters = new List<string>();
        var requireNoSkips = false;
        string? resultsPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--require-no-skips")
            {
                requireNoSkips = true;
            }
            else if (argument == "--results-json")
            {
                if (++index >= args.Length ||
                    string.IsNullOrWhiteSpace(args[index]) ||
                    args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    error.WriteLine("--results-json requires a file path.");
                    return 2;
                }
                resultsPath = args[index];
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"Unknown protocol-check option: {argument}");
                return 2;
            }
            else if (!string.IsNullOrWhiteSpace(argument))
            {
                filters.Add(argument);
            }
        }

        var unmatched = filters.Where(filter => !checks.Any(check =>
                check.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (unmatched.Length > 0)
        {
            error.WriteLine($"No protocol check matched: {string.Join(", ", unmatched)}");
            await WriteReportAsync(resultsPath, new(1, 2, requireNoSkips, unmatched, []));
            return 2;
        }

        var selected = checks.Where(check => filters.Count == 0 ||
            filters.Any(filter => check.Name.Contains(
                filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        var results = new List<ProtocolCheckResult>(selected.Length);
        foreach (var check in selected)
        {
            var started = Stopwatch.GetTimestamp();
            ProtocolCheckOutcome outcome;
            string? reason = null;
            try
            {
                if (PublicPackageSkips.Omits(check.Name))
                {
                    throw new CheckSkippedException(PublicPackageSkips.Reason);
                }
                await check.Run();
                outcome = ProtocolCheckOutcome.Passed;
                output.WriteLine($"PASS {check.Name}");
            }
            catch (CheckSkippedException skipped)
            {
                outcome = ProtocolCheckOutcome.Skipped;
                reason = skipped.Message;
                output.WriteLine($"SKIP {check.Name}: {reason}");
            }
            catch (Exception failure)
            {
                outcome = ProtocolCheckOutcome.Failed;
                reason = failure.ToString();
                error.WriteLine($"FAIL {check.Name}: {failure}");
            }
            results.Add(new(check.Name, outcome,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, reason));
        }

        var passed = results.Count(result => result.Outcome == ProtocolCheckOutcome.Passed);
        var failed = results.Count(result => result.Outcome == ProtocolCheckOutcome.Failed);
        var skippedCount = results.Count(result => result.Outcome == ProtocolCheckOutcome.Skipped);
        var exitCode = failed > 0 || requireNoSkips && skippedCount > 0 ? 1 : 0;
        output.WriteLine($"Protocol checks: {passed} passed, {failed} failed, {skippedCount} skipped");
        if (requireNoSkips && skippedCount > 0)
        {
            error.WriteLine("Selected checks were skipped while --require-no-skips was enabled.");
        }
        await WriteReportAsync(resultsPath,
            new(1, exitCode, requireNoSkips, [], results.ToArray()));
        return exitCode;
    }

    private static async Task WriteReportAsync(string? path, ProtocolCheckReport report)
    {
        if (path is null)
        {
            return;
        }
        var absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, JsonSerializer.Serialize(report, JsonOptions));
    }
}
