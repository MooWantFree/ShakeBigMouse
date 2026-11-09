using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ShakeToFindCursor;

public class AppSettings
{
    public string Language { get; set; } = Localization.UiText.SystemLanguage;
    public int MinimumShakeDistance { get; set; } = 1283; // accumulated logical pixels
    public int ShakeWindowMs { get; set; } = 350;
    public int ShakeSensitivityPercent { get; set; } = 40;

    // Maximum cursor magnification reached during a vigorous shake.
    public double MagnificationFactor { get; set; } = 10.0;

    // Independent of the hold duration. New installs use proportional enlargement.
    public bool ScaleWithShakeStrength { get; set; } = true;

    // How long the cursor stays at its reached size after shaking stops.
    public double MaxSizeHoldSeconds { get; set; }

    private double _commandLineDurationSeconds = 1;
    public double CommandLineDurationSeconds
    {
        get => _commandLineDurationSeconds;
        set => _commandLineDurationSeconds = double.IsFinite(value) ? Math.Clamp(value, 0.5, 30) : 1;
    }

    private const double DefaultShrinkSpeed = 2.5;
    private double _shrinkSpeed = DefaultShrinkSpeed;
    public double ShrinkSpeed
    {
        get => _shrinkSpeed;
        set => _shrinkSpeed = double.IsFinite(value) ? Math.Clamp(value, 0.5, 3) : DefaultShrinkSpeed;
    }

    // New exclusions store full executable paths; bare process names remain valid for older settings.
    public List<string> ExcludedProcesses { get; set; } = new();
    // Keep each rule's original path when its matching mode is switched to process name.
    public List<string> ProcessNameOnlyExclusions { get; set; } = new();
    public bool DisableInFullscreen { get; set; } = true;

    // System
    public bool RunOnStartup { get; set; } = false;

    // The file's location selects the mode, including for copied or older settings.
    internal bool PortableMode { get; set; } = true;
    private static readonly string PortablePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly string UserPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ShakeToFindCursor",
        "settings.json");

    /// <summary>Creates an independent copy (deep-copies the exclusion list).</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ExcludedProcesses = new List<string>(ExcludedProcesses);
        copy.ProcessNameOnlyExclusions = new List<string>(ProcessNameOnlyExclusions);
        return copy;
    }

    public static AppSettings Load() => Load(PortablePath, UserPath);

    internal static AppSettings Load(string portablePath, string userPath)
    {
        bool portable = !string.Equals(portablePath, userPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(portablePath) || !File.Exists(userPath));
        string path = portable ? portablePath : userPath;
        var settings = new AppSettings();
        if (File.Exists(path))
        {
            try
            {
                settings = FromJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                LogLoadError(ex, path);
            }
        }
        settings.PortableMode = portable;
        return settings;
    }

    public static AppSettings FromJson(string json)
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            !root.TryGetProperty(nameof(MinimumShakeDistance), out _) &&
            root.TryGetProperty("Sensitivity", out var legacy) && legacy.ValueKind == JsonValueKind.Number &&
            legacy.TryGetDouble(out double sensitivity) && double.IsFinite(sensitivity))
            loaded.MinimumShakeDistance = (int)Math.Round(1700 - 1250 * (Math.Clamp(sensitivity, 1, 10) - 1) / 9);
        // Preserve the behavior of configurations written before the mode existed.
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty(nameof(ScaleWithShakeStrength), out _))
            loaded.ScaleWithShakeStrength = loaded.MaxSizeHoldSeconds == 0;
        loaded.Clamp();
        return loaded;
    }

    /// <summary>
    /// Clamps deserialized values to the ranges the UI can produce, so a hand-edited or
    /// corrupt settings file can't push the app into a bad state (e.g. a huge magnification
    /// factor blowing up the cursor frame cache).
    /// </summary>
    private void Clamp()
    {
        Language = Localization.UiText.Normalize(Language);
        MinimumShakeDistance = Math.Clamp(MinimumShakeDistance, 1, 3000);
        ShakeWindowMs = Math.Clamp(ShakeWindowMs, 50, 1500);
        ShakeSensitivityPercent = Math.Clamp(ShakeSensitivityPercent, 1, 100);
        MagnificationFactor = Math.Clamp(MagnificationFactor, 2.0, 10.0);
        MaxSizeHoldSeconds = double.IsFinite(MaxSizeHoldSeconds)
            ? Math.Clamp(MaxSizeHoldSeconds, 0.0, 10.0)
            : 0;
    }

    private static void LogLoadError(Exception ex, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "settings_error.log"), $"{DateTime.Now}\n{ex}\n\n");
            }
        }
        catch { }
    }

    internal void Save(AppSettings previous) => Save(previous, PortablePath, UserPath);

    internal void Save(AppSettings previous, string portablePath, string userPath)
    {
        if (PortableMode && string.Equals(portablePath, userPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Localization.UiText.Current["PortableSameFolder"]);

        string path = PortableMode ? portablePath : userPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool leavingPortable = previous.PortableMode && !PortableMode;
        string? backup = leavingPortable && File.Exists(userPath) ? temporary + ".bak" : null;
        bool changeStartup = RunOnStartup != previous.RunOnStartup;
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (changeStartup && !ApplyStartupSettings(RunOnStartup))
                throw new IOException(Localization.UiText.Current["ErrorStartup"]);
            try
            {
                if (backup != null) File.Replace(temporary, path, backup);
                else File.Move(temporary, path, overwrite: true);
                if (leavingPortable)
                {
                    // Keep the old portable file authoritative until the new user copy is in place.
                    try { File.Delete(portablePath); }
                    catch
                    {
                        if (backup != null) File.Move(backup, path, overwrite: true);
                        else File.Delete(path);
                        throw;
                    }
                }
            }
            catch
            {
                if (changeStartup) ApplyStartupSettings(previous.RunOnStartup);
                throw;
            }
        }
        finally { File.Delete(temporary); }
        if (backup != null) File.Delete(backup);
    }

    private static bool ApplyStartupSettings(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                if (enabled)
                {
                    string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue("ShakeToFindCursor", $"\"{exePath}\"");
                    }
                }
                else
                {
                    key.DeleteValue("ShakeToFindCursor", false);
                }
                return true;
            }
            return false;
        }
        catch
        {
            return false; // Failed due to permissions/AV
        }
    }
}
