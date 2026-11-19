using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ClipFlux.Services;

public class AppSettings
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipFlux",
        "settings.json");

    public bool AutoStart { get; set; } = false;
    public bool EnableGlobalHotkey { get; set; } = false; // Default: false
    public uint HotkeyModifiers { get; set; } = 0x0001; // Win32.MOD_ALT
    public uint HotkeyKey { get; set; } = 0x56;        // 'V'
    public string HotkeyDisplay { get; set; } = "Alt + V";
    public string HotkeyType { get; set; } = "Custom";
    public string FontFamilyName { get; set; } = "Microsoft YaHei UI";
    public double GlobalFontScale { get; set; } = 1.0;
    public double NavFontSize { get; set; } = 14.5;
    public double ListFontSize { get; set; } = 15.5;
    public double PreviewFontSize { get; set; } = 18.0;
    public int TrayRecentCount { get; set; } = 5;
    public int MaxDisplayCount { get; set; } = 1000;
    public string ThemeMode { get; set; } = ThemeService.ModeSystem;
    public string LanguageMode { get; set; } = LocalizationService.ModeSystem;

    // Backup settings
    public string LocalBackupDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ClipFluxBackups");
    public bool EnableScheduledBackup { get; set; } = false;
    public int BackupIntervalHours { get; set; } = 24;
    public int MaxBackupRetention { get; set; } = 7;
    public string LastBackupTime { get; set; } = string.Empty;

    public bool EnableCloudDriveBackup { get; set; } = false;
    public string CloudDriveProvider { get; set; } = string.Empty;
    public string CloudDriveFolder { get; set; } = string.Empty;

    public bool EnableWebDavBackup { get; set; } = false;
    public string WebDavUrl { get; set; } = string.Empty;
    public string WebDavUsername { get; set; } = string.Empty;
    public string WebDavPasswordProtected { get; set; } = string.Empty;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    settings.AutoStart = CheckAutoStartInRegistry();

                    // Migrate older settings if HotkeyKey is not yet set
                    if (settings.HotkeyKey == 0)
                    {
                        if (settings.HotkeyType == "CtrlD")
                        {
                            settings.HotkeyModifiers = 0x0002;
                            settings.HotkeyKey = 0x44;
                            settings.HotkeyDisplay = "Ctrl + D";
                        }
                        else
                        {
                            settings.HotkeyModifiers = 0x0001;
                            settings.HotkeyKey = 0x56;
                            settings.HotkeyDisplay = "Alt + V";
                        }
                    }

                    return settings;
                }
            }
        }
        catch { }

        var def = new AppSettings
        {
            AutoStart = CheckAutoStartInRegistry()
        };
        return def;
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);

            SetAutoStartInRegistry(AutoStart);
        }
        catch { }
    }

    private static bool CheckAutoStartInRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("ClipFlux") != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStartInRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue("ClipFlux", $"\"{exePath}\" --silent");
                }
            }
            else
            {
                key.DeleteValue("ClipFlux", false);
            }
        }
        catch { }
    }
}
