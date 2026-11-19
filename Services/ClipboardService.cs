using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using ClipFlux.Models;
using ClipFlux.Native;

namespace ClipFlux.Services;

public class ClipboardService
{
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private bool _isListening;
    private bool _isInternalCopy;
    private DateTime _lastInternalCopyTime = DateTime.MinValue;
    private string? _lastCapturedFingerprint;
    private DateTime _lastCapturedTime = DateTime.MinValue;

    private readonly uint _cfIgnore1;
    private readonly uint _cfIgnore2;
    private readonly uint _cfHtml;
    private readonly uint _cfRtf;
    private readonly uint _cfRtfNoObj;
    private readonly uint _cfMozUrlPriv;
    private readonly uint _cfMozUrl;
    private readonly uint _cfUrlW;

    public event Action<ClipItem>? ClipCaptured;

    public ClipboardService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _cfIgnore1 = Win32.RegisterClipboardFormat("Clipboard Viewer Ignore");
        _cfIgnore2 = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _cfHtml = Win32.RegisterClipboardFormat("HTML Format");
        _cfRtf = Win32.RegisterClipboardFormat("Rich Text Format");
        _cfRtfNoObj = Win32.RegisterClipboardFormat("Rich Text Format Without Objects");
        _cfMozUrlPriv = Win32.RegisterClipboardFormat("text/x-moz-url-priv");
        _cfMozUrl = Win32.RegisterClipboardFormat("text/x-moz-url");
        _cfUrlW = Win32.RegisterClipboardFormat("UniformResourceLocatorW");
    }

    public void StartListening(Window window)
    {
        if (_isListening) return;

        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        Win32.AddClipboardFormatListener(_hwnd);
        _isListening = true;
    }

    public void StopListening()
    {
        if (!_isListening) return;

        if (_hwnd != IntPtr.Zero)
        {
            Win32.RemoveClipboardFormatListener(_hwnd);
            _hwndSource?.RemoveHook(WndProc);
        }
        _isListening = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_CLIPBOARDUPDATE)
        {
            if (_isInternalCopy || (DateTime.UtcNow - _lastInternalCopyTime).TotalMilliseconds < 600)
            {
                _isInternalCopy = false;
                handled = true;
                return IntPtr.Zero;
            }

            var staThread = new Thread(RunCaptureInStaWorker)
            {
                IsBackground = true
            };
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();

            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RunCaptureInStaWorker()
    {
        // Give the source application a brief moment to finish writing all delayed clipboard formats
        Thread.Sleep(45);

        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                string? mozUrlPriv = null;
                string? mozUrl = null;
                string? urlW = null;

                if (Win32.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        bool ignore = (_cfIgnore1 != 0 && Win32.IsClipboardFormatAvailable(_cfIgnore1)) ||
                                      (_cfIgnore2 != 0 && Win32.IsClipboardFormatAvailable(_cfIgnore2));
                        if (ignore) return;

                        mozUrlPriv = FirstNonEmptyLine(Win32.TryReadOpenClipboardStringFormat(_cfMozUrlPriv, isUnicode: true));
                        mozUrl = FirstNonEmptyLine(Win32.TryReadOpenClipboardStringFormat(_cfMozUrl, isUnicode: true));
                        urlW = FirstNonEmptyLine(Win32.TryReadOpenClipboardStringFormat(_cfUrlW, isUnicode: true));
                    }
                    finally
                    {
                        Win32.CloseClipboard();
                    }
                }

                IDataObject? dataObj = null;
                try
                {
                    dataObj = Clipboard.GetDataObject();
                }
                catch { }

                string windowTitle = Win32.GetForegroundWindowInfo(out string processName, out IntPtr fgHwnd);
                var item = new ClipItem
                {
                    AppName = processName,
                    AppTitle = windowTitle,
                    Created = DateTime.Now
                };

                // 1. Image
                if (TryCaptureImage(dataObj, item))
                {
                    EmitCaptured(item);
                    return;
                }

                // 2. Files / FileDrop
                if (TryCaptureFiles(dataObj, item))
                {
                    EmitCaptured(item);
                    return;
                }

                // 3. Rich content (HTML / RTF / combined multi-format)
                string? html = TryGetStringFormat(dataObj, DataFormats.Html);
                string? rtf = TryGetStringFormat(dataObj, DataFormats.Rtf);
                string? unicodeText = TryGetStringFormat(dataObj, DataFormats.UnicodeText) ?? TryGetStringFormat(dataObj, DataFormats.Text);

                if (!string.IsNullOrWhiteSpace(html) || !string.IsNullOrWhiteSpace(rtf))
                {
                    bool hasHtml = !string.IsNullOrWhiteSpace(html);
                    bool hasRtf = !string.IsNullOrWhiteSpace(rtf);
                    bool hasSourceUrl = hasHtml && !string.IsNullOrWhiteSpace(ClipItem.ExtractSourceUrlFromHtml(html));
                    bool isBrowser = BrowserUrlService.IsBrowserProcess(processName);

                    bool classifyAsHtml = hasHtml && (!hasRtf || hasSourceUrl || isBrowser);
                    item.Type = classifyAsHtml ? ClipType.Html : ClipType.RichText;

                    if (item.Type == ClipType.Html)
                    {
                        string resolvedUrl = ResolveBestHtmlUrl(unicodeText, html!, mozUrlPriv, mozUrl, urlW, fgHwnd, processName);
                        item.Url = resolvedUrl;

                        string? existingSrc = ClipItem.ExtractSourceUrlFromHtml(html!);
                        if (!string.IsNullOrWhiteSpace(resolvedUrl) &&
                            (string.IsNullOrWhiteSpace(existingSrc) ||
                             (BrowserUrlService.IsOriginOnlyUrl(existingSrc) && !BrowserUrlService.IsOriginOnlyUrl(resolvedUrl))))
                        {
                            html = RichContentService.NormalizeCfHtml(html!, resolvedUrl);
                        }

                        string previewText = !string.IsNullOrWhiteSpace(unicodeText)
                            ? unicodeText
                            : ClipItem.ConvertHtmlToPlainText(html!);
                        item.Title = SanitizeTitle(previewText);

                        string bookmarkName = ClipItem.ConvertHtmlToPlainText(html!);
                        if (!string.IsNullOrWhiteSpace(item.Url) && ClipItem.IsWebUrl(unicodeText) &&
                            !string.IsNullOrWhiteSpace(bookmarkName) && !string.Equals(bookmarkName, item.Url, StringComparison.OrdinalIgnoreCase))
                        {
                            item.Description = $"{bookmarkName} • 网页链接";
                        }
                        else
                        {
                            item.Description = $"{html!.Length} 字符 HTML";
                        }
                    }
                    else
                    {
                        string previewText = !string.IsNullOrWhiteSpace(unicodeText)
                            ? unicodeText
                            : ClipItem.ConvertRtfToPlainText(rtf!);
                        item.Title = SanitizeTitle(previewText);
                        item.Description = $"{rtf!.Length} 字符 富文本";
                    }

                    var entries = new List<(int FormatId, string FormatName, byte[] Data)>();
                    if (hasHtml)
                    {
                        entries.Add(((int)_cfHtml, "HTML Format", Encoding.UTF8.GetBytes(html! + "\0")));
                    }
                    if (hasRtf)
                    {
                        entries.Add(((int)_cfRtf, "Rich Text Format", EncodeRtfBytes(rtf!)));
                    }
                    if (!string.IsNullOrEmpty(unicodeText))
                    {
                        entries.Add(((int)Win32.CF_UNICODETEXT, "CF_UNICODETEXT", Encoding.Unicode.GetBytes(unicodeText + "\0")));
                    }

                    byte[] container = ClipItem.BuildMultiFormatContainer(entries);
                    item.Data = CompressBytes(container);
                    item.DataSize = item.Data.Length;
                    EmitCaptured(item);
                    return;
                }

                // 4. Plain Text
                if (string.IsNullOrWhiteSpace(unicodeText))
                {
                    try
                    {
                        if (Clipboard.ContainsText()) unicodeText = Clipboard.GetText();
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(unicodeText))
                {
                    item.Type = ClipType.Text;
                    item.Title = SanitizeTitle(unicodeText);
                    item.Description = $"{unicodeText.Length} 字符";
                    item.Data = CompressString(unicodeText);
                    item.DataSize = item.Data.Length;
                    EmitCaptured(item);
                    return;
                }
            }
            catch { }

            Thread.Sleep(35);
        }
    }

    /// <summary>
    /// Resolves the fullest webpage URL for copied HTML using clipboard URL metadata and shallow browser UIA fallback.
    /// </summary>
    public static string ResolveBestHtmlUrl(
        string? unicodeText,
        string html,
        string? mozUrlPriv,
        string? mozUrl,
        string? urlW,
        IntPtr fgHwnd,
        string processName)
    {
        if (!string.IsNullOrWhiteSpace(unicodeText) && ClipItem.IsWebUrl(unicodeText))
        {
            return unicodeText.Trim();
        }

        string? sourceUrl = ClipItem.ExtractSourceUrlFromHtml(html);

        mozUrlPriv = ClipItem.IsWebUrl(mozUrlPriv) ? mozUrlPriv!.Trim() : null;
        sourceUrl = ClipItem.IsWebUrl(sourceUrl) ? sourceUrl!.Trim() : null;
        urlW = ClipItem.IsWebUrl(urlW) ? urlW!.Trim() : null;
        mozUrl = ClipItem.IsWebUrl(mozUrl) ? mozUrl!.Trim() : null;

        if (!string.IsNullOrWhiteSpace(mozUrlPriv) && !BrowserUrlService.IsOriginOnlyUrl(mozUrlPriv))
        {
            return mozUrlPriv;
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl) && !BrowserUrlService.IsOriginOnlyUrl(sourceUrl))
        {
            return sourceUrl;
        }

        if (BrowserUrlService.IsBrowserProcess(processName) && fgHwnd != IntPtr.Zero)
        {
            string? hint = sourceUrl ?? mozUrlPriv ?? urlW ?? mozUrl;
            string? uiaUrl = BrowserUrlService.TryGetActiveTabUrlWithTimeout(fgHwnd, processName, hint, timeoutMs: 250);
            if (!string.IsNullOrWhiteSpace(uiaUrl) && (!BrowserUrlService.IsOriginOnlyUrl(uiaUrl) || string.IsNullOrWhiteSpace(hint)))
            {
                return uiaUrl;
            }
        }

        if (!string.IsNullOrWhiteSpace(mozUrlPriv)) return mozUrlPriv;
        if (!string.IsNullOrWhiteSpace(sourceUrl)) return sourceUrl;
        if (!string.IsNullOrWhiteSpace(urlW)) return urlW;
        if (!string.IsNullOrWhiteSpace(mozUrl)) return mozUrl;

        if (ClipItem.IsSingleLinkHtml(html, out string? linkUrl) && !string.IsNullOrWhiteSpace(linkUrl))
        {
            return linkUrl.Trim();
        }

        return string.Empty;
    }

    private static string? FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0].Trim() : null;
    }

    private static string? TryGetStringFormat(IDataObject? dataObj, string format)
    {
        if (dataObj == null) return null;
        try
        {
            if (dataObj.GetDataPresent(format))
            {
                return dataObj.GetData(format) as string;
            }
        }
        catch { }
        return null;
    }

    private static bool TryCaptureImage(IDataObject? dataObj, ClipItem item)
    {
        // Raw PNG stream first (preserves transparency cleanly)
        if (dataObj != null)
        {
            try
            {
                if (dataObj.GetDataPresent("PNG") && dataObj.GetData("PNG") is MemoryStream msPng && msPng.Length > 0)
                {
                    byte[] pngBytes = msPng.ToArray();
                    if (pngBytes.Length > 8 && pngBytes[0] == 0x89 && pngBytes[1] == 0x50)
                    {
                        using var testMs = new MemoryStream(pngBytes);
                        var dec = BitmapDecoder.Create(testMs, BitmapCreateOptions.None, BitmapCacheOption.None);
                        if (dec.Frames.Count > 0)
                        {
                            var frame = dec.Frames[0];
                            item.Type = ClipType.Image;
                            item.Title = $"图像 [{frame.PixelWidth}x{frame.PixelHeight}]";
                            item.Description = $"{frame.PixelWidth} x {frame.PixelHeight} 像素, PNG";
                            item.Data = pngBytes;
                            item.DataSize = pngBytes.Length;
                            return true;
                        }
                    }
                }
            }
            catch { }
        }

        try
        {
            if (Forms.Clipboard.ContainsImage())
            {
                using var gdiImg = Forms.Clipboard.GetImage();
                if (gdiImg != null)
                {
                    using var ms = new MemoryStream();
                    gdiImg.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] pngBytes = ms.ToArray();
                    if (pngBytes.Length > 0)
                    {
                        item.Type = ClipType.Image;
                        item.Title = $"图像 [{gdiImg.Width}x{gdiImg.Height}]";
                        item.Description = $"{gdiImg.Width} x {gdiImg.Height} 像素, PNG";
                        item.Data = pngBytes;
                        item.DataSize = pngBytes.Length;
                        return true;
                    }
                }
            }
        }
        catch { }

        try
        {
            if (Clipboard.ContainsImage() || (dataObj != null && (dataObj.GetDataPresent(DataFormats.Bitmap) || dataObj.GetDataPresent(DataFormats.Dib))))
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap != null)
                {
                    item.Type = ClipType.Image;
                    item.Title = $"图像 [{bitmap.PixelWidth}x{bitmap.PixelHeight}]";
                    item.Description = $"{bitmap.PixelWidth} x {bitmap.PixelHeight} 像素, WPF";

                    using var ms = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    BitmapSource safeBitmap = bitmap;
                    if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Bgr32 && bitmap.Format != PixelFormats.Pbgra32)
                    {
                        safeBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                    }
                    encoder.Frames.Add(BitmapFrame.Create(safeBitmap));
                    encoder.Save(ms);
                    byte[] pngBytes = ms.ToArray();
                    if (pngBytes.Length > 0)
                    {
                        item.Data = pngBytes;
                        item.DataSize = pngBytes.Length;
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static bool TryCaptureFiles(IDataObject? dataObj, ClipItem item)
    {
        var fileList = new List<string>();
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var sc = Clipboard.GetFileDropList();
                if (sc != null)
                {
                    foreach (string? f in sc)
                    {
                        if (!string.IsNullOrWhiteSpace(f)) fileList.Add(f);
                    }
                }
            }
            else if (dataObj != null && dataObj.GetDataPresent(DataFormats.FileDrop))
            {
                if (dataObj.GetData(DataFormats.FileDrop) is string[] files)
                {
                    fileList.AddRange(files);
                }
            }

            if (fileList.Count == 0 && Forms.Clipboard.ContainsFileDropList())
            {
                var fsc = Forms.Clipboard.GetFileDropList();
                if (fsc != null)
                {
                    foreach (string? f in fsc)
                    {
                        if (!string.IsNullOrWhiteSpace(f)) fileList.Add(f);
                    }
                }
            }
        }
        catch { }

        if (fileList.Count == 0) return false;

        item.Type = ClipType.Files;
        item.Title = fileList.Count == 1 ? Path.GetFileName(fileList[0]) : $"{fileList.Count} 个文件: {Path.GetFileName(fileList[0])}...";
        if (string.IsNullOrWhiteSpace(item.Title)) item.Title = $"{fileList.Count} 个文件";
        item.Description = string.Join("\r\n", fileList);

        item.Data = CompressString(item.Description);
        item.DataSize = item.Data.Length;
        return true;
    }

    private void EmitCaptured(ClipItem item)
    {
        string fingerprint = $"{item.Type}:{item.Title}:{item.DataSize}:{item.Description}";
        if (fingerprint == _lastCapturedFingerprint && (DateTime.UtcNow - _lastCapturedTime).TotalMilliseconds < 800)
        {
            return;
        }

        _lastCapturedFingerprint = fingerprint;
        _lastCapturedTime = DateTime.UtcNow;
        ClipCaptured?.Invoke(item);
    }

    public async Task<bool> WriteClipToClipboardWithRetryAsync(ClipItem item, bool asPlainText = false)
    {
        _isInternalCopy = true;
        _lastInternalCopyTime = DateTime.UtcNow;

        bool writeOk = false;
        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                writeOk = WriteClipToClipboard(item, asPlainText);
                if (writeOk) break;
            }
            catch { }
            await Task.Delay(20);
        }

        if (!writeOk)
        {
            _isInternalCopy = false;
            return false;
        }

        _lastInternalCopyTime = DateTime.UtcNow;
        return true;
    }

    private static async Task WaitForTriggerKeysReleasedAsync(int maxWaitMs = 350)
    {
        int waited = 0;
        while (waited < maxWaitMs)
        {
            bool anyDown = Win32.IsKeyPhysicallyDown(Win32.VK_LSHIFT) ||
                           Win32.IsKeyPhysicallyDown(Win32.VK_RSHIFT) ||
                           Win32.IsKeyPhysicallyDown(Win32.VK_SHIFT) ||
                           Win32.IsKeyPhysicallyDown(Win32.VK_LMENU) ||
                           Win32.IsKeyPhysicallyDown(Win32.VK_RMENU) ||
                           Win32.IsKeyPhysicallyDown(Win32.VK_MENU) ||
                           Win32.IsKeyPhysicallyDown(Win32.VK_RETURN);
            if (!anyDown) break;
            await Task.Delay(15);
            waited += 15;
        }
    }

    public async Task CompletePasteToWindowAsync(IntPtr targetHwnd)
    {
        await WaitForTriggerKeysReleasedAsync(350);

        if (targetHwnd == IntPtr.Zero || Win32.IsShellOrTrayWindow(targetHwnd))
        {
            targetHwnd = Win32.FindFallbackForegroundWindow(_hwnd);
        }

        if (targetHwnd != IntPtr.Zero && !Win32.IsShellOrTrayWindow(targetHwnd) &&
            Win32.GetForegroundWindow() != targetHwnd)
        {
            Win32.ForceForegroundWindow(targetHwnd);
        }

        await Task.Delay(60);
        Win32.SimulatePaste();
    }

    public async Task PasteClipAsync(ClipItem item, IntPtr targetHwnd, bool asPlainText = false)
    {
        if (!await WriteClipToClipboardWithRetryAsync(item, asPlainText))
        {
            return;
        }

        if (targetHwnd == IntPtr.Zero || Win32.IsShellOrTrayWindow(targetHwnd))
        {
            targetHwnd = Win32.FindFallbackForegroundWindow(_hwnd);
        }

        if (targetHwnd != IntPtr.Zero && !Win32.IsShellOrTrayWindow(targetHwnd))
        {
            Win32.ForceForegroundWindow(targetHwnd);
        }

        await CompletePasteToWindowAsync(targetHwnd);
    }

    public void CopyToClipboard(ClipItem item, bool asPlainText = false)
    {
        _isInternalCopy = true;
        _lastInternalCopyTime = DateTime.UtcNow;
        try
        {
            if (WriteClipToClipboard(item, asPlainText))
            {
                _lastInternalCopyTime = DateTime.UtcNow;
            }
            else
            {
                _isInternalCopy = false;
            }
        }
        catch
        {
            _isInternalCopy = false;
        }
    }

    public void CopyPlainText(string text)
    {
        _isInternalCopy = true;
        _lastInternalCopyTime = DateTime.UtcNow;
        try
        {
            if (!Win32.SetClipboardPlainText(text ?? string.Empty))
            {
                Clipboard.Clear();
                Clipboard.SetText(text ?? string.Empty);
            }
            _lastInternalCopyTime = DateTime.UtcNow;
        }
        catch
        {
            _isInternalCopy = false;
        }
    }

    private bool WriteClipToClipboard(ClipItem item, bool asPlainText)
    {
        if (item.Type == ClipType.Image)
        {
            var fullBmp = item.GetFullResolutionBitmap() ?? item.BitmapImage;
            if (fullBmp == null) return false;
            Clipboard.SetImage(fullBmp);
            return true;
        }

        if (item.Type == ClipType.Files && !asPlainText && !string.IsNullOrWhiteSpace(item.Description))
        {
            var files = new StringCollection();
            var lines = item.Description.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                files.AddRange(lines);
                Clipboard.SetFileDropList(files);
                return true;
            }
        }

        if (asPlainText || item.Type == ClipType.Text || item.Type == ClipType.Files)
        {
            string plain = item.PlainTextContent;
            if (Win32.SetClipboardPlainText(plain)) return true;

            Clipboard.Clear();
            Clipboard.SetText(plain);
            return true;
        }

        if (item.Type == ClipType.RichText)
        {
            string? rtf = item.GetRtfContent();
            if (string.IsNullOrWhiteSpace(rtf))
            {
                return WritePlainFallback(item.PlainTextContent);
            }

            string plain = item.PlainTextContent;
            string? html = item.GetHtmlContent();
            string? cfHtml = !string.IsNullOrWhiteSpace(html)
                ? RichContentService.NormalizeCfHtml(html, item.DisplayUrl)
                : RichContentService.ConvertRtfToCfHtml(rtf);

            byte[] rtfBytes = EncodeRtfBytes(rtf);
            var formats = new List<(uint FormatId, byte[] Data)>
            {
                (_cfRtf, rtfBytes),
                (_cfRtfNoObj, rtfBytes)
            };

            if (!string.IsNullOrWhiteSpace(cfHtml))
            {
                formats.Add((_cfHtml, Encoding.UTF8.GetBytes(cfHtml + "\0")));
            }
            formats.Add((Win32.CF_UNICODETEXT, Encoding.Unicode.GetBytes(plain + "\0")));

            if (Win32.SetClipboardFormats(formats)) return true;

            var dataObj = new DataObject();
            dataObj.SetData(DataFormats.Rtf, rtf);
            if (!string.IsNullOrWhiteSpace(cfHtml)) dataObj.SetData(DataFormats.Html, cfHtml);
            dataObj.SetData(DataFormats.UnicodeText, plain);
            dataObj.SetData(DataFormats.Text, plain);
            Clipboard.SetDataObject(dataObj, true);
            return true;
        }

        if (item.Type == ClipType.Html)
        {
            string? rawHtml = item.GetHtmlContent();
            if (string.IsNullOrWhiteSpace(rawHtml))
            {
                return WritePlainFallback(item.PlainTextContent);
            }

            string plain = item.PlainTextContent;
            string cfHtml = RichContentService.NormalizeCfHtml(rawHtml, item.DisplayUrl);

            var formats = new List<(uint FormatId, byte[] Data)>
            {
                (_cfHtml, Encoding.UTF8.GetBytes(cfHtml + "\0"))
            };

            string? companionRtf = item.GetRtfContent();
            if (!string.IsNullOrWhiteSpace(companionRtf))
            {
                formats.Add((_cfRtf, EncodeRtfBytes(companionRtf)));
            }
            formats.Add((Win32.CF_UNICODETEXT, Encoding.Unicode.GetBytes(plain + "\0")));

            if (Win32.SetClipboardFormats(formats)) return true;

            var dataObj = new DataObject();
            dataObj.SetData(DataFormats.Html, cfHtml);
            if (!string.IsNullOrWhiteSpace(companionRtf)) dataObj.SetData(DataFormats.Rtf, companionRtf);
            dataObj.SetData(DataFormats.UnicodeText, plain);
            dataObj.SetData(DataFormats.Text, plain);
            Clipboard.SetDataObject(dataObj, true);
            return true;
        }

        return WritePlainFallback(item.PlainTextContent);
    }

    private static bool WritePlainFallback(string text)
    {
        if (Win32.SetClipboardPlainText(text)) return true;
        Clipboard.Clear();
        Clipboard.SetText(text);
        return true;
    }

    /// <summary>
    /// Encodes RTF strings as strict 7-bit ASCII bytes (converting any direct non-ASCII chars to \uN? escapes) + NUL terminator.
    /// </summary>
    private static byte[] EncodeRtfBytes(string rtf)
    {
        string clean = rtf.TrimEnd('\0');
        var sb = new StringBuilder(clean.Length + 16);
        foreach (char ch in clean)
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
        sb.Append('\0');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string SanitizeTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        string firstLine = lines.Length > 0 ? lines[0].Trim() : text.Trim();
        return firstLine.Length > 80 ? firstLine[..80] + "..." : firstLine;
    }

    private static byte[] CompressString(string s)
    {
        return CompressBytes(Encoding.UTF8.GetBytes(s));
    }

    private static byte[] CompressBytes(byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
        {
            gz.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }
}
