using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CodexPulse;

public sealed record LimitSnapshot(
    string Name,
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt);

public sealed record UsageSnapshot(
    DateTimeOffset SourceTimestamp,
    string SourceFile,
    string? PlanType,
    string? LimitId,
    IReadOnlyList<LimitSnapshot> Limits,
    LimitSnapshot SelectedLimit);

public sealed class UsageReader
{
    private const int InitialFileLimit = 100;
    private const int RefreshFileLimit = 30;
    private const int TailByteLimit = 6 * 1024 * 1024;

    private readonly object _sync = new();
    private bool _initialized;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private UsageSnapshot? _cachedSnapshot;

    public UsageReader(string? sessionRoot = null)
    {
        SessionRoot = sessionRoot ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "sessions");
    }

    public string SessionRoot { get; }

    public UsageSnapshot? ReadLatest()
    {
        lock (_sync)
        {
            if (!Directory.Exists(SessionRoot))
            {
                _initialized = true;
                _lastScanUtc = DateTime.UtcNow;
                return _cachedSnapshot;
            }

            var scanStartedUtc = DateTime.UtcNow;
            var files = EnumerateCandidateFiles()
                .Where(file => !_initialized || file.LastWriteTimeUtc >= _lastScanUtc.AddSeconds(-5))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(_initialized ? RefreshFileLimit : InitialFileLimit)
                .ToArray();

            foreach (var file in files)
            {
                var candidate = ReadLatestFromFile(file.FullName);
                if (candidate is not null &&
                    string.Equals(candidate.LimitId, "codex", StringComparison.Ordinal) &&
                    (_cachedSnapshot is null || candidate.SourceTimestamp > _cachedSnapshot.SourceTimestamp))
                {
                    _cachedSnapshot = candidate;
                }
            }

            _initialized = true;
            _lastScanUtc = scanStartedUtc;
            return _cachedSnapshot;
        }
    }

    private IEnumerable<FileInfo> EnumerateCandidateFiles()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(SessionRoot, "*.jsonl", options);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in paths)
        {
            FileInfo file;
            try
            {
                file = new FileInfo(path);
                _ = file.LastWriteTimeUtc;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            yield return file;
        }
    }

    private static UsageSnapshot? ReadLatestFromFile(string path)
    {
        UsageSnapshot? latest = null;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - TailByteLimit);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true);

            if (offset > 0)
            {
                _ = reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal) ||
                    !line.Contains("\"token_count\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var snapshot = TryParseSnapshot(line, path);
                if (snapshot is not null &&
                    (latest is null || snapshot.SourceTimestamp > latest.SourceTimestamp))
                {
                    latest = snapshot;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return latest;
    }

    internal static UsageSnapshot? TryParseSnapshot(string jsonLine, string sourceFile)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (!TryGetString(root, "type", out var eventType) ||
                eventType != "event_msg" ||
                !root.TryGetProperty("payload", out var payload) ||
                !TryGetString(payload, "type", out var payloadType) ||
                payloadType != "token_count" ||
                !payload.TryGetProperty("rate_limits", out var rateLimits) ||
                rateLimits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var limits = new List<LimitSnapshot>(2);
            TryAddLimit(rateLimits, "primary", "主窗口", limits);
            TryAddLimit(rateLimits, "secondary", "次窗口", limits);
            if (limits.Count == 0)
            {
                return null;
            }

            var timestamp = DateTimeOffset.MinValue;
            if (TryGetString(root, "timestamp", out var timestampText))
            {
                _ = DateTimeOffset.TryParse(timestampText, out timestamp);
            }
            if (timestamp == DateTimeOffset.MinValue)
            {
                timestamp = File.GetLastWriteTimeUtc(sourceFile);
            }

            var selected = limits
                .OrderByDescending(limit => limit.UsedPercent)
                .ThenByDescending(limit => limit.WindowMinutes ?? 0)
                .First();

            _ = TryGetString(rateLimits, "plan_type", out var planType);
            _ = TryGetString(rateLimits, "limit_id", out var limitId);

            return new UsageSnapshot(
                timestamp,
                sourceFile,
                planType,
                limitId,
                limits,
                selected);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryAddLimit(
        JsonElement rateLimits,
        string propertyName,
        string displayName,
        ICollection<LimitSnapshot> limits)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var limit) ||
            limit.ValueKind != JsonValueKind.Object ||
            !limit.TryGetProperty("used_percent", out var usedPercentElement) ||
            !usedPercentElement.TryGetDouble(out var usedPercent))
        {
            return;
        }

        int? windowMinutes = null;
        if (limit.TryGetProperty("window_minutes", out var windowElement) &&
            windowElement.TryGetInt32(out var parsedWindowMinutes))
        {
            windowMinutes = parsedWindowMinutes;
        }

        DateTimeOffset? resetsAt = null;
        if (limit.TryGetProperty("resets_at", out var resetElement) &&
            resetElement.TryGetInt64(out var unixSeconds))
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetsAt = null;
            }
        }

        limits.Add(new LimitSnapshot(
            displayName,
            Math.Clamp(usedPercent, 0, 100),
            windowMinutes,
            resetsAt));
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }
}
