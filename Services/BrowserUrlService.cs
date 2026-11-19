using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ClipFlux.Services;

/// <summary>
/// Extracts full active tab URLs from browser windows via shallow UI Automation inspection
/// when browsers strip SourceURL down to origin-only in CF_HTML.
/// </summary>
public static class BrowserUrlService
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "chrome", "msedge", "brave", "opera", "vivaldi", "arc",
        "zen", "waterfox", "floorp", "iexplore", "360chrome", "qqbrowser"
    };

    public static bool IsBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return BrowserProcesses.Contains(clean);
    }

    public static bool IsOriginOnlyUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/");
    }

    public static string? NormalizeAndValidateAddressBarUrl(string? rawValue, string? originHintUrl)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;
        string raw = rawValue.Trim();

        // Reject arbitrary text / search queries containing whitespace or control line breaks
        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c)) return null;
        }

        string candidate = raw;
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            bool looksLikeHost = raw.Contains('.') ||
                                 raw.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                                 raw.Contains(':');
            if (!looksLikeHost) return null;

            string scheme = "https://";
            if (!string.IsNullOrWhiteSpace(originHintUrl) &&
                Uri.TryCreate(originHintUrl.Trim(), UriKind.Absolute, out var hint) &&
                (hint.Scheme == Uri.UriSchemeHttp || hint.Scheme == Uri.UriSchemeHttps))
            {
                scheme = hint.Scheme + "://";
            }

            candidate = scheme + raw;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrWhiteSpace(uri.Host)) return null;

        if (!string.IsNullOrWhiteSpace(originHintUrl) &&
            Uri.TryCreate(originHintUrl.Trim(), UriKind.Absolute, out var originUri) &&
            !string.IsNullOrWhiteSpace(originUri.Host))
        {
            if (!HostsMatch(uri.Host, originUri.Host)) return null;
        }

        return uri.AbsoluteUri;
    }

    private static bool HostsMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        string sa = a.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? a[4..] : a;
        string sb = b.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? b[4..] : b;
        return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Queries the foreground browser window on an MTA threadpool thread with a hard timeout
    /// so UI Automation calls never stall clipboard capture.
    /// </summary>
    public static string? TryGetActiveTabUrlWithTimeout(IntPtr hwnd, string processName, string? originHintUrl, int timeoutMs = 250)
    {
        if (hwnd == IntPtr.Zero || !IsBrowserProcess(processName)) return null;

        try
        {
            var task = Task.Run(() => TryGetActiveTabUrlCore(hwnd, originHintUrl));
            if (task.Wait(timeoutMs))
            {
                return task.Result;
            }
        }
        catch { }

        return null;
    }

    private static string? TryGetActiveTabUrlCore(IntPtr hwnd, string? originHintUrl)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((root, 0));

            string? fallbackUrl = null;
            int visited = 0;
            const int maxDepth = 7;
            const int maxNodes = 220;

            while (queue.Count > 0 && visited < maxNodes)
            {
                var (current, depth) = queue.Dequeue();
                visited++;

                ControlType? ctrlType = null;
                try
                {
                    ctrlType = current.Current.ControlType;
                }
                catch
                {
                    continue;
                }

                if (ctrlType == ControlType.Document)
                {
                    string? docUrl = TryReadElementUrl(current, originHintUrl);
                    if (!string.IsNullOrEmpty(docUrl))
                    {
                        if (!IsOriginOnlyUrl(docUrl)) return docUrl;
                        fallbackUrl ??= docUrl;
                    }
                    // Never descend into web page DOM nodes
                    continue;
                }

                if (ctrlType == ControlType.Edit && IsAddressBarCandidate(current))
                {
                    string? barUrl = TryReadElementUrl(current, originHintUrl);
                    if (!string.IsNullOrEmpty(barUrl))
                    {
                        if (!IsOriginOnlyUrl(barUrl)) return barUrl;
                        fallbackUrl ??= barUrl;
                    }
                }

                if (depth >= maxDepth) continue;

                try
                {
                    AutomationElement? child = walker.GetFirstChild(current);
                    while (child != null && visited + queue.Count < maxNodes)
                    {
                        queue.Enqueue((child, depth + 1));
                        child = walker.GetNextSibling(child);
                    }
                }
                catch { }
            }

            return fallbackUrl;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAddressBarCandidate(AutomationElement element)
    {
        try
        {
            string autoId = element.Current.AutomationId ?? string.Empty;
            if (autoId.Equals("urlbar-input", StringComparison.OrdinalIgnoreCase) ||
                autoId.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                autoId.Contains("omnibox", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string accessKey = element.Current.AccessKey ?? string.Empty;
            if (accessKey.Contains('L') || accessKey.Contains('l'))
            {
                return true;
            }

            string name = element.Current.Name ?? string.Empty;
            return name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("地址") ||
                   name.Contains("URL", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("搜索") ||
                   name.Contains("Search", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadElementUrl(AutomationElement element, string? originHintUrl)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? patternObj) &&
                patternObj is ValuePattern vp)
            {
                string raw = vp.Current.Value;
                return NormalizeAndValidateAddressBarUrl(raw, originHintUrl);
            }
        }
        catch { }
        return null;
    }
}
