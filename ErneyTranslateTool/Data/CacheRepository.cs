using System;
using System.IO;
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
    private SqliteConnection _connection;
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

    /// <summary>
    /// Clear all cached translations.
    /// </summary>
    /// <returns>Number of entries deleted.</returns>
    public int ClearCache()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM TranslationCache";
            var rows = cmd.ExecuteNonQuery();
            _logger.Information("Cache cleared: {Rows} entries deleted", rows);
            return rows;
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
