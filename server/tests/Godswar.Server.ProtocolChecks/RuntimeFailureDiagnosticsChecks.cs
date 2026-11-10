using System.Runtime.CompilerServices;
using System.Text.Json;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.ProtocolChecks;

internal static class RuntimeFailureDiagnosticsChecks
{
    public const string CheckName = "B13 runtime failures retain bounded safe exception diagnostics";

    public static async Task RunAsync()
    {
        using var sink = new StringWriter();
        using var logger = new BoundedStructuredLogger(sink);
        try
        {
            // Mirrors the host's Task.WhenAll failure boundary: cancellation
            // of sibling tasks must not conceal the original runtime fault.
            await Task.WhenAll(FailWorldAsync(), Task.FromCanceled(new CancellationToken(true)));
            throw new InvalidOperationException("expected runtime failure");
        }
        catch (IOException error)
        {
            ServerFailureDiagnostics.RecordRuntime(logger, error);
        }
        Check.True(logger.WaitUntilIdle(TimeSpan.FromSeconds(2)), "runtime diagnosis is flushed");
        var output = sink.ToString();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var document = JsonDocument.Parse(lines[0]);
        var fields = document.RootElement;
        Check.Equal("runtime_failure", fields.GetProperty("event").GetString()!,
            "runtime failure is distinct from startup failure");
        Check.Equal("ioexception", fields.GetProperty("exception_type").GetString()!,
            "original runtime fault survives sibling cancellation");
        Check.True(lines.Length is >= 2 and <= 9 && output.Contains("runtime_failure_frame") &&
            output.Contains("failworldasync", StringComparison.OrdinalIgnoreCase),
            "bounded runtime method stack survives the host failure boundary");
        Check.True(!output.Contains("secret-value") && !output.Contains("private_data") &&
            !output.Contains("private-endpoint") && !output.Contains("C:\\"),
            "runtime diagnostics omit message data, SQL, connection details, and paths");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FailWorldAsync()
    {
        await Task.Yield();
        throw new IOException("Host=private-endpoint; password=secret-value; SELECT private_data; C:\\private");
    }
}
