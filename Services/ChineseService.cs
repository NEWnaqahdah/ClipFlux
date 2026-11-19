using System;
using System.Text;

namespace ClipFlux.Services;

public static class ChineseService
{
    private static readonly Encoding Gb2312;

    static ChineseService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            Gb2312 = Encoding.GetEncoding("GB18030");
        }
        catch
        {
            Gb2312 = Encoding.Default;
        }
    }

    /// <summary>
    /// Gets Pinyin initials for a given Chinese string (e.g., "微信支付" -> "wxzf")
    /// </summary>
    public static string GetPinyinInitials(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(GetCharPinyinInitial(c));
        }
        return sb.ToString();
    }

    private static char GetCharPinyinInitial(char c)
    {
        // If ASCII letter or digit, return lowercase
        if (c is >= 'a' and <= 'z') return c;
        if (c is >= 'A' and <= 'Z') return char.ToLowerInvariant(c);
        if (c is >= '0' and <= '9') return c;

        // Chinese character range in GB2312 / GBK
        try
        {
            byte[] bytes = Gb2312.GetBytes(new[] { c });
            if (bytes.Length < 2) return c;

            int code = (bytes[0] << 8) + bytes[1];

            if (code >= 0xB0A1 && code <= 0xB0C4) return 'a';
            if (code >= 0xB0C5 && code <= 0xB2C0) return 'b';
            if (code >= 0xB2C1 && code <= 0xB4ED) return 'c';
            if (code >= 0xB4EE && code <= 0xB6E9) return 'd';
            if (code >= 0xB6EA && code <= 0xB7A1) return 'e';
            if (code >= 0xB7A2 && code <= 0xB8C0) return 'f';
            if (code >= 0xB8C1 && code <= 0xB9FD) return 'g';
            if (code >= 0xB9FE && code <= 0xBBF6) return 'h';
            if (code >= 0xBBF7 && code <= 0xBFA5) return 'j';
            if (code >= 0xBFA6 && code <= 0xC0AB) return 'k';
            if (code >= 0xC0AC && code <= 0xC2E7) return 'l';
            if (code >= 0xC2E8 && code <= 0xC4C2) return 'm';
            if (code >= 0xC4C3 && code <= 0xC5B5) return 'n';
            if (code >= 0xC5B6 && code <= 0xC5BD) return 'o';
            if (code >= 0xC5BE && code <= 0xC6D9) return 'p';
            if (code >= 0xC6DA && code <= 0xC8BA) return 'q';
            if (code >= 0xC8BB && code <= 0xC8F5) return 'r';
            if (code >= 0xC8F6 && code <= 0xCBF9) return 's';
            if (code >= 0xCBFA && code <= 0xCDD9) return 't';
            if (code >= 0xCDDA && code <= 0xCEF3) return 'w';
            if (code >= 0xCEF4 && code <= 0xD188) return 'x';
            if (code >= 0xD189 && code <= 0xD4D0) return 'y';
            if (code >= 0xD4D1 && code <= 0xD7F9) return 'z';
        }
        catch { }

        return c;
    }

    /// <summary>
    /// Checks whether the target text matches query via exact substring or Pinyin initials
    /// </summary>
    public static bool MatchesChineseOrPinyin(string target, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(target)) return false;

        query = query.Trim();

        // 1. Direct case-insensitive substring match
        if (target.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Multi-word search (e.g. "wx zf" or "测试 接口")
        if (query.Contains(' '))
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool allMatch = true;
            foreach (var token in tokens)
            {
                if (!MatchesChineseOrPinyin(target, token))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) return true;
        }

        // 3. Pinyin initials match
        string initials = GetPinyinInitials(target);
        if (initials.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Formats DateTime into natural relative time matching the active UI language
    /// </summary>
    public static string FormatChineseRelativeTime(DateTime dt)
    {
        var now = DateTime.Now;
        var span = now - dt;

        if (!LocalizationService.IsChinese)
        {
            if (span.TotalSeconds < 30) return "Just now";
            if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s ago";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24 && dt.Date == now.Date) return $"Today {dt:HH:mm}";
            if (dt.Date == now.Date.AddDays(-1)) return $"Yesterday {dt:HH:mm}";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
            if (dt.Year == now.Year) return dt.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            return dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (span.TotalSeconds < 30) return "刚刚";
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds} 秒前";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24 && dt.Date == now.Date) return $"今天 {dt:HH:mm}";
        if (dt.Date == now.Date.AddDays(-1)) return $"昨天 {dt:HH:mm}";
        if (dt.Date == now.Date.AddDays(-2)) return $"前天 {dt:HH:mm}";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        if (dt.Year == now.Year) return dt.ToString("M月d日 HH:mm");
        return dt.ToString("yyyy年M月d日");
    }

    /// <summary>
    /// Sanitizes and heals Chinese title from text payload
    /// </summary>
    public static string GenerateCleanChineseTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        string firstLine = lines.Length > 0 ? lines[0].Trim() : text.Trim();

        if (firstLine.Length > 65)
        {
            return firstLine[..65] + "…";
        }
        return firstLine;
    }
}
