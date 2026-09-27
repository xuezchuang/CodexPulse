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
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    public UsageSnapshot? ReadLatest()
    {
        var codexPath = FindCodexExecutable();
        return codexPath is null
            ? null
            : ReadLatestAsync(CreateStartInfo(codexPath)).GetAwaiter().GetResult();
    }

    internal static async Task<UsageSnapshot?> ReadLatestAsync(
        ProcessStartInfo startInfo,
        TimeSpan? requestTimeout = null,
        TimeSpan? shutdownTimeout = null)
    {
        using var process = new Process { StartInfo = startInfo };
        using var outputCancellation = new CancellationTokenSource();
        var started = false;
        Task errorDrain = Task.CompletedTask;

        try
        {
            started = process.Start();
            if (!started)
            {
                return null;
            }

            // Drain stderr without retaining logs or blocking the child on a full pipe.
            errorDrain = DrainAsync(process.StandardError, outputCancellation.Token);
            using var timeout = new CancellationTokenSource(requestTimeout ?? RequestTimeout);
            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"codex_pulse\",\"title\":\"CodexPulse\",\"version\":\"1.0.0\"}}}".AsMemory(),
                timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);

            var initialization = await ReadResponseAsync(process.StandardOutput, 1, timeout.Token);
            if (initialization is null || !HasSuccessfulResult(initialization))
            {
                return null;
            }

            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"initialized\",\"params\":{}}".AsMemory(), timeout.Token);
            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"account/rateLimits/read\",\"id\":2}".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);

            var response = await ReadResponseAsync(process.StandardOutput, 2, timeout.Token);
            return response is null ? null : TryParseRateLimitsResponse(response);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        {
            return null;
        }
        finally
        {
            // A failed Start has no process handle; even HasExited would throw in that case.
            if (started)
            {
                var outputDrain = DrainAsync(process.StandardOutput, outputCancellation.Token);
                try
                {
                    await StopProcessAsync(process, shutdownTimeout ?? ShutdownTimeout);
                }
                finally
                {
                    outputCancellation.Cancel();
                    await Task.WhenAll(outputDrain, errorDrain);
                }
            }
        }
    }

    private static async Task<string?> ReadResponseAsync(
        StreamReader output, int expectedId, CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken) is { } line)
        {
            if (HasResponseId(line, expectedId))
            {
                return line;
            }
        }

        return null;
    }

    private static bool HasSuccessfulResult(string jsonLine)
    {
        using var document = JsonDocument.Parse(jsonLine);
        return document.RootElement.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty("error", out _);
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) != 0)
            {
            }
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or InvalidOperationException)
        {
            // Pipes may close while the owned process is exiting.
        }
    }

    private static async Task StopProcessAsync(Process process, TimeSpan shutdownTimeout)
    {
        try
        {
            // EOF closes the stdio connection and lets app-server shut itself down.
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // A crashed server may already have closed its input pipe.
        }

        try
        {
            using var gracePeriod = new CancellationTokenSource(shutdownTimeout);
            try
            {
                await process.WaitForExitAsync(gracePeriod.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                // Fall back only when this query's server does not honor EOF.
            }

            if (!process.HasExited)
            {
                process.Kill();
            }

            using var killTimeout = new CancellationTokenSource(shutdownTimeout);
            await process.WaitForExitAsync(killTimeout.Token);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or
            OperationCanceledException)
        {
            // Exit races must not turn a completed usage query into a widget failure.
        }
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
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt32(out var parsedId) &&
                parsedId == expectedId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string codexPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codexPath,
            // Do not inherit a repository as the app-server's working directory.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("app-server");
        foreach (var feature in new[] { "plugins", "apps", "code_mode_host" })
        {
            // Per-process overrides: quota reads need no plugin Git sync, apps, or code host.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"features.{feature}=false");
        }

        return startInfo;
    }

    private static string? FindCodexExecutable()
    {
        return FindCodexExecutable(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
    }

    internal static string? FindCodexExecutable(string localAppData, string appData, string pathValue)
    {
        var desktopBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            // Desktop updates live in versioned subdirectories. The root exe may be months old.
            if (Directory.Exists(desktopBin))
            {
                var currentDesktop = Directory.EnumerateDirectories(desktopBin)
                    .Select(directory => new FileInfo(Path.Combine(directory, "codex.exe")))
                    .Where(file => file.Exists)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (currentDesktop is not null)
                {
                    return currentDesktop.FullName;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Continue with a native CLI installation if the desktop directory is unavailable.
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "codex.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Resolve npm's native binary directly, avoiding a cmd/node wrapper and extra process tree.
        var npmPackage = Path.Combine(appData, "npm", "node_modules", "@openai", "codex");
        var target = Environment.Is64BitOperatingSystem &&
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                System.Runtime.InteropServices.Architecture.Arm64
            ? "aarch64-pc-windows-msvc"
            : "x86_64-pc-windows-msvc";
        var platformPackage = target.StartsWith("aarch64", StringComparison.Ordinal)
            ? "codex-win32-arm64"
            : "codex-win32-x64";
        foreach (var vendor in new[]
        {
            Path.Combine(npmPackage, "node_modules", "@openai", platformPackage, "vendor"),
            Path.Combine(npmPackage, "vendor")
        })
        {
            foreach (var nativeDirectory in new[] { "bin", "codex" })
            {
                var candidate = Path.Combine(vendor, target, nativeDirectory, "codex.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var legacyDesktop = Path.Combine(desktopBin, "codex.exe");
        return File.Exists(legacyDesktop) ? legacyDesktop : null;
    }
}
