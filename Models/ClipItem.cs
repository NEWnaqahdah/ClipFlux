using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ClipFlux.Native;

namespace ClipFlux.Models;

public readonly record struct ContainerEntry(int FormatId, string FormatName, int Offset, int Length);

public class ClipItem : INotifyPropertyChanged
{
    private static readonly Encoding GbEncoding;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static ClipItem()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            GbEncoding = Encoding.GetEncoding("GB18030");
        }
        catch
        {
            GbEncoding = Encoding.Default;
        }
    }

    private long _id;
    private DateTime _created = DateTime.Now;
    private string _title = string.Empty;
    private ClipType _type = ClipType.Text;
    private string _description = string.Empty;
    private byte[] _data = Array.Empty<byte>();
    private int _dataSize;
    private string _appName = string.Empty;
    private string _appTitle = string.Empty;
    private bool _isStarred;
    private string _tags = ",";
    private string _url = string.Empty;

    private string? _cachedTextContent;
    private string? _cachedPlainTextContent;
    private string? _cachedRtfContent;
    private string? _cachedHtmlContent;
    private string? _cachedDisplayUrl;
    private WeakReference<BitmapSource>? _weakBitmap;
    private WeakReference<BitmapSource>? _weakThumbnail;

    public long Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public DateTime Created
    {
        get => _created;
        set
        {
            if (SetField(ref _created, value))
            {
                OnPropertyChanged(nameof(TimeAgo));
            }
        }
    }

    public string Title
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_title) || _title.Contains('\ufffd'))
            {
                if (Type == ClipType.Image)
                {
                    _title = $"[图像] {AppNameDisplay} - {FormattedSize}";
                }
                else
                {
                    string fallback = Type switch
                    {
                        ClipType.Html => ConvertHtmlToPlainText(GetHtmlContent() ?? TextContent),
                        ClipType.RichText => ConvertRtfToPlainText(GetRtfContent() ?? TextContent),
                        _ => TextContent
                    };
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        _title = Services.ChineseService.GenerateCleanChineseTitle(fallback);
                    }
                }
            }
            return _title;
        }
        set
        {
            if (SetField(ref _title, value))
            {
                _cachedPlainTextContent = null;
                OnPropertyChanged(nameof(PlainTextContent));
            }
        }
    }

    public ClipType Type
    {
        get => _type;
        set
        {
            if (SetField(ref _type, value))
            {
                OnPropertyChanged(nameof(TypeDisplayName));
                OnPropertyChanged(nameof(TypeBadgeBackground));
                OnPropertyChanged(nameof(TypeBadgeForeground));
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public byte[] Data
    {
        get => _data;
        set
        {
            if (SetField(ref _data, value))
            {
                InvalidateDataCaches();
            }
        }
    }

    public int DataSize
    {
        get => _dataSize;
        set
        {
            if (SetField(ref _dataSize, value))
            {
                OnPropertyChanged(nameof(FormattedSize));
            }
        }
    }

    public string AppName
    {
        get => _appName;
        set
        {
            if (SetField(ref _appName, value))
            {
                OnPropertyChanged(nameof(AppNameDisplay));
            }
        }
    }

    public string AppTitle
    {
        get => _appTitle;
        set => SetField(ref _appTitle, value);
    }

    public bool IsStarred
    {
        get => _isStarred;
        set => SetField(ref _isStarred, value);
    }

    public string Tags
    {
        get => _tags;
        set => SetField(ref _tags, value);
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetField(ref _url, value))
            {
                _cachedDisplayUrl = null;
                _cachedPlainTextContent = null;
                OnPropertyChanged(nameof(DisplayUrl));
                OnPropertyChanged(nameof(HasUrl));
                OnPropertyChanged(nameof(PlainTextContent));
            }
        }
    }

    public string DisplayUrl
    {
        get
        {
            if (_cachedDisplayUrl != null) return _cachedDisplayUrl;

            string resolved = ResolveDisplayUrl();
            _cachedDisplayUrl = resolved;
            return resolved;
        }
    }

    public bool HasUrl => !string.IsNullOrWhiteSpace(DisplayUrl);

    public string AppNameDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_appName))
            {
                return Services.LocalizationService.Get("Str_SystemClipboard", "系统剪贴板");
            }
            var name = _appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? _appName[..^4]
                : _appName;
            return char.ToUpper(name[0]) + name[1..];
        }
    }

    public string FormattedSize
    {
        get
        {
            if (_dataSize < 1024) return $"{_dataSize} B";
            if (_dataSize < 1024 * 1024) return $"{_dataSize / 1024.0:F1} KB";
            return $"{_dataSize / (1024.0 * 1024):F1} MB";
        }
    }

    public string TimeAgo => Services.ChineseService.FormatChineseRelativeTime(_created);

    public string TypeDisplayName => Type switch
    {
        ClipType.Text => Services.LocalizationService.Get("Str_Type_Text", "纯文本"),
        ClipType.RichText => Services.LocalizationService.Get("Str_Type_RichText", "富文本"),
        ClipType.Html => Services.LocalizationService.Get("Str_Type_Html", "网页HTML"),
        ClipType.Files => Services.LocalizationService.Get("Str_Type_Files", "文件列表"),
        ClipType.Image => Services.LocalizationService.Get("Str_Type_Image", "图像"),
        _ => Services.LocalizationService.Get("Str_Type_Binary", "二进制")
    };

    public void NotifyLocaleChanged()
    {
        OnPropertyChanged(nameof(TypeDisplayName));
        OnPropertyChanged(nameof(AppNameDisplay));
        OnPropertyChanged(nameof(TimeAgo));
    }

    public string TypeBadgeBackground => Services.ThemeService.IsDarkTheme
        ? Type switch
        {
            ClipType.Text => "#1E293B",
            ClipType.RichText => "#2E1F47",
            ClipType.Html => "#132F3B",
            ClipType.Files => "#3B2314",
            ClipType.Image => "#172B4D",
            _ => "#212121"
        }
        : Type switch
        {
            ClipType.Text => "#EDF1F7",
            ClipType.RichText => "#F3E8FF",
            ClipType.Html => "#E0F2FE",
            ClipType.Files => "#FFEDD5",
            ClipType.Image => "#DBEAFE",
            _ => "#F3F4F6"
        };

    public string TypeBadgeForeground => Services.ThemeService.IsDarkTheme
        ? Type switch
        {
            ClipType.Text => "#94A3B8",
            ClipType.RichText => "#C4B5FD",
            ClipType.Html => "#38BDF8",
            ClipType.Files => "#FDBA74",
            ClipType.Image => "#60A5FA",
            _ => "#E0E0E0"
        }
        : Type switch
        {
            ClipType.Text => "#475569",
            ClipType.RichText => "#7C3AED",
            ClipType.Html => "#0369A1",
            ClipType.Files => "#C2410C",
            ClipType.Image => "#1D4ED8",
            _ => "#374151"
        };

    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(TypeBadgeBackground));
        OnPropertyChanged(nameof(TypeBadgeForeground));
        OnPropertyChanged(nameof(IsStarred));
    }

    private void InvalidateDataCaches()
    {
        _cachedTextContent = null;
        _cachedPlainTextContent = null;
        _cachedRtfContent = null;
        _cachedHtmlContent = null;
        _cachedDisplayUrl = null;
        _weakBitmap = null;
        _weakThumbnail = null;
        OnPropertyChanged(nameof(TextContent));
        OnPropertyChanged(nameof(PlainTextContent));
        OnPropertyChanged(nameof(DisplayUrl));
        OnPropertyChanged(nameof(HasUrl));
        OnPropertyChanged(nameof(ThumbnailImage));
        OnPropertyChanged(nameof(BitmapImage));
    }

    /// <summary>
    /// Parses multi-format binary container payloads into individual clipboard format entries.
    /// Format: 0x01 0x00 | short count | [int formatId, int dataLen, int nameLen, byte[] name, byte[] data]...
    /// </summary>
    public static bool TryParseContainer(byte[] raw, out List<ContainerEntry> entries)
    {
        entries = new List<ContainerEntry>();
        if (raw == null || raw.Length < 16 || raw[0] != 0x01 || raw[1] != 0x00)
        {
            return false;
        }

        short formatCount = BitConverter.ToInt16(raw, 2);
        if (formatCount <= 0 || formatCount > 64)
        {
            return false;
        }

        int pos = 4;
        for (int f = 0; f < formatCount; f++)
        {
            if (pos > raw.Length - 12) break;

            int formatId = BitConverter.ToInt32(raw, pos);
            int dataLen = BitConverter.ToInt32(raw, pos + 4);
            int nameLen = BitConverter.ToInt32(raw, pos + 8);
            pos += 12;

            if (nameLen < 0 || dataLen < 0 || nameLen > raw.Length - pos) break;
            string name = Encoding.ASCII.GetString(raw, pos, nameLen);
            pos += nameLen;

            if (dataLen > raw.Length - pos) break;
            entries.Add(new ContainerEntry(formatId, name, pos, dataLen));
            pos += dataLen;
        }

        return entries.Count > 0;
    }

    /// <summary>
    /// Packs multiple clipboard format payloads into the unified binary container format.
    /// </summary>
    public static byte[] BuildMultiFormatContainer(IReadOnlyList<(int FormatId, string FormatName, byte[] Data)> entries)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write((byte)0x01);
        bw.Write((byte)0x00);
        bw.Write((short)entries.Count);

        foreach (var (formatId, formatName, data) in entries)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(formatName);
            byte[] payload = data ?? Array.Empty<byte>();
            bw.Write(formatId);
            bw.Write(payload.Length);
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(payload);
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static string DecodeContainerText(byte[] raw, int offset, int length, bool preferUnicodeLe = false)
    {
        if (length <= 0 || offset < 0 || offset + length > raw.Length) return string.Empty;

        if (preferUnicodeLe || LooksLikeUtf16Le(raw, offset, length))
        {
            return Encoding.Unicode.GetString(raw, offset, length).TrimEnd('\0');
        }

        try
        {
            return StrictUtf8.GetString(raw, offset, length).TrimEnd('\0');
        }
        catch
        {
            return GbEncoding.GetString(raw, offset, length).TrimEnd('\0');
        }
    }

    private static bool LooksLikeUtf16Le(byte[] raw, int offset, int length)
    {
        if (length >= 2 && raw[offset] == 0xFF && raw[offset + 1] == 0xFE) return true;
        if (length < 4 || (length & 1) != 0) return false;

        int checkLen = Math.Min(length, 64);
        int zeroOdd = 0;
        int pairs = checkLen / 2;
        for (int i = 1; i < checkLen; i += 2)
        {
            if (raw[offset + i] == 0) zeroOdd++;
        }
        return pairs >= 2 && zeroOdd * 2 >= pairs;
    }

    /// <summary>
    /// Extracts clean RTF source without trailing NULs or sibling container formats.
    /// </summary>
    public string? GetRtfContent()
    {
        if (_cachedRtfContent != null) return _cachedRtfContent.Length > 0 ? _cachedRtfContent : null;
        if (_data == null || _data.Length == 0) return null;

        try
        {
            byte[] raw = DecompressIfNeeded(_data);
            if (TryParseContainer(raw, out var entries))
            {
                ContainerEntry? match = FindContainerEntry(entries,
                    "Rich Text Format",
                    "Rich Text Format Without Objects",
                    "RTF As Text");

                if (!match.HasValue)
                {
                    foreach (var e in entries)
                    {
                        if (e.FormatName.Contains("Rich Text", StringComparison.OrdinalIgnoreCase))
                        {
                            match = e;
                            break;
                        }
                    }
                }

                if (match.HasValue && match.Value.Length > 0)
                {
                    string rtf = DecodeContainerText(raw, match.Value.Offset, match.Value.Length).TrimEnd('\0');
                    _cachedRtfContent = rtf;
                    return rtf;
                }

                _cachedRtfContent = string.Empty;
                return null;
            }

            string decoded = Encoding.UTF8.GetString(raw).TrimEnd('\0');
            if (decoded.TrimStart().StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase))
            {
                _cachedRtfContent = decoded;
                return decoded;
            }
        }
        catch { }

        _cachedRtfContent = string.Empty;
        return null;
    }

    /// <summary>
    /// Extracts clean HTML source (preferring CF_HTML over raw text/html fragments) from the payload.
    /// </summary>
    public string? GetHtmlContent()
    {
        if (_cachedHtmlContent != null) return _cachedHtmlContent.Length > 0 ? _cachedHtmlContent : null;
        if (_data == null || _data.Length == 0) return null;

        try
        {
            byte[] raw = DecompressIfNeeded(_data);
            if (TryParseContainer(raw, out var entries))
            {
                ContainerEntry? match = FindContainerEntry(entries, "HTML Format", "text/html");
                if (!match.HasValue)
                {
                    foreach (var e in entries)
                    {
                        if (e.FormatName.Contains("HTML", StringComparison.OrdinalIgnoreCase) &&
                            !e.FormatName.Contains("context", StringComparison.OrdinalIgnoreCase) &&
                            !e.FormatName.Contains("info", StringComparison.OrdinalIgnoreCase))
                        {
                            match = e;
                            break;
                        }
                    }
                }

                if (match.HasValue && match.Value.Length > 0)
                {
                    bool isTextHtml = match.Value.FormatName.Equals("text/html", StringComparison.OrdinalIgnoreCase);
                    string html = DecodeContainerText(raw, match.Value.Offset, match.Value.Length, preferUnicodeLe: isTextHtml && LooksLikeUtf16Le(raw, match.Value.Offset, match.Value.Length));
                    _cachedHtmlContent = html;
                    return html;
                }

                _cachedHtmlContent = string.Empty;
                return null;
            }

            string decoded = Encoding.UTF8.GetString(raw).TrimEnd('\0');
            _cachedHtmlContent = decoded;
            return decoded;
        }
        catch { }

        _cachedHtmlContent = string.Empty;
        return null;
    }

    /// <summary>
    /// Extracts stored plain Unicode/ANSI text from multi-format container records, if present.
    /// </summary>
    public string? GetStoredUnicodeText()
    {
        if (_data == null || _data.Length == 0) return null;

        try
        {
            byte[] raw = DecompressIfNeeded(_data);
            if (!TryParseContainer(raw, out var entries)) return null;

            foreach (var e in entries)
            {
                if (e.FormatId == (int)Win32.CF_UNICODETEXT ||
                    e.FormatName.Contains("UNICODETEXT", StringComparison.OrdinalIgnoreCase))
                {
                    string text = DecodeContainerText(raw, e.Offset, e.Length, preferUnicodeLe: true);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }

            foreach (var e in entries)
            {
                if (e.FormatId == (int)Win32.CF_TEXT ||
                    e.FormatName.Equals("CF_TEXT", StringComparison.OrdinalIgnoreCase))
                {
                    string text = DecodeContainerText(raw, e.Offset, e.Length);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
        }
        catch { }

        return null;
    }

    private static ContainerEntry? FindContainerEntry(List<ContainerEntry> entries, params string[] preferredNames)
    {
        foreach (string name in preferredNames)
        {
            foreach (var e in entries)
            {
                if (e.FormatName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
        }
        return null;
    }

    private string ResolveDisplayUrl()
    {
        string? storedUrl = IsWebUrl(_url) ? _url.Trim() : null;
        if (!string.IsNullOrEmpty(storedUrl) && !Services.BrowserUrlService.IsOriginOnlyUrl(storedUrl))
        {
            return storedUrl;
        }

        if (Type == ClipType.Html)
        {
            string? html = GetHtmlContent();
            string? sourceUrl = !string.IsNullOrWhiteSpace(html) ? ExtractSourceUrlFromHtml(html) : null;
            if (!string.IsNullOrWhiteSpace(sourceUrl) && !Services.BrowserUrlService.IsOriginOnlyUrl(sourceUrl))
            {
                return sourceUrl;
            }

            if (_data != null && _data.Length > 0)
            {
                try
                {
                    byte[] raw = DecompressIfNeeded(_data);
                    if (TryParseContainer(raw, out var entries))
                    {
                        ContainerEntry? mozUrl = FindContainerEntry(entries, "text/x-moz-url-priv", "text/x-moz-url");
                        if (mozUrl.HasValue && mozUrl.Value.Length > 0)
                        {
                            string decoded = DecodeContainerText(raw, mozUrl.Value.Offset, mozUrl.Value.Length, preferUnicodeLe: true);
                            var lines = decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            if (lines.Length > 0)
                            {
                                string firstLine = lines[0].Trim();
                                if (IsWebUrl(firstLine))
                                {
                                    if (!Services.BrowserUrlService.IsOriginOnlyUrl(firstLine)) return firstLine;
                                    storedUrl ??= firstLine;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(storedUrl)) return storedUrl;
            if (!string.IsNullOrWhiteSpace(sourceUrl)) return sourceUrl;

            if (!string.IsNullOrWhiteSpace(html) && IsSingleLinkHtml(html, out string? linkUrl) && !string.IsNullOrWhiteSpace(linkUrl))
            {
                return linkUrl.Trim();
            }
        }

        return storedUrl ?? string.Empty;
    }

    /// <summary>
    /// Parses SourceURL from the CF_HTML clipboard header when it points to an http/https/ftp URL.
    /// </summary>
    public static string? ExtractSourceUrlFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        int scanLen = Math.Min(html.Length, 1024);
        var match = Regex.Match(
            html.AsSpan(0, scanLen).ToString(),
            @"^SourceURL:[ \t]*(?<url>[^\r\n]+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        string candidate = match.Groups["url"].Value.Trim();
        return IsWebUrl(candidate) ? candidate : null;
    }

    public string TextContent
    {
        get
        {
            if (_cachedTextContent != null) return _cachedTextContent;
            if (Type == ClipType.Image)
            {
                _cachedTextContent = $"[图像 - {FormattedSize}]";
                return _cachedTextContent;
            }
            if (_data == null || _data.Length == 0) return _title;

            try
            {
                if (Type == ClipType.RichText)
                {
                    string? rtf = GetRtfContent();
                    if (!string.IsNullOrEmpty(rtf))
                    {
                        _cachedTextContent = rtf;
                        return _cachedTextContent;
                    }
                }
                else if (Type == ClipType.Html)
                {
                    string? html = GetHtmlContent();
                    if (!string.IsNullOrEmpty(html))
                    {
                        _cachedTextContent = html;
                        return _cachedTextContent;
                    }
                }
                else
                {
                    string? stored = GetStoredUnicodeText();
                    if (!string.IsNullOrEmpty(stored))
                    {
                        _cachedTextContent = stored;
                        return _cachedTextContent;
                    }
                }

                byte[] raw = DecompressIfNeeded(_data);
                _cachedTextContent = Encoding.UTF8.GetString(raw).TrimEnd('\0');
                return _cachedTextContent;
            }
            catch
            {
                _cachedTextContent = _title;
                return _cachedTextContent;
            }
        }
    }

    public string PlainTextContent
    {
        get
        {
            if (_cachedPlainTextContent != null) return _cachedPlainTextContent;

            if (Type == ClipType.Text)
            {
                _cachedPlainTextContent = TextContent;
                return _cachedPlainTextContent;
            }

            if (Type == ClipType.Html)
            {
                string? stored = GetStoredUnicodeText();
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    _cachedPlainTextContent = stored;
                    return _cachedPlainTextContent;
                }

                if (IsWebUrl(_title))
                {
                    _cachedPlainTextContent = _title.Trim();
                    return _cachedPlainTextContent;
                }

                string rawHtml = GetHtmlContent() ?? TextContent;
                if (Description.Contains("网页链接", StringComparison.Ordinal) &&
                    IsSingleLinkHtml(rawHtml, out string? linkUrl) &&
                    !string.IsNullOrWhiteSpace(linkUrl))
                {
                    _cachedPlainTextContent = linkUrl;
                    return _cachedPlainTextContent;
                }

                _cachedPlainTextContent = ConvertHtmlToPlainText(rawHtml);
                return _cachedPlainTextContent;
            }

            if (Type == ClipType.RichText)
            {
                string? stored = GetStoredUnicodeText();
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    _cachedPlainTextContent = stored;
                    return _cachedPlainTextContent;
                }

                string rtfText = GetRtfContent() ?? TextContent;
                _cachedPlainTextContent = ConvertRtfToPlainText(rtfText);
                return _cachedPlainTextContent;
            }

            if (Type == ClipType.Files)
            {
                _cachedPlainTextContent = !string.IsNullOrWhiteSpace(Description) ? Description : Title;
                return _cachedPlainTextContent;
            }

            if (Type == ClipType.Image)
            {
                _cachedPlainTextContent = Title;
                return _cachedPlainTextContent;
            }

            _cachedPlainTextContent = TextContent;
            return _cachedPlainTextContent;
        }
    }

    public static string ConvertHtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // 1. Strip CF_HTML header (Version:0.9 ... StartHTML...)
        if (html.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            int fragStartIdx = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            if (fragStartIdx >= 0)
            {
                int fragEndIdx = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
                if (fragEndIdx > fragStartIdx)
                {
                    html = html.Substring(fragStartIdx + 20, fragEndIdx - (fragStartIdx + 20));
                }
            }
            else
            {
                int htmlTagPos = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
                if (htmlTagPos >= 0)
                {
                    html = html.Substring(htmlTagPos);
                }
            }
        }
        else
        {
            int fragStartIdx = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            int fragEndIdx = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (fragStartIdx >= 0 && fragEndIdx > fragStartIdx)
            {
                html = html.Substring(fragStartIdx + 20, fragEndIdx - (fragStartIdx + 20));
            }
        }

        // 2. Extract <body> if present
        int bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0)
        {
            int bodyOpenEnd = html.IndexOf('>', bodyStart);
            int bodyEnd = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyOpenEnd >= 0 && bodyEnd > bodyOpenEnd)
            {
                html = html.Substring(bodyOpenEnd + 1, bodyEnd - (bodyOpenEnd + 1));
            }
        }

        // 3. Remove <head>, <style>, <script>
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<head[^>]*>[\s\S]*?</head>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 4. Replace block elements with newlines
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<(p|div|tr|li|h[1-6])[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"</(p|div|tr|li|h[1-6])>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 5. Strip all remaining HTML tags
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 6. Decode HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);
        html = html.Replace('\u00A0', ' ');

        // 7. Normalize line breaks
        html = html.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = html.Split('\n');
        var sb = new StringBuilder();
        bool lastWasEmpty = false;
        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!lastWasEmpty && sb.Length > 0)
                {
                    sb.AppendLine();
                    lastWasEmpty = true;
                }
            }
            else
            {
                sb.AppendLine(line);
                lastWasEmpty = false;
            }
        }

        return sb.ToString().Trim();
    }

    public static bool IsWebUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSingleLinkHtml(string html, out string? linkUrl)
    {
        linkUrl = null;
        if (string.IsNullOrWhiteSpace(html)) return false;

        string target = html;
        int fragStart = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        int fragEnd = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (fragStart >= 0 && fragEnd > fragStart)
        {
            target = html.Substring(fragStart + 20, fragEnd - (fragStart + 20)).Trim();
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            target,
            @"<a\s+[^>]*href=[""'](?<url>[^""']+)[""'][^>]*>[\s\S]*?</a>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success) return false;

        string remaining = target.Remove(match.Index, match.Length);
        string remainingText = ConvertHtmlToPlainText(remaining).Trim();

        if (remainingText.Length < 10)
        {
            string url = match.Groups["url"].Value.Trim();
            if (IsWebUrl(url))
            {
                linkUrl = url;
                return true;
            }
        }

        return false;
    }

    public static string ConvertRtfToPlainText(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return string.Empty;
        if (!rtf.TrimStart().StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase))
        {
            return rtf;
        }

        try
        {
            using var rtb = new System.Windows.Forms.RichTextBox();
            rtb.Rtf = rtf;
            return rtb.Text.Trim();
        }
        catch
        {
            // Fallback to regex stripping
            try
            {
                string cleaned = System.Text.RegularExpressions.Regex.Replace(
                    rtf,
                    @"\{\\(?:fonttbl|colortbl|stylesheet|info|pict|nonesttables|themedata|datastore)[\s\S]*?\}",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\\(par|line)\b", "\n");
                cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\\[a-zA-Z]+-?[0-9]* ?", "");
                cleaned = cleaned.Replace("{", "").Replace("}", "").Replace("\\\\", "\\");
                return cleaned.Trim();
            }
            catch
            {
                return rtf.Trim();
            }
        }
    }

    public BitmapSource? ThumbnailImage
    {
        get
        {
            if (_weakThumbnail != null && _weakThumbnail.TryGetTarget(out var cached))
            {
                return cached;
            }

            if (Type != ClipType.Image || _data == null || _data.Length == 0) return null;

            byte[] raw = DecompressIfNeeded(_data);
            var bmp = DecodeImagePayload(raw, decodeWidth: 260);
            if (bmp != null)
            {
                _weakThumbnail = new WeakReference<BitmapSource>(bmp);
            }
            return bmp;
        }
    }

    public BitmapSource? BitmapImage
    {
        get
        {
            if (_weakBitmap != null && _weakBitmap.TryGetTarget(out var cached))
            {
                return cached;
            }

            if (Type != ClipType.Image || _data == null || _data.Length == 0) return null;

            byte[] raw = DecompressIfNeeded(_data);
            var bmp = DecodeImagePayload(raw, decodeWidth: 960);
            if (bmp != null)
            {
                _weakBitmap = new WeakReference<BitmapSource>(bmp);
            }
            return bmp;
        }
    }

    /// <summary>
    /// Decodes image at 100% full original resolution on demand (for pasting/saving/exporting).
    /// </summary>
    public BitmapSource? GetFullResolutionBitmap()
    {
        if (Type != ClipType.Image || _data == null || _data.Length == 0) return null;
        byte[] raw = DecompressIfNeeded(_data);
        return DecodeImagePayload(raw, decodeWidth: null);
    }

    private static BitmapImage? LoadBitmapFromMemory(byte[] buffer, int offset, int count, int? decodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(buffer, offset, count);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth.HasValue && decodeWidth.Value > 0)
            {
                img.DecodePixelWidth = decodeWidth.Value;
            }
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? DecodeImagePayload(byte[] raw, int? decodeWidth)
    {
        if (raw == null || raw.Length < 8) return null;

        // 1. Direct standard stream (PNG, BMP, JPG, GIF)
        var direct = LoadBitmapFromMemory(raw, 0, raw.Length, decodeWidth);
        if (direct != null) return direct;

        // 2. Multi-format container
        if (TryParseContainer(raw, out var entries))
        {
            foreach (var e in entries)
            {
                if (e.Length <= 0) continue;

                if (e.FormatId == 8 || e.FormatName.Contains("DIB", StringComparison.OrdinalIgnoreCase))
                {
                    var bmp = CreateBitmapFromDib(raw, e.Offset, e.Length, decodeWidth);
                    if (bmp != null) return bmp;
                }

                if (e.FormatName.Contains("PNG", StringComparison.OrdinalIgnoreCase) || e.FormatName.Contains("image/", StringComparison.OrdinalIgnoreCase))
                {
                    var bmp = LoadBitmapFromMemory(raw, e.Offset, e.Length, decodeWidth);
                    if (bmp != null) return bmp;
                }

                if (e.FormatName.Contains("Bitmap", StringComparison.OrdinalIgnoreCase) || e.FormatName.Contains("Drawing", StringComparison.OrdinalIgnoreCase))
                {
                    for (int k = e.Offset; k < e.Offset + e.Length - 14; k++)
                    {
                        if (raw[k] == 0x42 && raw[k + 1] == 0x4D)
                        {
                            var bmp = LoadBitmapFromMemory(raw, k, e.Offset + e.Length - k, decodeWidth);
                            if (bmp != null) return bmp;
                        }
                    }
                }
            }
        }

        // 3. Raw standalone DIB (biSize 40, 108, or 124)
        if (raw.Length > 40 && (BitConverter.ToInt32(raw, 0) == 40 || BitConverter.ToInt32(raw, 0) == 108 || BitConverter.ToInt32(raw, 0) == 124))
        {
            var bmp = CreateBitmapFromDib(raw, 0, raw.Length, decodeWidth);
            if (bmp != null) return bmp;
        }

        // 4. Scan raw buffer for embedded PNG or BMP signature
        for (int i = 0; i < raw.Length - 14; i++)
        {
            if (raw[i] == 0x89 && raw[i + 1] == 0x50 && raw[i + 2] == 0x4E && raw[i + 3] == 0x47)
            {
                var bmp = LoadBitmapFromMemory(raw, i, raw.Length - i, decodeWidth);
                if (bmp != null) return bmp;
            }
            if (raw[i] == 0x42 && raw[i + 1] == 0x4D)
            {
                var bmp = LoadBitmapFromMemory(raw, i, raw.Length - i, decodeWidth);
                if (bmp != null) return bmp;
            }
        }

        // 5. GDI+ fallback
        return DecodeViaGdiPlus(raw);
    }

    private static byte[] DecompressIfNeeded(byte[] data)
    {
        if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var msIn = new MemoryStream(data);
            using var gz = new GZipStream(msIn, CompressionMode.Decompress);
            using var msOut = new MemoryStream();
            gz.CopyTo(msOut);
            return msOut.ToArray();
        }
        return data;
    }

    private static BitmapSource? CreateBitmapFromDib(byte[] raw, int payloadOffset, int payloadLen, int? decodeWidth)
    {
        try
        {
            int biSize = BitConverter.ToInt32(raw, payloadOffset);
            if (biSize != 40 && biSize != 108 && biSize != 124) return null;

            int bitCount = BitConverter.ToInt16(raw, payloadOffset + 14);
            int biCompression = BitConverter.ToInt32(raw, payloadOffset + 16);
            int clrUsed = BitConverter.ToInt32(raw, payloadOffset + 32);

            int headerAndPaletteSize = biSize;
            // BI_BITFIELDS (3) or BI_ALPHABITFIELDS (6) masks following standard 40-byte header
            if (biSize == 40 && (biCompression == 3 || biCompression == 6))
            {
                headerAndPaletteSize += (biCompression == 6 ? 16 : 12);
            }
            if (bitCount <= 8)
            {
                int paletteEntries = clrUsed > 0 ? clrUsed : (1 << bitCount);
                headerAndPaletteSize += paletteEntries * 4;
            }

            using var bmpStream = new MemoryStream();
            using var bw = new BinaryWriter(bmpStream);
            bw.Write((ushort)0x4D42); // "BM"
            bw.Write(14 + payloadLen); // Total BMP file size
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write(14 + headerAndPaletteSize); // Offset to pixel bits
            bw.Write(raw, payloadOffset, payloadLen);

            bmpStream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth.HasValue && decodeWidth.Value > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth.Value;
            }
            bitmap.StreamSource = bmpStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? DecodeViaGdiPlus(byte[] data)
    {
        try
        {
            byte[] raw = DecompressIfNeeded(data);
            using var ms = new MemoryStream(raw);
            using var gdi = System.Drawing.Image.FromStream(ms);
            using var bmp = new System.Drawing.Bitmap(gdi);
            var hBitmap = bmp.GetHbitmap();
            try
            {
                var wpf = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                wpf.Freeze();
                return wpf;
            }
            finally
            {
                Win32.DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    public void ClearDataAndCache()
    {
        _data = Array.Empty<byte>();
        InvalidateDataCaches();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
