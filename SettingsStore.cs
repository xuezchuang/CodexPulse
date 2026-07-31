using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexPulse;

public sealed class WidgetSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public static class SettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexPulse");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static WidgetSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new WidgetSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<WidgetSettings>(json) ?? new WidgetSettings();
        }
        catch (JsonException)
        {
            return new WidgetSettings();
        }
        catch (IOException)
        {
            return new WidgetSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new WidgetSettings();
        }
    }

    public static void Save(WidgetSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            // Position persistence is optional; the widget should continue running.
        }
        catch (UnauthorizedAccessException)
        {
            // Position persistence is optional; the widget should continue running.
        }
    }
}

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexPulse";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value &&
                   !string.IsNullOrWhiteSpace(value);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            }
        }
        catch (System.Security.SecurityException)
        {
            // The menu will reflect the actual registry state on its next opening.
        }
        catch (UnauthorizedAccessException)
        {
            // The menu will reflect the actual registry state on its next opening.
        }
    }
}
