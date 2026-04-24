using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Serilog;

namespace ErneyTranslateTool.Data;

/// <summary>
/// SQLite repository for translation cache persistence.
/// </summary>
public class CacheRepository : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly SqliteConnection _connection;
    // Serialises cache writes against background eviction so neither steps
    // on the other's transaction. Reads aren't gated — SQLite handles them.
    private readonly object _writeLock = new();
    // Atomic flag preventing more than one eviction running at a time. We
    // can be triggered by every cache miss; if cleanup is already in flight,
    // subsequent triggers just no-op.
    private int _evictionInFlight;
    private bool _disposed;

    /// <summary>
    /// Initialize cache repository.
    /// </summary>
    /// <param name="appDataPath">Application data directory.</param>
    /// <param name="logger">Logger instance.</param>
    public CacheRepository(string appDataPath, ILogger logger)
    {
        _dbPath = Path.Combine(appDataPath, "cache.db");
        _logger = logger;
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        InitializeDatabase();
        _logger.Information("Cache repository initialized: {Path}", _dbPath);
    }

    /// <summary>
    /// Initialize database schema.
    /// </summary>
    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS TranslationCache (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceText TEXT NOT NULL,
                TargetLanguage TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                SourceLanguage TEXT,
                CreatedAt TEXT NOT NULL,
                AccessedAt TEXT NOT NULL,
                UNIQUE(SourceText, TargetLanguage)
            )";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Cache_Text
            ON TranslationCache(SourceText, TargetLanguage)";
        cmd.ExecuteNonQuery();

        // Eviction queries sort by AccessedAt — index dramatically speeds it up
        // on large caches (linear scan would dominate cleanup time).
        cmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Cache_AccessedAt
            ON TranslationCache(AccessedAt)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get cached translation.
    /// </summary>
    /// <param name="sourceText">Source text to look up.</param>
    /// <param name="targetLanguage">Target language code.</param>
    /// <returns>Translated text or null if not found.</returns>
    public string? GetTranslation(string sourceText, string targetLanguage)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT TranslatedText, SourceLanguage
                FROM TranslationCache
                WHERE SourceText = @sourceText AND TargetLanguage = @targetLang";
            cmd.Parameters.AddWithValue("@sourceText", sourceText);
            cmd.Parameters.AddWithValue("@targetLang", targetLanguage);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                // Update access time
                UpdateAccessTime(sourceText, targetLanguage);
                return reader.GetString(0);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get cached translation");
        }
        return null;
    }

    /// <summary>
    /// Store translation in cache.
    /// </summary>
    /// <param name="sourceText">Source text.</param>
    /// <param name="translatedText">Translated text.</param>
    /// <param name="targetLanguage">Target language code.</param>
    /// <param name="sourceLanguage">Detected source language.</param>
    public void SaveTranslation(string sourceText, string translatedText,
        string targetLanguage, string? sourceLanguage)
    {
        try
        {
            lock (_writeLock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO TranslationCache
                    (SourceText, TargetLanguage, TranslatedText, SourceLanguage, CreatedAt, AccessedAt)
                    VALUES (@sourceText, @targetLang, @translated, @sourceLang, @now, @now)";
                cmd.Parameters.AddWithValue("@sourceText", sourceText);
                cmd.Parameters.AddWithValue("@targetLang", targetLanguage);
                cmd.Parameters.AddWithValue("@translated", translatedText);
                cmd.Parameters.AddWithValue("@sourceLang", sourceLanguage ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save translation to cache");
        }
    }

    /// <summary>
    /// Update cache entry access time.
    /// </summary>
    private void UpdateAccessTime(string sourceText, string targetLanguage)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE TranslationCache
                SET AccessedAt = @now
                WHERE SourceText = @sourceText AND TargetLanguage = @targetLang";
            cmd.Parameters.AddWithValue("@sourceText", sourceText);
            cmd.Parameters.AddWithValue("@targetLang", targetLanguage);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Clear all cached translations.
    /// </summary>
    /// <returns>Number of entries deleted.</returns>
    public int ClearCache()
    {
        try
        {
            lock (_writeLock)
            {
                int rows;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM TranslationCache";
                    rows = cmd.ExecuteNonQuery();
                }
                // Reclaim disk space — without VACUUM the file stays the same size.
                using (var vac = _connection.CreateCommand())
                {
                    vac.CommandText = "VACUUM";
                    vac.ExecuteNonQuery();
                }
                _logger.Information("Cache cleared: {Rows} entries deleted", rows);
                return rows;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to clear cache");
            return 0;
        }
    }

    /// <summary>
    /// Get cache size (number of entries).
    /// </summary>
    public int GetCacheSize()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM TranslationCache";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get cache size");
            return 0;
        }
    }

    /// <summary>
    /// Get cache database file size in bytes.
    /// </summary>
    public long GetCacheFileSize()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                return new FileInfo(_dbPath).Length;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get cache file size");
        }
        return 0;
    }

    /// <summary>
    /// Returns row count + on-disk file size in a single struct, so callers
    /// don't have to make two separate trips for the stats display.
    /// </summary>
    public CacheStats GetStats() => new(GetCacheSize(), GetCacheFileSize());

    /// <summary>
    /// Trim the cache down to ~90% of the configured limit by evicting the
    /// least-recently-accessed entries. Safe to call from a background thread;
    /// silently no-ops when another eviction is already in flight or when
    /// no limit is configured.
    /// </summary>
    /// <param name="maxBytes">Hard cap in bytes. Pass 0 to disable.</param>
    /// <returns>Number of entries deleted; 0 if no work was needed/done.</returns>
    public int EnforceSizeLimit(long maxBytes)
    {
        if (maxBytes <= 0) return 0;

        // Only attempt cleanup once we're meaningfully over the limit (10 %
        // headroom). Stops us doing a VACUUM after every single insertion
        // when we're hovering right at the threshold.
        var currentSize = GetCacheFileSize();
        if (currentSize <= maxBytes * 1.1) return 0;

        // Atomic tryEnter — bail if a previous eviction is still running.
        if (Interlocked.CompareExchange(ref _evictionInFlight, 1, 0) != 0) return 0;

        try
        {
            // Target ~90 % so we don't have to come back immediately.
            var targetBytes = (long)(maxBytes * 0.9);
            var sw = Stopwatch.StartNew();

            // Estimate average row cost from current state, then derive how
            // many rows to drop. Cheap and resilient: even if the average is
            // off by 2× we just iterate again next time around.
            int totalRows;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM TranslationCache";
                totalRows = Convert.ToInt32(cmd.ExecuteScalar());
            }
            if (totalRows == 0) return 0;

            var avgRowBytes = Math.Max(1.0, (double)currentSize / totalRows);
            var bytesToShed = currentSize - targetBytes;
            // Cap at half the table per pass so a misconfigured tiny limit
            // doesn't nuke 99 % in one shot — we'd rather chip away.
            var rowsToDelete = Math.Min(totalRows / 2,
                Math.Max(1, (int)Math.Ceiling(bytesToShed / avgRowBytes)));

            int deleted;
            lock (_writeLock)
            {
                using var tx = _connection.BeginTransaction();
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        DELETE FROM TranslationCache
                        WHERE Id IN (
                            SELECT Id FROM TranslationCache
                            ORDER BY AccessedAt ASC
                            LIMIT @limit
                        )";
                    cmd.Parameters.AddWithValue("@limit", rowsToDelete);
                    deleted = cmd.ExecuteNonQuery();
                }
                tx.Commit();

                // VACUUM cannot run inside a transaction. Reclaims disk space
                // so the file actually shrinks; without it SQLite would just
                // mark pages free but keep the file at high-water-mark.
                using var vac = _connection.CreateCommand();
                vac.CommandText = "VACUUM";
                vac.ExecuteNonQuery();
            }

            var newSize = GetCacheFileSize();
            _logger.Information(
                "Cache eviction: removed {Deleted} entries in {Ms} ms, size {Old:N0} -> {New:N0} bytes (limit {Limit:N0})",
                deleted, sw.ElapsedMilliseconds, currentSize, newSize, maxBytes);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Cache eviction failed");
            return 0;
        }
        finally
        {
            Interlocked.Exchange(ref _evictionInFlight, 0);
        }
    }

    /// <summary>
    /// Fire-and-forget eviction on the thread pool. Safe to call from the
    /// hot translate path — won't block, won't throw, and won't pile up.
    /// </summary>
    public void EnforceSizeLimitInBackground(long maxBytes)
    {
        if (maxBytes <= 0) return;
        Task.Run(() =>
        {
            try { EnforceSizeLimit(maxBytes); }
            catch (Exception ex) { _logger.Error(ex, "Background eviction crashed"); }
        });
    }

    /// <summary>
    /// Dispose database connection.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Close();
            _connection?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>Lightweight bundle of cache row count + disk size for the settings UI.</summary>
public readonly record struct CacheStats(int Entries, long Bytes);
