using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClipFlux.Services;

/// <summary>
/// Manages Light/Dark/System palette tokens and dynamically updates Application resources.
/// </summary>
public static class ThemeService
{
    public const string ModeSystem = "System";
    public const string ModeLight = "Light";
    public const string ModeDark = "Dark";

    public static bool IsDarkTheme { get; private set; } = !IsSystemLightTheme();

    public static bool IsSystemDarkTheme() => !IsSystemLightTheme();

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            object? val = key?.GetValue("AppsUseLightTheme");
            if (val is int intVal)
            {
                return intVal != 0;
            }
        }
        catch { }
        return false;
    }

    public static bool ResolveIsDark(string? themeMode)
    {
        if (string.Equals(themeMode, ModeLight, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(themeMode, ModeDark, StringComparison.OrdinalIgnoreCase)) return true;
        return IsSystemDarkTheme();
    }

    public static string NormalizeMode(string? themeMode)
    {
        if (string.Equals(themeMode, ModeLight, StringComparison.OrdinalIgnoreCase)) return ModeLight;
        if (string.Equals(themeMode, ModeDark, StringComparison.OrdinalIgnoreCase)) return ModeDark;
        return ModeSystem;
    }

    public static void ApplyTheme(string? themeMode)
    {
        bool isDark = ResolveIsDark(themeMode);
        IsDarkTheme = isDark;

        var app = Application.Current;
        if (app == null) return;

        var resources = app.Resources;
        var palette = isDark ? DarkPalette : LightPalette;
        foreach (var (key, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["BrushBgDeep"] = "#0F1117",
        ["BrushBgSurface"] = "#161922",
        ["BrushBgSurfaceAlt"] = "#11141C",
        ["BrushBgCard"] = "#1C202B",
        ["BrushBgCardHover"] = "#232837",
        ["BrushBgCardSelected"] = "#242B40",
        ["BrushBorderSubtle"] = "#282E3E",
        ["BrushBorderCard"] = "#242938",
        ["BrushBorderHover"] = "#343C52",
        ["BrushAccent"] = "#6366F1",
        ["BrushAccentHover"] = "#4F46E5",
        ["BrushAccentSoftBg"] = "#312E81",
        ["BrushAccentSoftFg"] = "#E0E7FF",
        ["BrushTextPrimary"] = "#F3F4F6",
        ["BrushTextSecondary"] = "#9CA3AF",
        ["BrushTextMuted"] = "#6B7280",
        ["BrushLink"] = "#60A5FA",
        ["BrushStar"] = "#F59E0B",
        ["BrushUnstarred"] = "#4B5563",
        ["BrushDanger"] = "#F87171",
        ["BrushDangerBorder"] = "#3A262D",
        ["BrushDangerHoverBg"] = "#2E1A21",
        ["BrushDangerHoverBorder"] = "#7F1D1D",
        ["BrushDangerHoverFg"] = "#FCA5A5",
        ["BrushSecondaryBtnBg"] = "#202533",
        ["BrushSecondaryBtnHoverBg"] = "#2A3042",
        ["BrushGhostHoverBg"] = "#262C3B",
        ["BrushGhostPressedBg"] = "#31384B",
        ["BrushPillBg"] = "#1B1F2B",
        ["BrushInputBg"] = "#141821",
        ["BrushPopupBg"] = "#161922",
        ["BrushOverlayBackdrop"] = "#E60F1117",
        ["BrushScrollThumb"] = "#374151",
        ["BrushScrollThumbHover"] = "#525D73",
        ["BrushCheckBoxBg"] = "#1E232E",
        ["BrushCheckBoxBorder"] = "#3E465B",
        ["BrushSliderTrack"] = "#282E3E"
    };

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["BrushBgDeep"] = "#F3F5F9",
        ["BrushBgSurface"] = "#FFFFFF",
        ["BrushBgSurfaceAlt"] = "#F8FAFC",
        ["BrushBgCard"] = "#FFFFFF",
        ["BrushBgCardHover"] = "#F1F5F9",
        ["BrushBgCardSelected"] = "#EEF2FF",
        ["BrushBorderSubtle"] = "#E2E8F0",
        ["BrushBorderCard"] = "#E5E9F0",
        ["BrushBorderHover"] = "#CBD5E1",
        ["BrushAccent"] = "#4F46E5",
        ["BrushAccentHover"] = "#4338CA",
        ["BrushAccentSoftBg"] = "#E0E7FF",
        ["BrushAccentSoftFg"] = "#3730A3",
        ["BrushTextPrimary"] = "#111827",
        ["BrushTextSecondary"] = "#4B5563",
        ["BrushTextMuted"] = "#6B7280",
        ["BrushLink"] = "#2563EB",
        ["BrushStar"] = "#D97706",
        ["BrushUnstarred"] = "#CBD5E1",
        ["BrushDanger"] = "#DC2626",
        ["BrushDangerBorder"] = "#FECACA",
        ["BrushDangerHoverBg"] = "#FEF2F2",
        ["BrushDangerHoverBorder"] = "#FCA5A5",
        ["BrushDangerHoverFg"] = "#B91C1C",
        ["BrushSecondaryBtnBg"] = "#FFFFFF",
        ["BrushSecondaryBtnHoverBg"] = "#F1F5F9",
        ["BrushGhostHoverBg"] = "#E2E8F0",
        ["BrushGhostPressedBg"] = "#CBD5E1",
        ["BrushPillBg"] = "#EEF2F6",
        ["BrushInputBg"] = "#FFFFFF",
        ["BrushPopupBg"] = "#FFFFFF",
        ["BrushOverlayBackdrop"] = "#CC0F172A",
        ["BrushScrollThumb"] = "#CBD5E1",
        ["BrushScrollThumbHover"] = "#94A3B8",
        ["BrushCheckBoxBg"] = "#FFFFFF",
        ["BrushCheckBoxBorder"] = "#94A3B8",
        ["BrushSliderTrack"] = "#CBD5E1"
    };
}
