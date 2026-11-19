using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClipFlux.Models;
using Microsoft.Data.Sqlite;

namespace ClipFlux.Services;

public class DatabaseService
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public DatabaseService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string folder = Path.Combine(appData, "ClipFlux");
        Directory.CreateDirectory(folder);
        _dbPath = Path.Combine(folder, "history.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath => _dbPath;

    public async Task InitializeAsync()
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // High performance WAL mode
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        await pragmaCmd.ExecuteNonQueryAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                title TEXT NOT NULL DEFAULT '',
                type INTEGER NOT NULL DEFAULT 1,
                description TEXT NOT NULL DEFAULT '',
                data BLOB NOT NULL,
                datasize INTEGER NOT NULL DEFAULT 0,
                appname TEXT NOT NULL DEFAULT '',
                apptitle TEXT NOT NULL DEFAULT '',
                starred INTEGER NOT NULL DEFAULT 0,
                tags TEXT NOT NULL DEFAULT ',',
                url TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_log_created ON log(created DESC);
            CREATE INDEX IF NOT EXISTS idx_log_starred ON log(starred);
            CREATE INDEX IF NOT EXISTS idx_log_type ON log(type);
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> SaveClipAsync(ClipItem item)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Deduplication check: compare with the most recent clip
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT id, title, type, datasize FROM log ORDER BY id DESC LIMIT 1;";
        using var reader = await checkCmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            string lastTitle = reader.GetString(1);
            int lastType = reader.GetInt32(2);
            int lastSize = reader.GetInt32(3);

            if (lastType == (int)item.Type && lastTitle == item.Title && lastSize == item.DataSize)
            {
                // Already the latest item; update timestamp and return
                long existingId = reader.GetInt64(0);
                reader.Close();

                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = "UPDATE log SET created = datetime('now', 'localtime') WHERE id = @id;";
                updateCmd.Parameters.AddWithValue("@id", existingId);
                await updateCmd.ExecuteNonQueryAsync();
                item.Id = existingId;
                return existingId;
            }
        }
        reader.Close();

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO log (title, type, description, data, datasize, appname, apptitle, starred, tags, url, created)
            VALUES (@title, @type, @desc, @data, @size, @app, @apptitle, @starred, @tags, @url, datetime('now', 'localtime'));
            SELECT last_insert_rowid();
        ";
        insertCmd.Parameters.AddWithValue("@title", item.Title ?? string.Empty);
        insertCmd.Parameters.AddWithValue("@type", (int)item.Type);
        insertCmd.Parameters.AddWithValue("@desc", item.Description ?? string.Empty);
        insertCmd.Parameters.AddWithValue("@data", item.Data ?? Array.Empty<byte>());
        insertCmd.Parameters.AddWithValue("@size", item.DataSize);
        insertCmd.Parameters.AddWithValue("@app", item.AppName ?? string.Empty);
        insertCmd.Parameters.AddWithValue("@apptitle", item.AppTitle ?? string.Empty);
        insertCmd.Parameters.AddWithValue("@starred", item.IsStarred ? 1 : 0);
        insertCmd.Parameters.AddWithValue("@tags", item.Tags ?? ",");
        insertCmd.Parameters.AddWithValue("@url", item.Url ?? string.Empty);

        object? result = await insertCmd.ExecuteScalarAsync();
        long newId = result is long idVal ? idVal : 0;
        item.Id = newId;
        return newId;
    }

    public async Task<List<ClipItem>> GetClipsAsync(
        string? search = null,
        int? typeFilter = null,
        bool onlyStarred = false,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var list = new List<ClipItem>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();

        if (onlyStarred)
        {
            conditions.Add("starred = 1");
        }

        if (typeFilter.HasValue && typeFilter.Value >= 0)
        {
            conditions.Add($"type = {typeFilter.Value}");
        }

        bool hasSearch = !string.IsNullOrWhiteSpace(search);
        string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        int queryLimit = hasSearch ? Math.Max(limit * 3, 500) : limit;

        cmd.CommandText = $"SELECT id, created, title, type, description, (CASE WHEN datasize > 16384 THEN NULL ELSE data END) as data, datasize, appname, apptitle, starred, tags, url FROM log {whereClause} ORDER BY created DESC LIMIT {queryLimit} OFFSET {offset};";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataObj = reader["data"];
            byte[] dataBytes = dataObj is byte[] b ? b : Array.Empty<byte>();

            var item = new ClipItem
            {
                Id = reader.GetInt64(0),
                Created = reader.GetDateTime(1),
                Title = reader.GetString(2),
                Type = (ClipType)reader.GetInt32(3),
                Description = reader.GetString(4),
                Data = dataBytes,
                DataSize = reader.GetInt32(6),
                AppName = reader.GetString(7),
                AppTitle = reader.GetString(8),
                IsStarred = reader.GetInt32(9) == 1,
                Tags = reader.GetString(10),
                Url = reader.GetString(11)
            };

            if (hasSearch)
            {
                string q = search!;
                bool matched = ChineseService.MatchesChineseOrPinyin(item.Title, q) ||
                               ChineseService.MatchesChineseOrPinyin(item.Url, q) ||
                               ChineseService.MatchesChineseOrPinyin(item.AppName, q) ||
                               ChineseService.MatchesChineseOrPinyin(item.AppTitle, q) ||
                               ChineseService.MatchesChineseOrPinyin(item.Description, q) ||
                               (item.Type != ClipType.Image && item.Data.Length > 0 && ChineseService.MatchesChineseOrPinyin(item.PlainTextContent, q));

                if (matched)
                {
                    list.Add(item);
                    if (list.Count >= limit) break;
                }
            }
            else
            {
                list.Add(item);
            }
        }

        return list;
    }

    /// <summary>
    /// Loads lightweight metadata of recent clips across all categories for the system tray menu.
    /// </summary>
    public async Task<List<ClipItem>> GetRecentClipsForTrayAsync(int limit)
    {
        var list = new List<ClipItem>();
        int fetchCount = Math.Clamp(limit, 5, 60);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, created, title, type, description, datasize, appname, apptitle, starred, tags, url FROM log ORDER BY created DESC LIMIT {fetchCount};";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ClipItem
            {
                Id = reader.GetInt64(0),
                Created = reader.GetDateTime(1),
                Title = reader.GetString(2),
                Type = (ClipType)reader.GetInt32(3),
                Description = reader.GetString(4),
                DataSize = reader.GetInt32(5),
                AppName = reader.GetString(6),
                AppTitle = reader.GetString(7),
                IsStarred = reader.GetInt32(8) == 1,
                Tags = reader.GetString(9),
                Url = reader.GetString(10)
            });
        }

        return list;
    }

    /// <summary>
    /// Backfills missing SQL url columns for recent HTML records that contain SourceURL or link metadata in their payload.
    /// </summary>
    public async Task<int> BackfillRecentHtmlUrlsAsync(int maxRows = 500)
    {
        int updated = 0;
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var updates = new List<(long Id, string Url)>();
            using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.CommandText = "SELECT id, data, title, description FROM log WHERE type = 3 AND (url IS NULL OR url = '') ORDER BY id DESC LIMIT @limit;";
                selectCmd.Parameters.AddWithValue("@limit", maxRows);

                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(0);
                    byte[]? dataBytes = reader["data"] as byte[];
                    if (dataBytes == null || dataBytes.Length == 0) continue;

                    var probe = new ClipItem
                    {
                        Type = ClipType.Html,
                        Title = reader.GetString(2),
                        Description = reader.GetString(3),
                        Data = dataBytes
                    };
                    string resolved = probe.DisplayUrl;
                    if (ClipItem.IsWebUrl(resolved))
                    {
                        updates.Add((id, resolved));
                    }
                }
            }

            if (updates.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE log SET url = @url WHERE id = @id;";
            var pUrl = updateCmd.Parameters.Add("@url", SqliteType.Text);
            var pId = updateCmd.Parameters.Add("@id", SqliteType.Integer);

            foreach (var (id, url) in updates)
            {
                pUrl.Value = url;
                pId.Value = id;
                await updateCmd.ExecuteNonQueryAsync();
                updated++;
            }
            await tx.CommitAsync();
        }
        catch { }

        return updated;
    }

    /// <summary>
    /// Loads full data payload on demand for preview, pasting or exporting
    /// </summary>
    public async Task EnsureDataLoadedAsync(ClipItem item)
    {
        if (item.Data != null && item.Data.Length > 0) return;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM log WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", item.Id);

        var result = await cmd.ExecuteScalarAsync();
        if (result is byte[] bytes)
        {
            item.Data = bytes;
        }
    }

    public async Task ToggleStarAsync(long id, bool isStarred)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE log SET starred = @starred WHERE id = @id;";
        cmd.Parameters.AddWithValue("@starred", isStarred ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteClipAsync(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM log WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteClipsAsync(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM log WHERE id = @id;";
        var p = cmd.Parameters.Add("@id", SqliteType.Integer);

        foreach (var id in idList)
        {
            p.Value = id;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task ClearHistoryAsync(bool keepStarred = true)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = keepStarred ? "DELETE FROM log WHERE starred = 0;" : "DELETE FROM log;";
        await cmd.ExecuteNonQueryAsync();

        // Vacuum database to free physical space
        using var vacCmd = conn.CreateCommand();
        vacCmd.CommandText = "VACUUM;";
        await vacCmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetClipsCountAsync(string? search = null, int? typeFilter = null, bool onlyStarred = false)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (onlyStarred)
        {
            conditions.Add("starred = 1");
        }
        if (typeFilter.HasValue && typeFilter.Value >= 0)
        {
            conditions.Add($"type = {typeFilter.Value}");
        }

        string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"SELECT COUNT(*) FROM log {whereClause};";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<int> ClearCategoryHistoryAsync(int? typeFilter, bool keepStarred = true)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (keepStarred)
        {
            conditions.Add("starred = 0");
        }
        if (typeFilter.HasValue && typeFilter.Value >= 0)
        {
            conditions.Add($"type = {typeFilter.Value}");
        }

        string where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"DELETE FROM log {where};";
        int affected = await cmd.ExecuteNonQueryAsync();

        // Checkpoint WAL
        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await walCmd.ExecuteNonQueryAsync();

        return affected;
    }

    public async Task<(int totalCount, int starredCount, long dbSize)> GetStatsAsync()
    {
        int total = 0;
        int starred = 0;
        long size = 0;

        try
        {
            if (File.Exists(_dbPath))
            {
                size = new FileInfo(_dbPath).Length;
            }

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), SUM(CASE WHEN starred = 1 THEN 1 ELSE 0 END) FROM log;";
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                total = reader.GetInt32(0);
                starred = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            }
        }
        catch { }

        return (total, starred, size);
    }

    public async Task<int> GetUnstarredCountAsync()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM log WHERE starred = 0;";
            object? res = await cmd.ExecuteScalarAsync();
            return res != null && res != DBNull.Value ? Convert.ToInt32(res) : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Creates a consistent, WAL-safe snapshot of the active SQLite database to destinationFilePath.
    /// </summary>
    public async Task BackupDatabaseToFileAsync(string destinationFilePath)
    {
        if (string.IsNullOrWhiteSpace(destinationFilePath))
        {
            throw new ArgumentException("Destination backup path is empty.", nameof(destinationFilePath));
        }

        string fullDest = Path.GetFullPath(destinationFilePath);
        string? dir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = fullDest + ".tmp";
        await Task.Run(() =>
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            string destConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            using (var srcConn = new SqliteConnection(_connectionString))
            using (var destConn = new SqliteConnection(destConnStr))
            {
                srcConn.Open();
                destConn.Open();
                srcConn.BackupDatabase(destConn);
            }

            File.Move(tempPath, fullDest, overwrite: true);
        });
    }

    /// <summary>
    /// Imports clipboard history records from a compatible external SQLite database file.
    /// </summary>
    public async Task<int> ImportExternalDatabaseAsync(string sourceDbPath, Action<int, int>? progressCallback = null)
    {
        if (!File.Exists(sourceDbPath)) return 0;

        if (string.Equals(Path.GetFullPath(sourceDbPath), Path.GetFullPath(_dbPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能将当前正在使用的数据库导入到自身。");
        }

        string srcConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        int importedCount = 0;
        int totalCount = 0;

        using (var srcConn = new SqliteConnection(srcConnStr))
        {
            await srcConn.OpenAsync();

            using var tblCmd = srcConn.CreateCommand();
            tblCmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='log' LIMIT 1;";
            if (await tblCmd.ExecuteScalarAsync() == null)
            {
                throw new InvalidOperationException("所选数据库不包含兼容的历史记录表 (log)。");
            }

            using var countCmd = srcConn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM log;";
            object? val = await countCmd.ExecuteScalarAsync();
            totalCount = Convert.ToInt32(val);
        }

        using var readConn = new SqliteConnection(srcConnStr);
        await readConn.OpenAsync();
        using var selectCmd = readConn.CreateCommand();
        selectCmd.CommandText = "SELECT created, title, type, description, data, datasize, appname, apptitle, starred, tags, url FROM log ORDER BY id ASC;";

        using var destConn = new SqliteConnection(_connectionString);
        await destConn.OpenAsync();

        using var reader = await selectCmd.ExecuteReaderAsync();
        using var tx = destConn.BeginTransaction();

        using var insertCmd = destConn.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = @"
            INSERT INTO log (created, title, type, description, data, datasize, appname, apptitle, starred, tags, url)
            VALUES (@created, @title, @type, @desc, @data, @size, @app, @apptitle, @starred, @tags, @url);
        ";

        var pCreated = insertCmd.Parameters.Add("@created", SqliteType.Text);
        var pTitle = insertCmd.Parameters.Add("@title", SqliteType.Text);
        var pType = insertCmd.Parameters.Add("@type", SqliteType.Integer);
        var pDesc = insertCmd.Parameters.Add("@desc", SqliteType.Text);
        var pData = insertCmd.Parameters.Add("@data", SqliteType.Blob);
        var pSize = insertCmd.Parameters.Add("@size", SqliteType.Integer);
        var pApp = insertCmd.Parameters.Add("@app", SqliteType.Text);
        var pAppTitle = insertCmd.Parameters.Add("@apptitle", SqliteType.Text);
        var pStarred = insertCmd.Parameters.Add("@starred", SqliteType.Integer);
        var pTags = insertCmd.Parameters.Add("@tags", SqliteType.Text);
        var pUrl = insertCmd.Parameters.Add("@url", SqliteType.Text);

        int batchSize = 0;

        while (await reader.ReadAsync())
        {
            pCreated.Value = reader["created"] == DBNull.Value ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : reader["created"];

            int clipType = reader["type"] == DBNull.Value ? (int)ClipType.Text : Convert.ToInt32(reader["type"]);
            string origTitle = reader["title"] == DBNull.Value ? string.Empty : reader["title"].ToString() ?? string.Empty;
            byte[]? rawData = reader["data"] as byte[];
            if ((string.IsNullOrWhiteSpace(origTitle) || origTitle.Contains('\ufffd')) && rawData != null && rawData.Length > 0)
            {
                var tempItem = new ClipItem { Data = rawData, Type = (ClipType)clipType };
                string candidate = tempItem.PlainTextContent;
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    origTitle = ChineseService.GenerateCleanChineseTitle(candidate);
                }
            }

            pTitle.Value = origTitle;
            pType.Value = clipType;
            pDesc.Value = reader["description"] == DBNull.Value ? string.Empty : reader["description"];
            pData.Value = rawData ?? Array.Empty<byte>();
            pSize.Value = reader["datasize"] == DBNull.Value ? (rawData?.Length ?? 0) : reader["datasize"];
            pApp.Value = reader["appname"] == DBNull.Value ? string.Empty : reader["appname"];
            pAppTitle.Value = reader["apptitle"] == DBNull.Value ? string.Empty : reader["apptitle"];
            pStarred.Value = reader["starred"] == DBNull.Value ? 0 : reader["starred"];
            pTags.Value = reader["tags"] == DBNull.Value ? "," : reader["tags"];
            pUrl.Value = reader["url"] == DBNull.Value ? string.Empty : reader["url"];

            await insertCmd.ExecuteNonQueryAsync();
            importedCount++;
            batchSize++;

            if (batchSize >= 500)
            {
                progressCallback?.Invoke(importedCount, totalCount);
                batchSize = 0;
            }
        }

        await tx.CommitAsync();
        progressCallback?.Invoke(importedCount, totalCount);

        return importedCount;
    }
}
