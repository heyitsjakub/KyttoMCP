using System.Diagnostics;
using Kytto.Core.Health;
using Kytto.Core.Settings;
using static Kytto.Core.Tests.ScriptedTransport;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Tests;

/// <summary>Writes a throwaway Python server script and finds python.exe on PATH.</summary>
internal static class PythonScript
{
    public static string? Python =>
        Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory =>
            {
                try { return Path.Combine(directory.Trim('"'), "python.exe"); }
                catch (ArgumentException) { return null; }
            })
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));

    public static IReadOnlyDictionary<string, string> Environment_() =>
        Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>What the check would report: the failure reason under the server's stderr.</summary>
    public static string Reason(HealthFailure failure) => failure.Underlying switch
    {
        ProcessException process => process.Failure.Reason,
        McpException mcp => mcp.Failure.Reason,
        var other => other.GetType().Name,
    };

    public static string Write(string directory, string source)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"server-{Guid.NewGuid():N}.py");
        File.WriteAllText(path, source);
        return path;
    }
}

/// <summary>
/// AUD-01: a JSON-RPC error code is a number the server chose. On macOS converting
/// one that did not fit trapped and took the app down. On Windows the cast saturates
/// instead of throwing, which is quieter and still wrong: <c>1e100</c> became
/// <c>int.MaxValue</c> and <c>-32601.5</c> "method not found". Either way the answer
/// is a protocol failure, not a number.
/// </summary>
public sealed class ErrorCodeConversionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kytto-aud01-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    public static TheoryData<string> InvalidCodes =>
        ["1e100", "-1e100", "1.5", "1e400", "9223372036854775808", "-32601.5", "2147483648"];

    [Theory]
    [InlineData("-32601", -32_601)]
    [InlineData("0", 0)]
    [InlineData("1e3", 1_000)]
    [InlineData("-0", 0)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("1.5", null)]
    [InlineData("-32601.5", null)]
    [InlineData("1e100", null)]
    [InlineData("-1e100", null)]
    [InlineData("1e400", null)]
    [InlineData("-1e400", null)]
    [InlineData("2147483648", null)]
    [InlineData("9223372036854775808", null)]
    public void IntegerValueAcceptsOnlyExactIntegersThatFit(string text, int? expected) =>
        Assert.Equal(expected, JsonDocument.Parse("{\"code\":" + text + "}").Root["code"]!.IntegerValue);

    [Theory]
    [MemberData(nameof(InvalidCodes))]
    public async Task ARemoteServersOutOfRangeCodeIsAProtocolFailure(string code)
    {
        var transport = new ScriptedTransport(Reply(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":" + code + ",\"message\":\"fixture\"}}"));
        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient(transport).ListToolsAsync("https://mcp.example/mcp"));
        Assert.Equal(RemoteHttpFailure.Protocol, failure.Failure);
    }

    [Theory]
    [MemberData(nameof(InvalidCodes))]
    public async Task AnOutOfRangeCodeInAnHttpErrorBodyIsIgnoredNotConverted(string code)
    {
        var transport = new ScriptedTransport(Reply(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":" + code + ",\"message\":\"fixture\"}}",
            400));
        await Assert.ThrowsAnyAsync<Exception>(
            () => new McpHttpClient(transport).ListToolsAsync("https://mcp.example/mcp"));
    }

    [Theory]
    [MemberData(nameof(InvalidCodes))]
    public void ALocalServersOutOfRangeCodeIsAProtocolError(string code)
    {
        Assert.True(PythonScript.Python is not null, "These tests need python.exe on PATH.");
        var script = PythonScript.Write(_directory, $$$"""
            import sys, json
            for line in sys.stdin:
                request = json.loads(line)
                if "id" in request:
                    print('{"jsonrpc":"2.0","id":' + json.dumps(request["id"]) + ',"error":{"code":{{{code}}},"message":"fixture"}}', flush=True)
            """);

        var failure = Assert.Throws<HealthFailure>(() => new McpStdioClient().ListTools(
            PythonScript.Python!, [script], PythonScript.Environment_(), TimeSpan.FromSeconds(10)));
        Assert.Equal("protocolError", PythonScript.Reason(failure));
    }
}

/// <summary>AUD-04 and AUD-05.</summary>
[Collection("process-timing")]
public sealed class HealthCheckBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kytto-aud04-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The server sends a request with a 256 KB id, so Kytto's answer fills the pipe,
    /// then never reads and never exits on its own. A blocking write had no deadline:
    /// the check waited for the server's sleep to end.
    /// </summary>
    [Fact]
    public void AServerThatStopsReadingCannotHoldACheckPastItsDeadline()
    {
        Assert.True(PythonScript.Python is not null, "These tests need python.exe on PATH.");
        var pidFile = Path.Combine(_directory, "pid.txt");
        var script = PythonScript.Write(_directory, """
            import sys, json, os, time
            open(sys.argv[1], "w").write(str(os.getpid()))
            print(json.dumps({"jsonrpc": "2.0", "id": "x" * 262144, "method": "ping"}), flush=True)
            time.sleep(60)
            """);

        var clock = Stopwatch.StartNew();
        var failure = Assert.Throws<HealthFailure>(() => new McpStdioClient().ListTools(
            PythonScript.Python!, [script, pidFile], PythonScript.Environment_(), TimeSpan.FromSeconds(1)));
        clock.Stop();

        Assert.Equal("timedOut", PythonScript.Reason(failure));
        // One second of check, the documented shutdown grace (200 ms for the server
        // to exit on its own, then the tree is killed), and slack for starting Python.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the check took {clock.Elapsed.TotalSeconds:F1} s");

        // The whole tree is gone, not just abandoned.
        var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        var gone = false;
        for (var attempt = 0; attempt < 20 && !gone; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                gone = process.HasExited;
            }
            catch (ArgumentException)
            {
                gone = true;
            }
            if (!gone) Thread.Sleep(100);
        }
        Assert.True(gone, "the server was left running");
    }

    /// <summary>
    /// AUD-05 found on macOS that the first measurement could be written before
    /// anything had created Kytto's folder privately. On Windows <c>MetadataStore</c>
    /// already creates the root owner-only before staging any bytes; this pins it.
    /// </summary>
    [Fact]
    public void TheFirstMeasurementIsWrittenOwnerOnlyFolderIncluded()
    {
        var root = Path.Combine(_directory, "Kytto");
        Assert.False(Directory.Exists(root));
        var paths = new KyttoPaths(root);
        var health = new HealthResult(
            HealthStatus.Failed, DateTimeOffset.UtcNow, null, [], "fixture",
            "Bearer placeholder-token", 0, null, null);

        new MetadataStore(paths).Record("svc", health, null);

        Assert.True(KyttoStorage.IsPrivate(root), "the data folder grants someone else access");
        Assert.True(KyttoStorage.IsPrivate(paths.MetadataFile), "the measurements file grants someone else access");
        // The diagnostic itself is kept.
        Assert.Contains("placeholder-token", File.ReadAllText(paths.MetadataFile), StringComparison.Ordinal);
        // No staged temporary file is left beside it.
        Assert.Equal([paths.MetadataFile], Directory.GetFiles(root));
    }
}
