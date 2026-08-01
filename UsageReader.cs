using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPulse;

public sealed record UsageSnapshot(
    DateTimeOffset SourceTimestamp,
    string? PlanType,
    string LimitId,
    string LimitName,
    double UsedPercent,
    DateTimeOffset? ResetsAt);

public sealed class UsageReader
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public UsageSnapshot? ReadLatest()
    {
        return ReadLatestAsync().GetAwaiter().GetResult();
    }

    private static async Task<UsageSnapshot?> ReadLatestAsync()
    {
        var codexPath = FindCodexExecutable();
        if (codexPath is null)
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(codexPath),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return null;
            }

            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();

            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"codex_pulse\",\"title\":\"CodexPulse\",\"version\":\"1.0.0\"}}}");
            await process.StandardInput.FlushAsync();

            using var timeout = new CancellationTokenSource(RequestTimeout);
            while (!timeout.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                if (!HasResponseId(line, 1))
                {
                    continue;
                }

                await process.StandardInput.WriteLineAsync(
                    "{\"method\":\"initialized\",\"params\":{}}");
                await process.StandardInput.WriteLineAsync(
                    "{\"method\":\"account/rateLimits/read\",\"id\":2}");
                await process.StandardInput.FlushAsync();
                break;
            }

            while (!timeout.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                if (HasResponseId(line, 2))
                {
                    return TryParseRateLimitsResponse(line);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        {
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The short-lived app-server may exit between the checks above.
                }
            }
        }

        return null;
    }

    internal static UsageSnapshot? TryParseRateLimitsResponse(string jsonLine)
    {
        using var document = JsonDocument.Parse(jsonLine);
        var root = document.RootElement;
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rateLimitsByLimitId", out var buckets) ||
            buckets.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var snapshots = new List<UsageSnapshot>();
        foreach (var bucketProperty in buckets.EnumerateObject())
        {
            if (bucketProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var bucket = bucketProperty.Value;
            var limitId = GetString(bucket, "limitId") ?? bucketProperty.Name;
            var limitName = GetString(bucket, "limitName") ??
                (string.Equals(limitId, "codex", StringComparison.Ordinal) ? "Codex" : limitId);
            var planType = GetString(bucket, "planType");

            var windows = new List<UsageSnapshot>(2);
            TryAddWindow(bucket, "primary", limitId, limitName, planType, windows);
            TryAddWindow(bucket, "secondary", limitId, limitName, planType, windows);
            if (windows.Count > 0)
            {
                snapshots.Add(windows
                    .OrderByDescending(snapshot => snapshot.UsedPercent)
                    .First());
            }
        }

        if (snapshots.Count == 0)
        {
            return null;
        }

        return snapshots.FirstOrDefault(
            snapshot => string.Equals(snapshot.LimitId, "codex", StringComparison.Ordinal));
    }

    private static void TryAddWindow(
        JsonElement bucket,
        string propertyName,
        string limitId,
        string limitName,
        string? planType,
        ICollection<UsageSnapshot> snapshots)
    {
        if (!bucket.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var usedPercentElement) ||
            !usedPercentElement.TryGetDouble(out var usedPercent))
        {
            return;
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetsAtElement) &&
            resetsAtElement.TryGetInt64(out var unixSeconds))
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

        snapshots.Add(new UsageSnapshot(
            DateTimeOffset.Now,
            planType,
            limitId,
            limitName,
            Math.Clamp(usedPercent, 0, 100),
            resetsAt));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static bool HasResponseId(string jsonLine, int expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            return document.RootElement.TryGetProperty("id", out var id) &&
                id.TryGetInt32(out var parsedId) &&
                parsedId == expectedId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string codexPath)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (string.Equals(Path.GetExtension(codexPath), ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"{codexPath}\" app-server");
        }
        else
        {
            startInfo.FileName = codexPath;
            startInfo.ArgumentList.Add("app-server");
        }

        return startInfo;
    }

    private static string? FindCodexExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var knownPaths = new[]
        {
            Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(appData, "npm", "codex.cmd")
        };

        foreach (var path in knownPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalizedDirectory = directory.Trim().Trim('"');
            foreach (var fileName in new[] { "codex.exe", "codex.cmd" })
            {
                var candidate = Path.Combine(normalizedDirectory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
