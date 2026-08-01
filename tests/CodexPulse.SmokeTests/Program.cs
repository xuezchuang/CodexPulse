using CodexPulse;

var temporaryRoot = Path.Combine(Path.GetTempPath(), $"CodexPulseTests-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(temporaryRoot, "2026", "07", "31"));

try
{
    var olderPath = Path.Combine(temporaryRoot, "2026", "07", "31", "older.jsonl");
    var newerPath = Path.Combine(temporaryRoot, "2026", "07", "31", "newer.jsonl");
    var alternateLimitPath = Path.Combine(temporaryRoot, "2026", "07", "31", "alternate.jsonl");

    File.WriteAllLines(
        olderPath,
        new[]
        {
            """{"timestamp":"2026-07-31T01:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":40,"window_minutes":300,"resets_at":1785463200},"secondary":null,"plan_type":"pro"}}}"""
        });
    File.WriteAllLines(
        newerPath,
        new[]
        {
            """{"timestamp":"2026-07-31T02:00:00Z","type":"response_item","payload":{"type":"message","text":"rate_limits token_count"}}""",
            """{"timestamp":"2026-07-31T03:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":55,"window_minutes":300,"resets_at":1785470400},"secondary":{"used_percent":77,"window_minutes":10080,"resets_at":1785902975},"plan_type":"pro"}}}"""
        });
    File.WriteAllLines(
        alternateLimitPath,
        new[]
        {
            """{"timestamp":"2026-07-31T03:30:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex_bengalfox","primary":{"used_percent":0,"window_minutes":10080,"resets_at":1785936699},"secondary":null,"plan_type":"pro"}}}"""
        });

    File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddMinutes(-1));
    File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);

    var reader = new UsageReader(temporaryRoot);
    var snapshot = reader.ReadLatest() ?? throw new InvalidOperationException("Expected a usage snapshot.");

    Assert(snapshot.SourceTimestamp == DateTimeOffset.Parse("2026-07-31T03:00:00Z"), "Newest event was not selected.");
    Assert(snapshot.Limits.Count == 2, "Both limit windows should be parsed.");
    Assert(snapshot.SelectedLimit.UsedPercent == 77, "The most constrained window should be selected.");
    Assert(snapshot.SelectedLimit.WindowMinutes == 10080, "Selected window metadata is incorrect.");
    Assert(snapshot.LimitId == "codex", "Non-codex limit IDs should be ignored.");

    File.AppendAllLines(
        newerPath,
        new[]
        {
            """{"timestamp":"2026-07-31T04:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":80,"window_minutes":10080,"resets_at":1785902975},"secondary":null,"plan_type":"pro"}}}"""
        });
    File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow.AddSeconds(1));

    snapshot = reader.ReadLatest() ?? throw new InvalidOperationException("Expected a refreshed usage snapshot.");
    Assert(snapshot.SourceTimestamp == DateTimeOffset.Parse("2026-07-31T04:00:00Z"), "Changed file was not refreshed.");
    Assert(snapshot.SelectedLimit.UsedPercent == 80, "Refreshed percentage is incorrect.");

    var concurrentWriteTime = DateTime.UtcNow.AddSeconds(3);
    for (var index = 0; index < 35; index++)
    {
        var concurrentPath = Path.Combine(temporaryRoot, "2026", "07", "31", $"concurrent-{index:00}.jsonl");
        File.WriteAllLines(
            concurrentPath,
            new[]
            {
                """{"timestamp":"2026-07-31T04:30:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":81,"window_minutes":10080,"resets_at":1785902975},"secondary":null,"plan_type":"pro"}}}"""
            });
        File.SetLastWriteTimeUtc(concurrentPath, concurrentWriteTime);
    }

    var latestLimitPath = Path.Combine(temporaryRoot, "2026", "07", "31", "latest-limit.jsonl");
    File.WriteAllLines(
        latestLimitPath,
        new[]
        {
            """{"timestamp":"2026-07-31T05:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":82,"window_minutes":10080,"resets_at":1785902975},"secondary":null,"plan_type":"pro"}}}"""
        });
    File.SetLastWriteTimeUtc(latestLimitPath, concurrentWriteTime.AddSeconds(-1));

    snapshot = reader.ReadLatest() ?? throw new InvalidOperationException("Expected a concurrent refresh snapshot.");
    Assert(
        snapshot.SourceTimestamp == DateTimeOffset.Parse("2026-07-31T05:00:00Z"),
        "Refresh must inspect every changed session file, even when more than 30 files changed.");
    Assert(snapshot.SelectedLimit.UsedPercent == 82, "Latest concurrent limit percentage is incorrect.");

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
    Console.WriteLine($"PASS: Codex limits, refresh caching, network, CPU, memory, and temperature sampling ({temperatureText}).");
    return 0;
}
finally
{
    Directory.Delete(temporaryRoot, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
