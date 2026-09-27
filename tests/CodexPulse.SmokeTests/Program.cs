using CodexPulse;
using System.Diagnostics;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--fake-server")
{
    return await RunFakeServerAsync(args[1], args[2]);
}

var response =
    """{"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":18,"windowDurationMins":10080,"resetsAt":1786160131},"planType":"pro"},"rateLimitsByLimitId":{"codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1786178543},"secondary":null,"planType":"pro"},"codex":{"limitId":"codex","limitName":null,"primary":{"usedPercent":18,"windowDurationMins":10080,"resetsAt":1786160131},"secondary":null,"planType":"pro"}}}}""";

var snapshot = UsageReader.TryParseRateLimitsResponse(response) ??
    throw new InvalidOperationException("Expected an App Server usage snapshot.");
Assert(snapshot.LimitId == "codex", "The ordinary Codex limit should be selected.");
Assert(snapshot.LimitName == "Codex", "The ordinary Codex display name is incorrect.");
Assert(snapshot.UsedPercent == 18, "The ordinary Codex percentage is incorrect.");
Assert(
    snapshot.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1786160131),
    "The ordinary Codex reset time is incorrect.");

var sparkOnlyResponse =
    """{"id":2,"result":{"rateLimitsByLimitId":{"codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":42,"resetsAt":1786160131},"secondary":null,"planType":"pro"}}}}""";
Assert(
    UsageReader.TryParseRateLimitsResponse(sparkOnlyResponse) is null,
    "A Spark-only response must not be displayed.");

Assert(
    UsageReader.TryParseRateLimitsResponse("{\"id\":2,\"result\":{}}") is null,
    "A response without rate-limit buckets must not produce a snapshot.");

await VerifyProcessLifecycleAsync();
VerifyExecutableSelection();

if (string.Equals(
        Environment.GetEnvironmentVariable("CODEXPULSE_LIVE_TEST"),
        "1",
        StringComparison.Ordinal))
{
    var liveSnapshot = new UsageReader().ReadLatest() ??
        throw new InvalidOperationException("The live Codex App Server request returned no usage data.");
    Assert(liveSnapshot.UsedPercent is >= 0 and <= 100, "Live usage must stay in the 0-100 range.");
    var liveReset = liveSnapshot.ResetsAt ??
        throw new InvalidOperationException("Live usage must include a reset time.");
    Console.WriteLine(
        $"LIVE: {liveSnapshot.LimitName}, {100 - liveSnapshot.UsedPercent:0.#}% left, " +
        $"resets {liveReset.ToLocalTime():yyyy-MM-dd HH:mm:ss}.");
}

using var systemReader = new SystemMonitorReader();
_ = systemReader.Sample();
Thread.Sleep(1100);
var systemSnapshot = systemReader.Sample();
Assert(systemSnapshot.UploadBytesPerSecond >= 0, "Upload speed must not be negative.");
Assert(systemSnapshot.DownloadBytesPerSecond >= 0, "Download speed must not be negative.");
Assert(
    systemSnapshot.CpuPercent is >= 0 and <= 100,
    "CPU percentage must stay in the 0-100 range.");
Assert(
    systemSnapshot.MemoryPercent is > 0 and <= 100,
    "Memory percentage must stay in the 0-100 range.");
Assert(
    systemSnapshot.CpuTemperatureCelsius is null or (>= 0 and <= 125),
    "CPU temperature must be absent or remain in a plausible range.");

var temperatureText = systemSnapshot.CpuTemperatureCelsius is { } temperature
    ? $"{temperature:0.0}°C"
    : "unavailable";
Console.WriteLine($"PASS: App Server limits and system sampling ({temperatureText}).");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}


static async Task VerifyProcessLifecycleAsync()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "CodexPulse-process-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    try
    {
        var missing = UsageReader.CreateStartInfo(Path.Combine(tempRoot, "missing-codex.exe"));
        Assert(await UsageReader.ReadLatestAsync(missing) is null,
            "A failed process start must return no snapshot without throwing during cleanup.");

        foreach (var mode in new[] { "success", "initialize-error", "timeout", "ignore-eof" })
        {
            var marker = Path.Combine(tempRoot, mode + ".json");
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // Also support invocation as `dotnet CodexPulse.SmokeTests.dll`.
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            startInfo.ArgumentList.Add("--fake-server");
            startInfo.ArgumentList.Add(mode);
            startInfo.ArgumentList.Add(marker);

            var timer = Stopwatch.StartNew();
            var result = await UsageReader.ReadLatestAsync(startInfo,
                requestTimeout: TimeSpan.FromSeconds(mode == "timeout" ? 2 : 10),
                shutdownTimeout: TimeSpan.FromSeconds(2));
            Assert((result is not null) == (mode is "success" or "ignore-eof"),
                $"Unexpected query result for {mode}.");
            Assert(File.Exists(marker), $"The {mode} server did not receive EOF.");
            using var exit = JsonDocument.Parse(File.ReadAllText(marker));
            var pid = exit.RootElement.GetProperty("pid").GetInt32();
            try
            {
                using var child = Process.GetProcessById(pid);
                Assert(child.HasExited, $"The {mode} server was left running.");
            }
            catch (ArgumentException)
            {
                // Exited processes no longer have an entry in the process table.
            }
            Assert(timer.Elapsed < TimeSpan.FromSeconds(15), $"Unbounded cleanup for {mode}.");
            Assert(exit.RootElement.GetProperty("queryReceived").GetBoolean() ==
                (mode is "success" or "ignore-eof"),
                $"A quota request was sent before successful initialization for {mode}.");
            Console.WriteLine($"PASS: process lifecycle ({mode}).");
        }
    }
    finally
    {
        DeleteTestDirectory(tempRoot);
    }
}

static async Task<int> RunFakeServerAsync(string mode, string marker)
{
    var queryReceived = false;
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var request = JsonDocument.Parse(line);
        switch (request.RootElement.GetProperty("method").GetString())
        {
            case "initialize":
                // Noise and server-originated requests must not be mistaken for our responses.
                Console.WriteLine("not json");
                Console.WriteLine("[]");
                Console.WriteLine("{\"id\":\"server-request\",\"method\":\"notice\"}");
                if (mode != "timeout")
                {
                    Console.WriteLine(mode == "initialize-error"
                        ? "{\"id\":1,\"error\":{\"code\":-1,\"message\":\"test\"}}"
                        : "{\"id\":1,\"result\":{}}");
                }
                break;
            case "account/rateLimits/read":
                queryReceived = true;
                Console.WriteLine("{\"id\":2,\"result\":{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":25}}}}}");
                break;
        }
    }

    // Exercise output draining while the parent waits for a graceful exit.
    Console.Out.Write(new string('o', 128 * 1024));
    Console.Error.Write(new string('e', 128 * 1024));
    await Console.Out.FlushAsync();
    await Console.Error.FlushAsync();
    File.WriteAllText(marker, JsonSerializer.Serialize(new { pid = Environment.ProcessId, queryReceived }));
    await Task.Delay(mode == "ignore-eof" ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(200));
    return 0;
}

static void VerifyExecutableSelection()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "CodexPulse-path-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var desktopBin = Path.Combine(tempRoot, "OpenAI", "Codex", "bin");
        var oldVersion = Path.Combine(desktopBin, "old-version", "codex.exe");
        var newVersion = Path.Combine(desktopBin, "new-version", "codex.exe");
        var legacy = Path.Combine(desktopBin, "codex.exe");
        foreach (var candidate in new[] { oldVersion, newVersion, legacy })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
            File.WriteAllText(candidate, string.Empty);
        }
        File.SetLastWriteTimeUtc(oldVersion, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newVersion, DateTime.UtcNow.AddDays(-1));
        Assert(UsageReader.FindCodexExecutable(tempRoot, tempRoot, desktopBin) == newVersion,
            "The newest versioned desktop binary must win even when the legacy exe has a newer timestamp.");
        File.Delete(newVersion);
        Assert(UsageReader.FindCodexExecutable(tempRoot, tempRoot, string.Empty) == oldVersion,
            "Missing versioned binaries must be skipped.");
        File.Delete(oldVersion);
        Assert(UsageReader.FindCodexExecutable(tempRoot, tempRoot, string.Empty) == legacy,
            "An older desktop layout must remain usable when no newer binary exists.");
        File.Delete(legacy);
        Assert(UsageReader.FindCodexExecutable(tempRoot, tempRoot, string.Empty) is null,
            "No installation should return no executable.");
        var target = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64
            ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        var package = target.StartsWith("aarch64", StringComparison.Ordinal)
            ? "codex-win32-arm64" : "codex-win32-x64";
        var npmBase = Path.Combine(tempRoot, "npm", "node_modules", "@openai", "codex");
        var npmExe = Path.Combine(npmBase, "node_modules", "@openai", package, "vendor", target, "bin", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(npmExe)!);
        File.WriteAllText(npmExe, string.Empty);
        Assert(UsageReader.FindCodexExecutable(tempRoot, tempRoot, string.Empty) == npmExe,
            "The current npm native binary layout must be found without running codex.cmd.");
        File.Delete(npmExe);
        var oldNpmExe = Path.Combine(npmBase, "vendor", target, "codex", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldNpmExe)!);
        File.WriteAllText(oldNpmExe, string.Empty);
        Assert(UsageReader.FindCodexExecutable(tempRoot, tempRoot, string.Empty) == oldNpmExe,
            "The legacy npm native binary layout must remain usable.");
        Console.WriteLine("PASS: native Codex executable selection.");
    }
    finally
    {
        DeleteTestDirectory(tempRoot);
    }
}

static void DeleteTestDirectory(string directory)
{
    var fullPath = Path.GetFullPath(directory);
    Assert(string.Equals(Path.GetDirectoryName(fullPath),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
        StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(fullPath).StartsWith("CodexPulse-", StringComparison.Ordinal),
        "Cleanup must stay inside the test's temporary directory.");
    Directory.Delete(fullPath, recursive: true);
}
