using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClipFlux.Native;
using Microsoft.Win32;

namespace ClipFlux.Services;

public sealed record CloudDriveOption(string Id, string DisplayName, string RootPath);

public sealed record BackupExecutionResult(
    bool Success,
    List<string> CompletedTargets,
    List<string> Errors,
    DateTime Timestamp);

/// <summary>
/// Orchestrates manual, scheduled, local, cloud sync folder, and WebDAV database backups.
/// </summary>
public static class BackupService
{
    private const string BackupFilePrefix = "ClipFlux_Backup_";
    private const string BackupFilePattern = "ClipFlux_Backup_*.db";
    private static readonly Regex BackupNameRegex = new(@"^ClipFlux_Backup_\d{8}_\d{6}\.db$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly SemaphoreSlim BackupLock = new(1, 1);
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private const string PropfindXmlBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>";

    public static bool IsScheduledBackupDue(AppSettings settings)
    {
        if (!settings.EnableScheduledBackup) return false;
        if (string.IsNullOrWhiteSpace(settings.LastBackupTime)) return true;

        if (!DateTime.TryParse(settings.LastBackupTime, out DateTime last))
        {
            return true;
        }

        int interval = Math.Clamp(settings.BackupIntervalHours, 1, 720);
        return (DateTime.Now - last).TotalHours >= interval;
    }

    public static List<CloudDriveOption> DetectInstalledCloudDrives()
    {
        var results = new List<CloudDriveOption>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string id, string label, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(trimmed)) return;
                if (seenPaths.Add(trimmed))
                {
                    results.Add(new CloudDriveOption(id, $"{label} ({trimmed})", trimmed));
                }
            }
            catch { }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // OneDrive
        TryAdd("OneDrive", "OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        TryAdd("OneDrive", "OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        TryAdd("OneDrive", "OneDrive for Business", Environment.GetEnvironmentVariable("OneDriveCommercial"));
        try
        {
            using var odKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive", false);
            TryAdd("OneDrive", "OneDrive", odKey?.GetValue("UserFolder") as string);
        }
        catch { }

        // Google Drive
        TryAdd("GoogleDrive", "Google Drive", @"G:\My Drive");
        TryAdd("GoogleDrive", "Google Drive", @"G:\我的云端硬盘");
        TryAdd("GoogleDrive", "Google Drive", Path.Combine(userProfile, "Google Drive"));

        // Dropbox
        foreach (string infoJson in new[]
        {
            Path.Combine(localAppData, "Dropbox", "info.json"),
            Path.Combine(appData, "Dropbox", "info.json")
        })
        {
            if (File.Exists(infoJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(infoJson));
                    if (doc.RootElement.TryGetProperty("personal", out var personal) &&
                        personal.TryGetProperty("path", out var pPath))
                    {
                        TryAdd("Dropbox", "Dropbox", pPath.GetString());
                    }
                    if (doc.RootElement.TryGetProperty("business", out var biz) &&
                        biz.TryGetProperty("path", out var bPath))
                    {
                        TryAdd("Dropbox", "Dropbox Business", bPath.GetString());
                    }
                }
                catch { }
            }
        }
        TryAdd("Dropbox", "Dropbox", Path.Combine(userProfile, "Dropbox"));

        // iCloud Drive
        TryAdd("iCloud", "iCloud Drive", Path.Combine(userProfile, "iCloudDrive"));

        // Nutstore (坚果云)
        TryAdd("Nutstore", "坚果云 / Nutstore", Path.Combine(userProfile, "我的坚果云"));
        TryAdd("Nutstore", "坚果云 / Nutstore", Path.Combine(userProfile, "Nutstore"));

        // Baidu Syncdisk (百度网盘同步空间)
        TryAdd("BaiduSync", "百度网盘同步盘", Path.Combine(userProfile, "BaiduSyncdisk"));

        return results;
    }

    public static string NormalizeWebDavFolderUrl(string url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        if (!trimmed.EndsWith("/", StringComparison.Ordinal))
        {
            trimmed += "/";
        }
        return trimmed;
    }

    public static async Task<(bool Ok, string Message)> TestWebDavConnectionAsync(string url, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, LocalizationService.Get("Str_Bak_WebDavNeedUrl", "请先填写 WebDAV 目录地址"));
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, LocalizationService.Get("Str_Bak_WebDavBadUrl", "WebDAV 地址格式无效 (需以 http:// 或 https:// 开头)"));
        }

        string folderUrl = NormalizeWebDavFolderUrl(uri.AbsoluteUri);
        try
        {
            using var propReq = CreateWebDavRequest(new HttpMethod("PROPFIND"), folderUrl, username, password);
            propReq.Headers.Add("Depth", "0");
            propReq.Content = new StringContent(PropfindXmlBody, Encoding.UTF8, "application/xml");

            using var resp = await SharedHttpClient.SendAsync(propReq);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return (false, LocalizationService.Get("Str_Bak_WebDavAuthFail", "认证失败 (用户名或应用密码错误)"));
            }

            if ((int)resp.StatusCode is >= 200 and < 300 || (int)resp.StatusCode == 207)
            {
                return (true, LocalizationService.Get("Str_Bak_WebDavOk", "连接成功"));
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                using var mkcolReq = CreateWebDavRequest(new HttpMethod("MKCOL"), folderUrl, username, password);
                using var mkResp = await SharedHttpClient.SendAsync(mkcolReq);
                if (mkResp.IsSuccessStatusCode)
                {
                    return (true, LocalizationService.Get("Str_Bak_WebDavOk", "连接成功"));
                }
            }

            return (false, LocalizationService.Format("Str_Bak_WebDavHttpErr", (int)resp.StatusCode));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<BackupExecutionResult> RunBackupAsync(
        DatabaseService dbService,
        AppSettings settings,
        bool includeLocal = true,
        string? customSaveAsPath = null)
    {
        await BackupLock.WaitAsync();
        DateTime now = DateTime.Now;
        var completed = new List<string>();
        var errors = new List<string>();
        string fileName = $"{BackupFilePrefix}{now:yyyyMMdd_HHmmss}.db";
        string stagingPath = Path.Combine(Path.GetTempPath(), $"ClipFlux_Staging_{Guid.NewGuid():N}.db");

        try
        {
            await dbService.BackupDatabaseToFileAsync(stagingPath);

            if (!string.IsNullOrWhiteSpace(customSaveAsPath))
            {
                CopyFileAtomically(stagingPath, Path.GetFullPath(customSaveAsPath));
                completed.Add(Path.GetFileName(customSaveAsPath));
                return new BackupExecutionResult(true, completed, errors, now);
            }

            int retention = Math.Clamp(settings.MaxBackupRetention, 1, 50);

            if (includeLocal && !string.IsNullOrWhiteSpace(settings.LocalBackupDir))
            {
                try
                {
                    string targetPath = Path.Combine(settings.LocalBackupDir, fileName);
                    await Task.Run(() =>
                    {
                        CopyFileAtomically(stagingPath, targetPath);
                        PruneLocalBackups(settings.LocalBackupDir, retention);
                    });
                    completed.Add(LocalizationService.Get("Str_Bak_LocalTarget", "本地目录"));
                }
                catch (Exception ex)
                {
                    errors.Add($"{LocalizationService.Get("Str_Bak_LocalTarget", "本地目录")}: {ex.Message}");
                }
            }

            if (settings.EnableCloudDriveBackup && !string.IsNullOrWhiteSpace(settings.CloudDriveFolder))
            {
                try
                {
                    string cloudBackupDir = Path.Combine(settings.CloudDriveFolder.Trim(), "ClipFluxBackups");
                    string targetPath = Path.Combine(cloudBackupDir, fileName);
                    await Task.Run(() =>
                    {
                        CopyFileAtomically(stagingPath, targetPath);
                        PruneLocalBackups(cloudBackupDir, retention);
                    });
                    completed.Add(LocalizationService.Get("Str_Bak_CloudTargetName", "网盘目录"));
                }
                catch (Exception ex)
                {
                    errors.Add($"{LocalizationService.Get("Str_Bak_CloudTargetName", "网盘目录")}: {ex.Message}");
                }
            }

            if (settings.EnableWebDavBackup && !string.IsNullOrWhiteSpace(settings.WebDavUrl))
            {
                try
                {
                    string password = Win32.UnprotectString(settings.WebDavPasswordProtected);
                    await UploadToWebDavAsync(stagingPath, fileName, settings.WebDavUrl, settings.WebDavUsername, password, retention);
                    completed.Add(LocalizationService.Get("Str_Bak_WebDavTargetName", "WebDAV"));
                }
                catch (Exception ex)
                {
                    errors.Add($"WebDAV: {ex.Message}");
                }
            }

            return new BackupExecutionResult(errors.Count == 0 && completed.Count > 0, completed, errors, now);
        }
        finally
        {
            try
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
                if (File.Exists(stagingPath + ".tmp")) File.Delete(stagingPath + ".tmp");
            }
            catch { }
            BackupLock.Release();
        }
    }

    private static void CopyFileAtomically(string srcPath, string destPath)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempDest = destPath + ".tmp";
        if (File.Exists(tempDest)) File.Delete(tempDest);
        File.Copy(srcPath, tempDest, overwrite: true);
        File.Move(tempDest, destPath, overwrite: true);
    }

    private static void PruneLocalBackups(string directory, int maxRetention)
    {
        if (!Directory.Exists(directory)) return;

        var files = new DirectoryInfo(directory)
            .GetFiles(BackupFilePattern)
            .Where(f => BackupNameRegex.IsMatch(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = maxRetention; i < files.Count; i++)
        {
            try
            {
                files[i].Delete();
            }
            catch { }
        }
    }

    private static async Task UploadToWebDavAsync(
        string localSnapshotPath,
        string remoteFileName,
        string webDavUrl,
        string username,
        string password,
        int maxRetention)
    {
        if (!Uri.TryCreate(webDavUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(LocalizationService.Get("Str_Bak_WebDavBadUrl", "WebDAV 地址无效"));
        }

        string folderUrl = NormalizeWebDavFolderUrl(baseUri.AbsoluteUri);

        // Best-effort MKCOL in case target subfolder does not yet exist
        try
        {
            using var mkReq = CreateWebDavRequest(new HttpMethod("MKCOL"), folderUrl, username, password);
            using var _ = await SharedHttpClient.SendAsync(mkReq);
        }
        catch { }

        string uploadUrl = folderUrl + Uri.EscapeDataString(remoteFileName);
        await using (var fs = new FileStream(localSnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var putReq = CreateWebDavRequest(HttpMethod.Put, uploadUrl, username, password))
        {
            putReq.Content = new StreamContent(fs);
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var putResp = await SharedHttpClient.SendAsync(putReq);
            if (!putResp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)putResp.StatusCode} {putResp.ReasonPhrase}");
            }
        }

        await PruneWebDavBackupsAsync(folderUrl, username, password, maxRetention);
    }

    private static async Task PruneWebDavBackupsAsync(string folderUrl, string username, string password, int maxRetention)
    {
        try
        {
            using var listReq = CreateWebDavRequest(new HttpMethod("PROPFIND"), folderUrl, username, password);
            listReq.Headers.Add("Depth", "1");
            listReq.Content = new StringContent(PropfindXmlBody, Encoding.UTF8, "application/xml");

            using var listResp = await SharedHttpClient.SendAsync(listReq);
            if (!listResp.IsSuccessStatusCode && (int)listResp.StatusCode != 207) return;

            string xml = await listResp.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            XNamespace dav = "DAV:";
            var remoteNames = new List<string>();

            foreach (var hrefEl in doc.Descendants(dav + "href"))
            {
                string rawHref = hrefEl.Value.Trim().TrimEnd('/');
                int lastSlash = rawHref.LastIndexOf('/');
                string filePart = Uri.UnescapeDataString(lastSlash >= 0 ? rawHref[(lastSlash + 1)..] : rawHref);
                if (BackupNameRegex.IsMatch(filePart) && !remoteNames.Contains(filePart, StringComparer.OrdinalIgnoreCase))
                {
                    remoteNames.Add(filePart);
                }
            }

            remoteNames.Sort(StringComparer.OrdinalIgnoreCase);
            remoteNames.Reverse();

            for (int i = maxRetention; i < remoteNames.Count; i++)
            {
                string deleteUrl = folderUrl + Uri.EscapeDataString(remoteNames[i]);
                using var delReq = CreateWebDavRequest(HttpMethod.Delete, deleteUrl, username, password);
                using var _ = await SharedHttpClient.SendAsync(delReq);
            }
        }
        catch { }
    }

    private static HttpRequestMessage CreateWebDavRequest(HttpMethod method, string url, string username, string password)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
        {
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username ?? string.Empty}:{password ?? string.Empty}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return req;
    }
}
