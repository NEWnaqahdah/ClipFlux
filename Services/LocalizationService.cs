using System;
using System.Globalization;
using System.Windows;

namespace ClipFlux.Services;

/// <summary>
/// Manages dynamic zh-CN / en-US localization dictionaries with System UI language fallback.
/// </summary>
public static class LocalizationService
{
    public const string ModeSystem = "System";
    public const string ModeZhCn = "zh-CN";
    public const string ModeEnUs = "en-US";

    public static bool IsChinese { get; private set; } = DetectSystemIsChinese();

    private static bool DetectSystemIsChinese()
    {
        try
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public static string NormalizeMode(string? mode)
    {
        if (string.Equals(mode, ModeZhCn, StringComparison.OrdinalIgnoreCase)) return ModeZhCn;
        if (string.Equals(mode, ModeEnUs, StringComparison.OrdinalIgnoreCase)) return ModeEnUs;
        return ModeSystem;
    }

    public static string ResolveCulture(string? mode)
    {
        string normalized = NormalizeMode(mode);
        if (normalized == ModeZhCn) return ModeZhCn;
        if (normalized == ModeEnUs) return ModeEnUs;
        return DetectSystemIsChinese() ? ModeZhCn : ModeEnUs;
    }

    public static void ApplyLanguage(string? mode)
    {
        string culture = ResolveCulture(mode);
        IsChinese = culture.Equals(ModeZhCn, StringComparison.OrdinalIgnoreCase);

        var app = Application.Current;
        if (app == null) return;

        var uri = new Uri($"pack://application:,,,/ClipFlux;component/Resources/Strings.{culture}.xaml", UriKind.Absolute);
        var nextDict = new ResourceDictionary { Source = uri };

        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            string? src = merged[i].Source?.OriginalString;
            if (!string.IsNullOrEmpty(src) && src.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase))
            {
                merged.RemoveAt(i);
            }
        }
        merged.Add(nextDict);
    }

    public static string Get(string key, string fallback = "")
    {
        var app = Application.Current;
        if (app != null && app.TryFindResource(key) is string s)
        {
            return s;
        }
        return fallback;
    }

    public static string Format(string key, params object[] args)
    {
        string template = Get(key, string.Empty);
        if (string.IsNullOrEmpty(template)) return string.Empty;
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }
}
