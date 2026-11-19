using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClipFlux.Models;

namespace ClipFlux.Services;

/// <summary>
/// Provides pure-WPF rich content conversion (RTF/CF_HTML) and theme-aware FlowDocument preview rendering.
/// </summary>
public static class RichContentService
{
    private sealed class PreviewThemePalette
    {
        public bool IsDark { get; init; }
        public Color DefaultTextColor { get; init; }
        public Color MutedDarkTextColor { get; init; }
        public Brush DefaultText { get; init; } = Brushes.White;
        public Brush Link { get; init; } = Brushes.DodgerBlue;
        public Brush CodeBg { get; init; } = Brushes.Black;
        public Brush InlineCodeBg { get; init; } = Brushes.DarkSlateGray;
        public Brush InlineCodeFg { get; init; } = Brushes.LightBlue;
        public Brush SubtleBorder { get; init; } = Brushes.Gray;
        public Brush QuoteBorder { get; init; } = Brushes.Indigo;
        public Brush QuoteText { get; init; } = Brushes.LightGray;
        public Brush TableHeaderBg { get; init; } = Brushes.SlateGray;
        public Brush ImageBadgeFg { get; init; } = Brushes.Gray;
        public Brush MarkBg { get; init; } = Brushes.Yellow;
        public Brush MarkFg { get; init; } = Brushes.Black;
    }

    private static readonly PreviewThemePalette DarkPreviewPalette = new()
    {
        IsDark = true,
        DefaultTextColor = Color.FromRgb(0xE5, 0xE7, 0xEB),
        DefaultText = FreezeBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
        Link = FreezeBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
        CodeBg = FreezeBrush(Color.FromRgb(0x0D, 0x0F, 0x14)),
        InlineCodeBg = FreezeBrush(Color.FromRgb(0x1E, 0x24, 0x33)),
        InlineCodeFg = FreezeBrush(Color.FromRgb(0x93, 0xC5, 0xFD)),
        SubtleBorder = FreezeBrush(Color.FromRgb(0x2E, 0x34, 0x44)),
        QuoteBorder = FreezeBrush(Color.FromRgb(0x63, 0x66, 0xF1)),
        QuoteText = FreezeBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
        TableHeaderBg = FreezeBrush(Color.FromRgb(0x1E, 0x23, 0x30)),
        ImageBadgeFg = FreezeBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
        MarkBg = FreezeBrush(Color.FromRgb(0x42, 0x20, 0x06)),
        MarkFg = FreezeBrush(Color.FromRgb(0xFD, 0xE0, 0x47))
    };

    private static readonly PreviewThemePalette LightPreviewPalette = new()
    {
        IsDark = false,
        DefaultTextColor = Color.FromRgb(0x11, 0x18, 0x27),
        MutedDarkTextColor = Color.FromRgb(0x37, 0x41, 0x51),
        DefaultText = FreezeBrush(Color.FromRgb(0x11, 0x18, 0x27)),
        Link = FreezeBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
        CodeBg = FreezeBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
        InlineCodeBg = FreezeBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)),
        InlineCodeFg = FreezeBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)),
        SubtleBorder = FreezeBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
        QuoteBorder = FreezeBrush(Color.FromRgb(0x4F, 0x46, 0xE5)),
        QuoteText = FreezeBrush(Color.FromRgb(0x37, 0x41, 0x51)),
        TableHeaderBg = FreezeBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
        ImageBadgeFg = FreezeBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        MarkBg = FreezeBrush(Color.FromRgb(0xFE, 0xF0, 0x8A)),
        MarkFg = FreezeBrush(Color.FromRgb(0x71, 0x3F, 0x12))
    };

    private static readonly FontFamily DefaultCodeFont = new("Cascadia Code, Consolas, Microsoft YaHei UI, monospace");

    private static SolidColorBrush FreezeBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Wraps an HTML fragment in a valid Windows CF_HTML header with exact UTF-8 byte offsets.
    /// </summary>
    public static string BuildCfHtml(string htmlFragment, string? sourceUrl = null)
    {
        string urlLine = string.Empty;
        if (!string.IsNullOrWhiteSpace(sourceUrl) && !sourceUrl.Contains('\r') && !sourceUrl.Contains('\n'))
        {
            urlLine = $"SourceURL:{sourceUrl.Trim()}\r\n";
        }

        string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:0000000000\r\n" +
            "EndHTML:0000000000\r\n" +
            "StartFragment:0000000000\r\n" +
            "EndFragment:0000000000\r\n" +
            urlLine;

        const string prefix = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";

        int headerBytesLen = Encoding.UTF8.GetByteCount(headerTemplate);
        int prefixBytesLen = Encoding.UTF8.GetByteCount(prefix);
        int fragBytesLen = Encoding.UTF8.GetByteCount(htmlFragment ?? string.Empty);
        int suffixBytesLen = Encoding.UTF8.GetByteCount(suffix);

        int startHtml = headerBytesLen;
        int startFragment = startHtml + prefixBytesLen;
        int endFragment = startFragment + fragBytesLen;
        int endHtml = endFragment + suffixBytesLen;

        string header =
            $"Version:0.9\r\n" +
            $"StartHTML:{startHtml:D10}\r\n" +
            $"EndHTML:{endHtml:D10}\r\n" +
            $"StartFragment:{startFragment:D10}\r\n" +
            $"EndFragment:{endFragment:D10}\r\n" +
            urlLine;

        return header + prefix + (htmlFragment ?? string.Empty) + suffix;
    }

    /// <summary>
    /// Extracts the inner HTML fragment and rebuilds CF_HTML headers with exact UTF-8 byte offsets.
    /// </summary>
    public static string NormalizeCfHtml(string rawHtml, string? fallbackSourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return BuildCfHtml(string.Empty, fallbackSourceUrl);
        }

        string? sourceUrl = ClipItem.ExtractSourceUrlFromHtml(rawHtml);
        if (string.IsNullOrWhiteSpace(sourceUrl) && ClipItem.IsWebUrl(fallbackSourceUrl))
        {
            sourceUrl = fallbackSourceUrl!.Trim();
        }

        string fragment = ExtractHtmlFragment(rawHtml);
        return BuildCfHtml(fragment, sourceUrl);
    }

    private static string ExtractHtmlFragment(string html)
    {
        int fragStart = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        int fragEnd = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (fragStart >= 0 && fragEnd > fragStart)
        {
            int contentStart = fragStart + "<!--StartFragment-->".Length;
            return html.Substring(contentStart, fragEnd - contentStart);
        }

        int bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0)
        {
            int bodyOpenEnd = html.IndexOf('>', bodyStart);
            int bodyEnd = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyOpenEnd >= 0 && bodyEnd > bodyOpenEnd)
            {
                return html.Substring(bodyOpenEnd + 1, bodyEnd - bodyOpenEnd - 1);
            }
        }

        if (html.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            int firstTag = html.IndexOf('<');
            if (firstTag >= 0)
            {
                return html.Substring(firstTag);
            }
        }

        return html;
    }

    /// <summary>
    /// Converts RTF into CF_HTML so Chromium/Electron/web editors preserve colors, weights, lists, and tables.
    /// Must be invoked on an STA thread (such as the WPF UI thread).
    /// </summary>
    public static string? ConvertRtfToCfHtml(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return null;

        FlowDocument? doc = LoadFlowDocumentFromRtf(rtf);
        if (doc == null) return null;

        var sb = new StringBuilder(Math.Max(256, rtf.Length));
        sb.Append("<div style=\"white-space:pre-wrap;font-family:'Microsoft YaHei UI',sans-serif;\">");
        SerializeBlocksToHtml(doc.Blocks, sb);
        sb.Append("</div>");

        return BuildCfHtml(sb.ToString());
    }

    /// <summary>
    /// Renders RTF into a theme-aware WPF FlowDocument with proportional font scaling to match baseFontSize.
    /// Must be invoked on an STA thread.
    /// </summary>
    public static FlowDocument BuildRtfPreviewDocument(string rtf, double baseFontSize, string baseFontFamily, bool isDarkTheme = true)
    {
        var palette = isDarkTheme ? DarkPreviewPalette : LightPreviewPalette;
        var doc = LoadFlowDocumentFromRtf(rtf);
        if (doc == null)
        {
            return CreateFallbackDocument(ClipItem.ConvertRtfToPlainText(rtf), baseFontSize, baseFontFamily, palette);
        }

        ApplyBaseDocumentStyle(doc, baseFontSize, baseFontFamily, palette);
        ScaleRtfFontSizes(doc, baseFontSize);
        AdaptDocumentColorsForTheme(doc, palette);
        return doc;
    }

    private static FlowDocument? LoadFlowDocumentFromRtf(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return null;
        string trimmed = rtf.TrimStart('\0', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var doc = new FlowDocument();
            var range = new TextRange(doc.ContentStart, doc.ContentEnd);
            string escapedRtf = EscapeNonAsciiForRtf(trimmed);
            byte[] bytes = Encoding.ASCII.GetBytes(escapedRtf);
            using var ms = new MemoryStream(bytes);
            range.Load(ms, DataFormats.Rtf);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeNonAsciiForRtf(string rtf)
    {
        bool hasNonAscii = false;
        foreach (char ch in rtf)
        {
            if (ch > 0x7F)
            {
                hasNonAscii = true;
                break;
            }
        }
        if (!hasNonAscii) return rtf;

        var sb = new StringBuilder(rtf.Length + 64);
        foreach (char ch in rtf)
        {
            if (ch <= 0x7F)
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append("\\u").Append((short)ch).Append('?');
            }
        }
        return sb.ToString();
    }

    private static void ApplyBaseDocumentStyle(FlowDocument doc, double baseFontSize, string baseFontFamily, PreviewThemePalette palette)
    {
        doc.PagePadding = new Thickness(2);
        doc.Background = Brushes.Transparent;
        doc.Foreground = palette.DefaultText;
        doc.FontSize = baseFontSize;
        if (!string.IsNullOrWhiteSpace(baseFontFamily))
        {
            doc.FontFamily = new FontFamily(baseFontFamily);
        }
        doc.LineHeight = double.NaN;
    }

    private static FlowDocument CreateFallbackDocument(string plainText, double baseFontSize, string baseFontFamily, PreviewThemePalette palette)
    {
        var doc = new FlowDocument();
        ApplyBaseDocumentStyle(doc, baseFontSize, baseFontFamily, palette);
        doc.Blocks.Add(new Paragraph(new Run(plainText ?? string.Empty)) { Margin = new Thickness(0) });
        return doc;
    }

    private static void ScaleRtfFontSizes(FlowDocument doc, double targetBaseSize)
    {
        var runSizes = new List<double>();
        var localElements = new List<(TextElement Element, double Size)>();
        CollectRtfElements(doc.Blocks, runSizes, localElements);

        double baseline = 13.3333;
        if (runSizes.Count > 0)
        {
            runSizes.Sort();
            baseline = runSizes[runSizes.Count / 2];
            if (baseline < 6 || baseline > 96) baseline = 13.3333;
        }

        double scale = targetBaseSize / baseline;
        double minSize = Math.Max(10.0, targetBaseSize * 0.85);
        double maxSize = targetBaseSize * 2.2;

        foreach (var (el, origSize) in localElements)
        {
            el.FontSize = SnapScaledSize(origSize * scale, targetBaseSize, minSize, maxSize);
        }
    }

    private static void CollectRtfElements(BlockCollection blocks, List<double> runSizes, List<(TextElement Element, double Size)> localElements)
    {
        foreach (Block block in blocks)
        {
            RecordIfLocalFontSize(block, localElements);

            switch (block)
            {
                case Paragraph p:
                    p.LineHeight = double.NaN;
                    p.Margin = new Thickness(p.Margin.Left, p.Margin.Top, p.Margin.Right, Math.Max(p.Margin.Bottom, 2));
                    CollectRtfInlines(p.Inlines, runSizes, localElements);
                    break;
                case List list:
                    foreach (ListItem li in list.ListItems) CollectRtfElements(li.Blocks, runSizes, localElements);
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells) CollectRtfElements(cell.Blocks, runSizes, localElements);
                        }
                    }
                    break;
                case Section sec:
                    CollectRtfElements(sec.Blocks, runSizes, localElements);
                    break;
            }
        }
    }

    private static void CollectRtfInlines(InlineCollection inlines, List<double> runSizes, List<(TextElement Element, double Size)> localElements)
    {
        foreach (Inline inline in inlines)
        {
            RecordIfLocalFontSize(inline, localElements);

            if (inline is Run r)
            {
                if (!string.IsNullOrEmpty(r.Text) && !double.IsNaN(r.FontSize) && r.FontSize > 4 && r.FontSize < 120)
                {
                    runSizes.Add(r.FontSize);
                }
            }
            else if (inline is Span span)
            {
                CollectRtfInlines(span.Inlines, runSizes, localElements);
            }
        }
    }

    private static void RecordIfLocalFontSize(TextElement el, List<(TextElement Element, double Size)> localElements)
    {
        object local = el.ReadLocalValue(TextElement.FontSizeProperty);
        if (local != DependencyProperty.UnsetValue && local is double d && !double.IsNaN(d) && d > 4 && d < 120)
        {
            localElements.Add((el, d));
        }
    }

    private static double SnapScaledSize(double value, double targetBaseSize, double minSize, double maxSize)
    {
        double clamped = Math.Clamp(value, minSize, maxSize);
        if (Math.Abs(clamped - targetBaseSize) < 0.15) return targetBaseSize;
        return Math.Round(clamped, 1);
    }

    #region RTF -> HTML Serialization

    private static void SerializeBlocksToHtml(BlockCollection blocks, StringBuilder sb)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    SerializeParagraphToHtml(p, sb);
                    break;
                case List list:
                    string listTag = list.MarkerStyle == TextMarkerStyle.Decimal ? "ol" : "ul";
                    sb.Append('<').Append(listTag).Append(" style=\"margin:4px 0 4px 20px;padding:0;\">");
                    foreach (ListItem li in list.ListItems)
                    {
                        sb.Append("<li>");
                        SerializeBlocksToHtml(li.Blocks, sb);
                        sb.Append("</li>");
                    }
                    sb.Append("</").Append(listTag).Append('>');
                    break;
                case Table table:
                    sb.Append("<table style=\"border-collapse:collapse;margin:4px 0;\">");
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            sb.Append("<tr>");
                            foreach (TableCell cell in row.Cells)
                            {
                                sb.Append("<td style=\"border:1px solid #cbd5e1;padding:4px 8px;\">");
                                SerializeBlocksToHtml(cell.Blocks, sb);
                                sb.Append("</td>");
                            }
                            sb.Append("</tr>");
                        }
                    }
                    sb.Append("</table>");
                    break;
                case Section sec:
                    sb.Append("<div>");
                    SerializeBlocksToHtml(sec.Blocks, sb);
                    sb.Append("</div>");
                    break;
            }
        }
    }

    private static void SerializeParagraphToHtml(Paragraph p, StringBuilder sb)
    {
        var style = new StringBuilder("margin:0;");
        if (p.TextAlignment != TextAlignment.Left)
        {
            style.Append("text-align:").Append(p.TextAlignment.ToString().ToLowerInvariant()).Append(';');
        }
        if (TryGetColor(p.Background, out Color bg) && bg.A > 10)
        {
            style.Append("background-color:").Append(ToCssHex(bg)).Append(';');
        }

        sb.Append("<p style=\"").Append(style).Append("\">");
        if (p.Inlines.Count == 0)
        {
            sb.Append("<br/>");
        }
        else
        {
            SerializeInlinesToHtml(p.Inlines, sb, bg.A > 10);
        }
        sb.Append("</p>");
    }

    private static void SerializeInlinesToHtml(InlineCollection inlines, StringBuilder sb, bool hasExplicitParentBg)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case LineBreak:
                    sb.Append("<br/>");
                    break;
                case Run run:
                    SerializeRunToHtml(run, sb, hasExplicitParentBg);
                    break;
                case Hyperlink link:
                    string href = link.NavigateUri?.AbsoluteUri ?? string.Empty;
                    sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">");
                    SerializeInlinesToHtml(link.Inlines, sb, hasExplicitParentBg);
                    sb.Append("</a>");
                    break;
                case Span span:
                    bool bold = span is Bold || span.FontWeight >= FontWeights.SemiBold;
                    bool italic = span is Italic || span.FontStyle == FontStyles.Italic;
                    bool underline = span is Underline || (span.TextDecorations?.Contains(TextDecorations.Underline[0]) ?? false);
                    bool spanBg = hasExplicitParentBg || (TryGetColor(span.Background, out Color sbg) && sbg.A > 10);
                    string spanStyle = BuildInlineStyle(span, spanBg);
                    bool wrap = !string.IsNullOrEmpty(spanStyle) || bold || italic || underline;
                    if (wrap)
                    {
                        sb.Append("<span style=\"");
                        sb.Append(spanStyle);
                        if (bold) sb.Append("font-weight:bold;");
                        if (italic) sb.Append("font-style:italic;");
                        if (underline) sb.Append("text-decoration:underline;");
                        sb.Append("\">");
                    }
                    SerializeInlinesToHtml(span.Inlines, sb, spanBg);
                    if (wrap) sb.Append("</span>");
                    break;
            }
        }
    }

    private static void SerializeRunToHtml(Run run, StringBuilder sb, bool hasExplicitParentBg)
    {
        string text = WebUtility.HtmlEncode(run.Text);
        if (string.IsNullOrEmpty(text)) return;

        bool hasBg = hasExplicitParentBg || (TryGetColor(run.Background, out Color rbg) && rbg.A > 10);
        string css = BuildInlineStyle(run, hasBg);
        if (string.IsNullOrEmpty(css))
        {
            sb.Append(text);
        }
        else
        {
            sb.Append("<span style=\"").Append(css).Append("\">").Append(text).Append("</span>");
        }
    }

    private static string BuildInlineStyle(TextElement el, bool hasExplicitBg)
    {
        var sb = new StringBuilder();

        if (TryGetColor(el.Background, out Color bg) && bg.A > 10)
        {
            sb.Append("background-color:").Append(ToCssHex(bg)).Append(';');
        }

        if (TryGetColor(el.Foreground, out Color fg) && fg.A > 10)
        {
            // Omit near-black default color unless inside a light background so both light and dark paste targets adapt naturally
            bool isDefaultBlack = fg.R < 20 && fg.G < 20 && fg.B < 20;
            if (!isDefaultBlack || hasExplicitBg)
            {
                sb.Append("color:").Append(ToCssHex(fg)).Append(';');
            }
        }

        if (el.FontWeight >= FontWeights.SemiBold)
        {
            sb.Append("font-weight:bold;");
        }
        if (el.FontStyle == FontStyles.Italic)
        {
            sb.Append("font-style:italic;");
        }

        if (el is Inline inl && inl.TextDecorations != null && inl.TextDecorations.Count > 0)
        {
            bool hasUnderline = false;
            bool hasStrike = false;
            foreach (var td in inl.TextDecorations)
            {
                if (td.Location == TextDecorationLocation.Underline) hasUnderline = true;
                if (td.Location == TextDecorationLocation.Strikethrough) hasStrike = true;
            }
            if (hasUnderline) sb.Append("text-decoration:underline;");
            else if (hasStrike) sb.Append("text-decoration:line-through;");
        }

        if (Math.Abs(el.FontSize - 12.0) > 1.5 && el.FontSize > 6 && el.FontSize < 96)
        {
            double px = el.FontSize;
            sb.Append("font-size:").Append(px.ToString("0.#", CultureInfo.InvariantCulture)).Append("px;");
        }

        return sb.ToString();
    }

    #endregion

    #region Theme Color Adaptation

    private static void AdaptDocumentColorsForTheme(FlowDocument doc, PreviewThemePalette palette)
    {
        AdaptBlocksForTheme(doc.Blocks, null, palette);
    }

    private static void AdaptBlocksForTheme(BlockCollection blocks, Color? inheritedBg, PreviewThemePalette palette)
    {
        foreach (Block block in blocks)
        {
            Color? localBg = GetEffectiveOpaqueColor(block.Background) ?? inheritedBg;

            switch (block)
            {
                case Paragraph p:
                    AdaptInlinesForTheme(p.Inlines, localBg, palette);
                    break;
                case List list:
                    list.Foreground = palette.DefaultText;
                    foreach (ListItem li in list.ListItems)
                    {
                        Color? liBg = GetEffectiveOpaqueColor(li.Background) ?? localBg;
                        AdaptBlocksForTheme(li.Blocks, liBg, palette);
                    }
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                Color? cellBg = GetEffectiveOpaqueColor(cell.Background) ?? localBg;
                                if (cell.BorderBrush != null && TryGetColor(cell.BorderBrush, out Color bc))
                                {
                                    double borderLum = GetLuminance(bc);
                                    if ((palette.IsDark && borderLum < 0.2) || (!palette.IsDark && borderLum > 0.85))
                                    {
                                        cell.BorderBrush = palette.SubtleBorder;
                                    }
                                }
                                AdaptBlocksForTheme(cell.Blocks, cellBg, palette);
                            }
                        }
                    }
                    break;
                case Section sec:
                    AdaptBlocksForTheme(sec.Blocks, localBg, palette);
                    break;
            }
        }
    }

    private static void AdaptInlinesForTheme(InlineCollection inlines, Color? inheritedBg, PreviewThemePalette palette)
    {
        foreach (Inline inline in inlines)
        {
            Color? localBg = GetEffectiveOpaqueColor(inline.Background) ?? inheritedBg;

            if (palette.IsDark)
            {
                bool hasLightBg = localBg.HasValue && GetLuminance(localBg.Value) > 0.45;
                if (!hasLightBg && TryGetColor(inline.Foreground, out Color fg) && fg.A > 10)
                {
                    inline.Foreground = FreezeBrush(AdaptForegroundForDarkBg(fg, palette));
                }
                else if (!hasLightBg && inline.Foreground == null && inline is not Hyperlink)
                {
                    inline.Foreground = palette.DefaultText;
                }
            }
            else
            {
                bool hasDarkBg = localBg.HasValue && GetLuminance(localBg.Value) < 0.45;
                if (!hasDarkBg && TryGetColor(inline.Foreground, out Color fg) && fg.A > 10)
                {
                    inline.Foreground = FreezeBrush(AdaptForegroundForLightBg(fg, palette));
                }
                else if (!hasDarkBg && inline.Foreground == null && inline is not Hyperlink)
                {
                    inline.Foreground = palette.DefaultText;
                }
            }

            if (inline is Hyperlink link)
            {
                link.Foreground = palette.Link;
                AdaptInlinesForTheme(link.Inlines, localBg, palette);
            }
            else if (inline is Span span)
            {
                AdaptInlinesForTheme(span.Inlines, localBg, palette);
            }
        }
    }

    private static Color? GetEffectiveOpaqueColor(Brush? brush)
    {
        if (brush is SolidColorBrush sb && sb.Color.A > 20)
        {
            return sb.Color;
        }
        return null;
    }

    private static Color AdaptForegroundForDarkBg(Color c, PreviewThemePalette palette)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        double lum = GetLuminance(c);

        if (max - min <= 28 && lum < 0.45)
        {
            return palette.DefaultTextColor;
        }

        if (lum < 0.42 && max > 0)
        {
            double boost = Math.Max(1.35, Math.Sqrt(0.45 / Math.Max(lum, 0.02)));
            double r = c.R * boost;
            double g = c.G * boost;
            double b = c.B * boost;
            byte lift = 55;
            return Color.FromArgb(
                c.A,
                (byte)Math.Clamp(r + (255 - r) * (lift / 255.0) * 0.45, 0, 255),
                (byte)Math.Clamp(g + (255 - g) * (lift / 255.0) * 0.45, 0, 255),
                (byte)Math.Clamp(b + (255 - b) * (lift / 255.0) * 0.45, 0, 255));
        }

        return c;
    }

    private static Color AdaptForegroundForLightBg(Color c, PreviewThemePalette palette)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        double lum = GetLuminance(c);

        // White or faint silver text copied from dark-themed editors -> dark neutral text
        if (max - min <= 28 && lum > 0.55)
        {
            return lum > 0.80 ? palette.DefaultTextColor : palette.MutedDarkTextColor;
        }

        // Very pale saturated text (yellow/cyan/pastel on white) -> deepen proportionally to preserve hue contrast
        if (lum > 0.65 && max > 0)
        {
            double scale = 0.48 / lum;
            return Color.FromArgb(
                c.A,
                (byte)Math.Clamp(c.R * scale, 0, 255),
                (byte)Math.Clamp(c.G * scale, 0, 255),
                (byte)Math.Clamp(c.B * scale, 0, 255));
        }

        return c;
    }

    private static double GetLuminance(Color c)
    {
        return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    }

    private static bool TryGetColor(Brush? brush, out Color color)
    {
        if (brush is SolidColorBrush sb)
        {
            color = sb.Color;
            return true;
        }
        color = default;
        return false;
    }

    private static string ToCssHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    #endregion

    #region HTML -> FlowDocument Preview Renderer

    private const int MaxHtmlRenderChars = 180_000;

    private sealed class RenderStyle
    {
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public bool IsUnderline { get; set; }
        public bool IsStrike { get; set; }
        public bool IsCode { get; set; }
        public bool IsHidden { get; set; }
        public BaselineAlignment Baseline { get; set; } = BaselineAlignment.Baseline;
        public double FontSize { get; set; }
        public Brush? Foreground { get; set; }
        public Brush? Background { get; set; }
        public string? Href { get; set; }

        public RenderStyle Clone() => new()
        {
            IsBold = IsBold,
            IsItalic = IsItalic,
            IsUnderline = IsUnderline,
            IsStrike = IsStrike,
            IsCode = IsCode,
            IsHidden = IsHidden,
            Baseline = Baseline,
            FontSize = FontSize,
            Foreground = Foreground,
            Background = Background,
            Href = Href
        };
    }

    /// <summary>
    /// Renders HTML clipboard payloads into a clean, theme-aware FlowDocument with clickable hyperlinks.
    /// Must be invoked on an STA thread.
    /// </summary>
    public static FlowDocument BuildHtmlPreviewDocument(
        string rawHtml,
        string? baseUrl,
        double baseFontSize,
        string baseFontFamily,
        Action<string> onLinkClick,
        bool isDarkTheme = true)
    {
        var palette = isDarkTheme ? DarkPreviewPalette : LightPreviewPalette;
        var doc = new FlowDocument();
        ApplyBaseDocumentStyle(doc, baseFontSize, baseFontFamily, palette);

        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            doc.Blocks.Add(new Paragraph(new Run("(空内容)")) { Margin = new Thickness(0) });
            return doc;
        }

        string fragment = ExtractHtmlFragment(rawHtml);
        fragment = Regex.Replace(fragment, @"<!--[\s\S]*?-->", string.Empty);
        fragment = Regex.Replace(fragment, @"<(head|style|script|noscript|svg|template)\b[^>]*>[\s\S]*?</\1>", string.Empty, RegexOptions.IgnoreCase);

        if (fragment.Length > MaxHtmlRenderChars)
        {
            fragment = fragment[..MaxHtmlRenderChars] + "<p><i>[内容过长，已截断预览...]</i></p>";
        }

        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out baseUri);
        }

        var builder = new HtmlFlowBuilder(doc, baseUri, baseFontSize, palette, onLinkClick);
        builder.Parse(fragment);

        if (doc.Blocks.Count == 0)
        {
            string fallback = ClipItem.ConvertHtmlToPlainText(rawHtml);
            doc.Blocks.Add(new Paragraph(new Run(fallback)) { Margin = new Thickness(0) });
        }

        return doc;
    }

    private sealed class HtmlFlowBuilder
    {
        private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "br", "hr", "img", "input", "meta", "link", "wbr", "col", "source", "area", "base", "embed", "param", "track"
        };

        private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "div", "section", "article", "header", "footer", "aside", "main", "nav",
            "figure", "figcaption", "address", "h1", "h2", "h3", "h4", "h5", "h6"
        };

        private static readonly Regex TagRegex = new(
            @"<(?<close>/)?(?<name>[a-zA-Z0-9:_-]+)(?<attrs>(?:\s+[^<>""']*(?:""[^""]*""|'[^']*')?)*?)\s*(?<self>/)?>",
            RegexOptions.Compiled);

        private static readonly Regex AttrRegex = new(
            @"(?<key>[a-zA-Z0-9:_-]+)\s*(?:=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<uq>[^\s>]+)))?",
            RegexOptions.Compiled);

        private readonly FlowDocument _doc;
        private readonly Uri? _baseUri;
        private readonly double _baseFontSize;
        private readonly PreviewThemePalette _palette;
        private readonly Action<string> _onLinkClick;

        private readonly Stack<(string Tag, RenderStyle Style)> _tagStack = new();
        private readonly Stack<BlockCollection> _containerStack = new();
        private Paragraph? _currentParagraph;
        private int _preDepth;
        private int _hiddenDepth;

        private TableRowGroup? _currentRowGroup;
        private TableRow? _currentRow;
        private List? _currentList;
        private ListItem? _currentListItem;

        public HtmlFlowBuilder(FlowDocument doc, Uri? baseUri, double baseFontSize, PreviewThemePalette palette, Action<string> onLinkClick)
        {
            _doc = doc;
            _baseUri = baseUri;
            _baseFontSize = baseFontSize;
            _palette = palette;
            _onLinkClick = onLinkClick;

            _containerStack.Push(_doc.Blocks);
            _tagStack.Push(("root", new RenderStyle { FontSize = baseFontSize, Foreground = _palette.DefaultText }));
        }

        public void Parse(string html)
        {
            int lastIdx = 0;
            foreach (Match m in TagRegex.Matches(html))
            {
                if (m.Index > lastIdx)
                {
                    AppendText(html.Substring(lastIdx, m.Index - lastIdx));
                }

                bool isClose = m.Groups["close"].Success;
                bool isSelfClosing = m.Groups["self"].Success;
                string tagName = m.Groups["name"].Value.ToLowerInvariant();
                string attrsRaw = m.Groups["attrs"].Value;

                if (isClose)
                {
                    HandleCloseTag(tagName);
                }
                else
                {
                    HandleOpenTag(tagName, attrsRaw, isSelfClosing || VoidTags.Contains(tagName));
                }

                lastIdx = m.Index + m.Length;
            }

            if (lastIdx < html.Length)
            {
                AppendText(html.Substring(lastIdx));
            }
        }

        private void HandleOpenTag(string tag, string attrsRaw, bool isVoid)
        {
            if (_hiddenDepth > 0)
            {
                if (!isVoid) _tagStack.Push((tag, CurrentStyle.Clone()));
                return;
            }

            var attrs = ParseAttributes(attrsRaw);
            RenderStyle nextStyle = CurrentStyle.Clone();
            TextAlignment? blockAlign = ApplyInlineCssAndStyleAttrs(attrs, nextStyle);

            if (nextStyle.IsHidden)
            {
                if (!isVoid)
                {
                    _hiddenDepth++;
                    _tagStack.Push((tag, nextStyle));
                }
                return;
            }

            switch (tag)
            {
                case "br":
                    EnsureParagraph().Inlines.Add(new LineBreak());
                    return;

                case "hr":
                    CloseCurrentParagraph();
                    var hrPara = new Paragraph(new Run(" "))
                    {
                        BorderBrush = _palette.SubtleBorder,
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Margin = new Thickness(0, 8, 0, 8),
                        FontSize = 2
                    };
                    CurrentBlocks.Add(hrPara);
                    return;

                case "img":
                    attrs.TryGetValue("alt", out string? alt);
                    string label = string.IsNullOrWhiteSpace(alt) ? "[图片]" : $"[图片: {alt.Trim()}]";
                    EnsureParagraph().Inlines.Add(new Run(label)
                    {
                        Foreground = _palette.ImageBadgeFg,
                        FontSize = Math.Max(10, _baseFontSize - 1)
                    });
                    return;
            }

            if (tag == "pre")
            {
                CloseCurrentParagraph();
                var codeSection = new Section
                {
                    Background = _palette.CodeBg,
                    BorderBrush = _palette.SubtleBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 4, 0, 8),
                    FontFamily = DefaultCodeFont,
                    FontSize = Math.Max(11, _baseFontSize - 0.5)
                };
                CurrentBlocks.Add(codeSection);
                _containerStack.Push(codeSection.Blocks);
                _preDepth++;
                nextStyle.IsCode = true;
                _tagStack.Push((tag, nextStyle));
                return;
            }

            if (tag == "blockquote")
            {
                CloseCurrentParagraph();
                var quoteSection = new Section
                {
                    BorderBrush = _palette.QuoteBorder,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Margin = new Thickness(0, 4, 0, 8),
                    Foreground = _palette.QuoteText
                };
                CurrentBlocks.Add(quoteSection);
                _containerStack.Push(quoteSection.Blocks);
                nextStyle.Foreground = _palette.QuoteText;
                _tagStack.Push((tag, nextStyle));
                return;
            }

            if (tag is "ul" or "ol")
            {
                CloseCurrentParagraph();
                var list = new List
                {
                    MarkerStyle = tag == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(18, 2, 0, 6),
                    Padding = new Thickness(0)
                };
                CurrentBlocks.Add(list);
                _currentList = list;
                _tagStack.Push((tag, nextStyle));
                return;
            }

            if (tag == "li")
            {
                CloseCurrentParagraph();
                if (_currentList != null)
                {
                    var li = new ListItem { Margin = new Thickness(0, 1, 0, 2) };
                    _currentList.ListItems.Add(li);
                    _currentListItem = li;
                    _containerStack.Push(li.Blocks);
                }
                else
                {
                    EnsureParagraph(new Thickness(10, 0, 0, 3)).Inlines.Add(new Run("• ") { Foreground = _palette.QuoteBorder });
                }
                _tagStack.Push((tag, nextStyle));
                return;
            }

            if (tag == "table")
            {
                CloseCurrentParagraph();
                var table = new Table
                {
                    CellSpacing = 0,
                    Margin = new Thickness(0, 4, 0, 8),
                    BorderBrush = _palette.SubtleBorder,
                    BorderThickness = new Thickness(1)
                };
                var rg = new TableRowGroup();
                table.RowGroups.Add(rg);
                CurrentBlocks.Add(table);
                _currentRowGroup = rg;
                _tagStack.Push((tag, nextStyle));
                return;
            }

            if (tag == "tr" && _currentRowGroup != null)
            {
                CloseCurrentParagraph();
                var row = new TableRow();
                _currentRowGroup.Rows.Add(row);
                _currentRow = row;
                _tagStack.Push((tag, nextStyle));
                return;
            }

            if ((tag is "td" or "th") && _currentRow != null)
            {
                CloseCurrentParagraph();
                var cell = new TableCell
                {
                    BorderBrush = _palette.SubtleBorder,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 5, 8, 5)
                };
                if (tag == "th")
                {
                    cell.Background = _palette.TableHeaderBg;
                    nextStyle.IsBold = true;
                }
                _currentRow.Cells.Add(cell);
                _containerStack.Push(cell.Blocks);
                _tagStack.Push((tag, nextStyle));
                return;
            }

            if (BlockTags.Contains(tag))
            {
                CloseCurrentParagraph();
                ApplyHeadingStyle(tag, nextStyle, out Thickness margin);
                var p = EnsureParagraph(margin);
                if (blockAlign.HasValue) p.TextAlignment = blockAlign.Value;
                if (!isVoid) _tagStack.Push((tag, nextStyle));
                return;
            }

            ApplyInlineTagStyle(tag, attrs, nextStyle);
            if (!isVoid)
            {
                _tagStack.Push((tag, nextStyle));
            }
        }

        private void HandleCloseTag(string tag)
        {
            if (!_tagStack.ContainsTag(tag)) return;

            while (_tagStack.Count > 1)
            {
                var (topTag, style) = _tagStack.Pop();
                if (_hiddenDepth > 0 && style.IsHidden) _hiddenDepth = Math.Max(0, _hiddenDepth - 1);

                switch (topTag)
                {
                    case "pre":
                        CloseCurrentParagraph();
                        _preDepth = Math.Max(0, _preDepth - 1);
                        PopContainerSafe();
                        break;
                    case "blockquote":
                        CloseCurrentParagraph();
                        PopContainerSafe();
                        break;
                    case "li":
                        CloseCurrentParagraph();
                        if (_currentListItem != null)
                        {
                            PopContainerSafe();
                            _currentListItem = null;
                        }
                        break;
                    case "ul":
                    case "ol":
                        CloseCurrentParagraph();
                        _currentList = null;
                        break;
                    case "td":
                    case "th":
                        CloseCurrentParagraph();
                        PopContainerSafe();
                        break;
                    case "tr":
                        CloseCurrentParagraph();
                        _currentRow = null;
                        break;
                    case "table":
                        CloseCurrentParagraph();
                        _currentRow = null;
                        _currentRowGroup = null;
                        break;
                    default:
                        if (BlockTags.Contains(topTag))
                        {
                            CloseCurrentParagraph();
                        }
                        break;
                }

                if (string.Equals(topTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        private RenderStyle CurrentStyle => _tagStack.Peek().Style;
        private BlockCollection CurrentBlocks => _containerStack.Peek();

        private void PopContainerSafe()
        {
            if (_containerStack.Count > 1)
            {
                _containerStack.Pop();
            }
        }

        private Paragraph EnsureParagraph(Thickness? margin = null)
        {
            if (_currentParagraph == null)
            {
                _currentParagraph = new Paragraph
                {
                    Margin = margin ?? new Thickness(0, 0, 0, _preDepth > 0 ? 0 : 4),
                    LineHeight = _preDepth > 0 ? _baseFontSize * 1.45 : _baseFontSize * 1.6
                };
                CurrentBlocks.Add(_currentParagraph);
            }
            return _currentParagraph;
        }

        private void CloseCurrentParagraph()
        {
            if (_currentParagraph != null && _currentParagraph.Inlines.Count == 0)
            {
                CurrentBlocks.Remove(_currentParagraph);
            }
            _currentParagraph = null;
        }

        private void AppendText(string rawText)
        {
            if (_hiddenDepth > 0 || string.IsNullOrEmpty(rawText)) return;

            string decoded = WebUtility.HtmlDecode(rawText).Replace('\u00A0', ' ');
            string text;

            if (_preDepth > 0)
            {
                text = decoded.Replace("\r\n", "\n");
            }
            else
            {
                text = Regex.Replace(decoded, @"\s+", " ");
                if (string.IsNullOrWhiteSpace(text) && (_currentParagraph == null || _currentParagraph.Inlines.Count == 0))
                {
                    return;
                }
            }

            if (text.Length == 0) return;

            var style = CurrentStyle;
            var para = EnsureParagraph();

            var run = new Run(text);
            if (style.IsBold) run.FontWeight = FontWeights.SemiBold;
            if (style.IsItalic) run.FontStyle = FontStyles.Italic;
            if (style.Baseline != BaselineAlignment.Baseline) run.BaselineAlignment = style.Baseline;
            if (style.FontSize > 0 && Math.Abs(style.FontSize - _baseFontSize) > 0.2) run.FontSize = style.FontSize;

            if (style.IsCode && _preDepth == 0)
            {
                run.FontFamily = DefaultCodeFont;
                run.Background = _palette.InlineCodeBg;
                run.Foreground = _palette.InlineCodeFg;
            }
            else
            {
                if (style.Background != null) run.Background = style.Background;
                if (style.Foreground != null && string.IsNullOrEmpty(style.Href))
                {
                    run.Foreground = style.Foreground;
                }
            }

            var decorations = new TextDecorationCollection();
            if (style.IsUnderline) decorations.Add(TextDecorations.Underline);
            if (style.IsStrike) decorations.Add(TextDecorations.Strikethrough);
            if (decorations.Count > 0) run.TextDecorations = decorations;

            if (!string.IsNullOrWhiteSpace(style.Href))
            {
                var link = new Hyperlink(run)
                {
                    Foreground = _palette.Link,
                    Cursor = Cursors.Hand,
                    ToolTip = style.Href
                };
                if (!style.IsUnderline)
                {
                    link.TextDecorations = null;
                }
                string capturedUrl = style.Href;
                link.Click += (_, _) => _onLinkClick(capturedUrl);
                para.Inlines.Add(link);
            }
            else
            {
                para.Inlines.Add(run);
            }
        }

        private void ApplyHeadingStyle(string tag, RenderStyle style, out Thickness margin)
        {
            margin = tag switch
            {
                "p" => new Thickness(0, 0, 0, 7),
                "h1" => new Thickness(0, 6, 0, 6),
                "h2" => new Thickness(0, 6, 0, 5),
                "h3" => new Thickness(0, 5, 0, 4),
                "h4" or "h5" or "h6" => new Thickness(0, 4, 0, 3),
                _ => new Thickness(0, 0, 0, 3)
            };

            switch (tag)
            {
                case "h1": style.IsBold = true; style.FontSize = _baseFontSize + 5; break;
                case "h2": style.IsBold = true; style.FontSize = _baseFontSize + 3.5; break;
                case "h3": style.IsBold = true; style.FontSize = _baseFontSize + 2; break;
                case "h4":
                case "h5":
                case "h6": style.IsBold = true; style.FontSize = _baseFontSize + 0.5; break;
            }
        }

        private void ApplyInlineTagStyle(string tag, Dictionary<string, string> attrs, RenderStyle style)
        {
            switch (tag)
            {
                case "b":
                case "strong":
                    style.IsBold = true;
                    break;
                case "i":
                case "em":
                    style.IsItalic = true;
                    break;
                case "u":
                case "ins":
                    style.IsUnderline = true;
                    break;
                case "s":
                case "del":
                case "strike":
                    style.IsStrike = true;
                    break;
                case "code":
                case "kbd":
                case "samp":
                case "tt":
                    style.IsCode = true;
                    break;
                case "mark":
                    style.Background = _palette.MarkBg;
                    style.Foreground = _palette.MarkFg;
                    break;
                case "sup":
                    style.Baseline = BaselineAlignment.Superscript;
                    style.FontSize = Math.Max(9, _baseFontSize * 0.78);
                    break;
                case "sub":
                    style.Baseline = BaselineAlignment.Subscript;
                    style.FontSize = Math.Max(9, _baseFontSize * 0.78);
                    break;
                case "a":
                    if (attrs.TryGetValue("href", out string? rawHref))
                    {
                        string? resolved = ResolveHref(rawHref);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            style.Href = resolved;
                        }
                    }
                    break;
            }
        }

        private string? ResolveHref(string rawHref)
        {
            if (string.IsNullOrWhiteSpace(rawHref)) return null;
            string clean = WebUtility.HtmlDecode(rawHref).Trim();
            if (clean.StartsWith("#") || clean.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;

            if (ClipItem.IsWebUrl(clean)) return clean;

            if (_baseUri != null && Uri.TryCreate(_baseUri, clean, out var combined))
            {
                string abs = combined.AbsoluteUri;
                if (ClipItem.IsWebUrl(abs)) return abs;
            }
            return null;
        }

        private TextAlignment? ApplyInlineCssAndStyleAttrs(Dictionary<string, string> attrs, RenderStyle style)
        {
            TextAlignment? align = null;
            Color? fg = null;
            Color? bg = null;

            if (attrs.TryGetValue("color", out string? attrColor) && TryParseCssColor(attrColor, out Color c1))
            {
                fg = c1;
            }

            if (attrs.TryGetValue("style", out string? css) && !string.IsNullOrWhiteSpace(css))
            {
                foreach (string decl in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int colon = decl.IndexOf(':');
                    if (colon <= 0) continue;
                    string prop = decl[..colon].Trim().ToLowerInvariant();
                    string val = decl[(colon + 1)..].Trim();

                    switch (prop)
                    {
                        case "display" when val.Equals("none", StringComparison.OrdinalIgnoreCase):
                        case "visibility" when val.Equals("hidden", StringComparison.OrdinalIgnoreCase):
                            style.IsHidden = true;
                            break;
                        case "color":
                            if (TryParseCssColor(val, out Color cFg)) fg = cFg;
                            break;
                        case "background":
                        case "background-color":
                            if (TryParseCssColor(val, out Color cBg) && cBg.A > 20) bg = cBg;
                            break;
                        case "font-weight":
                            if (val.Equals("bold", StringComparison.OrdinalIgnoreCase) ||
                                val.Equals("bolder", StringComparison.OrdinalIgnoreCase) ||
                                (int.TryParse(val, out int fw) && fw >= 600))
                            {
                                style.IsBold = true;
                            }
                            break;
                        case "font-style":
                            if (val.Equals("italic", StringComparison.OrdinalIgnoreCase) ||
                                val.Equals("oblique", StringComparison.OrdinalIgnoreCase))
                            {
                                style.IsItalic = true;
                            }
                            break;
                        case "text-decoration":
                        case "text-decoration-line":
                            if (val.Contains("underline", StringComparison.OrdinalIgnoreCase)) style.IsUnderline = true;
                            if (val.Contains("line-through", StringComparison.OrdinalIgnoreCase)) style.IsStrike = true;
                            break;
                        case "text-align":
                            if (val.Equals("center", StringComparison.OrdinalIgnoreCase)) align = TextAlignment.Center;
                            else if (val.Equals("right", StringComparison.OrdinalIgnoreCase)) align = TextAlignment.Right;
                            break;
                        case "font-size":
                            if (TryParseCssFontSize(val, _baseFontSize, out double fs, out bool isRelative))
                            {
                                double scaled = isRelative ? fs : (fs / 14.0) * _baseFontSize;
                                style.FontSize = Math.Clamp(scaled, Math.Max(12, _baseFontSize * 0.88), _baseFontSize * 1.8);
                            }
                            break;
                    }
                }
            }

            if (bg.HasValue)
            {
                Color c = bg.Value;
                int chroma = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
                double bgLum = GetLuminance(c);
                bool dropContainerBg = _palette.IsDark
                    ? (bgLum > 0.72 && chroma < 32)
                    : (bgLum < 0.25 && chroma < 32);
                if (dropContainerBg)
                {
                    bg = null;
                }
                else
                {
                    style.Background = FreezeBrush(c);
                }
            }

            if (fg.HasValue)
            {
                if (_palette.IsDark)
                {
                    bool hasLightBg = bg.HasValue && GetLuminance(bg.Value) > 0.45;
                    Color adapted = hasLightBg ? fg.Value : AdaptForegroundForDarkBg(fg.Value, _palette);
                    style.Foreground = FreezeBrush(adapted);
                }
                else
                {
                    bool hasDarkBg = bg.HasValue && GetLuminance(bg.Value) < 0.45;
                    Color adapted = hasDarkBg ? fg.Value : AdaptForegroundForLightBg(fg.Value, _palette);
                    style.Foreground = FreezeBrush(adapted);
                }
            }

            return align;
        }

        private static Dictionary<string, string> ParseAttributes(string raw)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return dict;

            foreach (Match m in AttrRegex.Matches(raw))
            {
                string key = m.Groups["key"].Value;
                string val = m.Groups["dq"].Success ? m.Groups["dq"].Value
                           : m.Groups["sq"].Success ? m.Groups["sq"].Value
                           : m.Groups["uq"].Success ? m.Groups["uq"].Value
                           : string.Empty;
                dict[key] = val;
            }
            return dict;
        }

        private static bool TryParseCssColor(string value, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string v = value.Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (v.Equals("transparent", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("initial", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (v.StartsWith('#'))
            {
                try
                {
                    if (v.Length == 4)
                    {
                        v = $"#{v[1]}{v[1]}{v[2]}{v[2]}{v[3]}{v[3]}";
                    }
                    if (v.Length == 7 || v.Length == 9)
                    {
                        var obj = ColorConverter.ConvertFromString(v);
                        if (obj is Color c) { color = c; return true; }
                    }
                }
                catch { }
                return false;
            }

            var rgbMatch = Regex.Match(v, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([0-9.]+))?\s*\)", RegexOptions.IgnoreCase);
            if (rgbMatch.Success)
            {
                byte r = (byte)Math.Clamp(int.Parse(rgbMatch.Groups[1].Value), 0, 255);
                byte g = (byte)Math.Clamp(int.Parse(rgbMatch.Groups[2].Value), 0, 255);
                byte b = (byte)Math.Clamp(int.Parse(rgbMatch.Groups[3].Value), 0, 255);
                byte a = 255;
                if (rgbMatch.Groups[4].Success && double.TryParse(rgbMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha))
                {
                    a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
                }
                color = Color.FromArgb(a, r, g, b);
                return a > 0;
            }

            try
            {
                var obj = ColorConverter.ConvertFromString(v);
                if (obj is Color namedColor)
                {
                    color = namedColor;
                    return color.A > 0;
                }
            }
            catch { }

            return false;
        }

        private static bool TryParseCssFontSize(string val, double baseSize, out double px, out bool isRelative)
        {
            px = baseSize;
            isRelative = false;
            string v = val.Trim().ToLowerInvariant();
            if (v.EndsWith("px") && double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
            {
                px = p;
                return true;
            }
            if (v.EndsWith("pt") && double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double pt))
            {
                px = pt * 1.333;
                return true;
            }
            if (v.EndsWith("em") && double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double em))
            {
                px = em * baseSize;
                isRelative = true;
                return true;
            }
            return false;
        }
    }

    #endregion
}

internal static class TagStackExtensions
{
    public static bool ContainsTag<TStyle>(this Stack<(string Tag, TStyle Style)> stack, string tag)
    {
        foreach (var entry in stack)
        {
            if (string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
