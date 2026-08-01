using CodexPulse;

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
