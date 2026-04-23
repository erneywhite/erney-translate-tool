using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Data;

/// <summary>
/// SQLite repository for translation history persistence.
/// </summary>
public class HistoryRepository : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private SqliteConnection _connection;
    private bool _disposed;
    private int _currentSessionId;

    /// <summary>
    /// Current active session ID.
    /// </summary>
    public int CurrentSessionId => _currentSessionId;

    /// <summary>
    /// Initialize history repository.
    /// </summary>
    /// <param name="appDataPath">Application data directory.</param>
    /// <param name="logger">Logger instance.</param>
    public HistoryRepository(string appDataPath, ILogger logger)
    {
        _dbPath = Path.Combine(appDataPath, "history.db");
        _logger = logger;
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        InitializeDatabase();
        _logger.Information("History repository initialized: {Path}", _dbPath);
    }

    /// <summary>
    /// Initialize database schema.
    /// </summary>
    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Sessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StartTime TEXT NOT NULL,
                WindowTitle TEXT NOT NULL,
                TotalCharacters INTEGER DEFAULT 0,
                TotalTranslations INTEGER DEFAULT 0
            )";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Translations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL,
                Timestamp TEXT NOT NULL,
                OriginalText TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                SourceLanguage TEXT,
                IsFromCache INTEGER DEFAULT 0,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            )";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Sessions_StartTime 
            ON Sessions(StartTime DESC)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Translations_SessionId 
            ON Translations(SessionId)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Start a new translation session.
    /// </summary>
    /// <param name="windowTitle">Target window title.</param>
    /// <returns>New session ID.</returns>
    public int StartSession(string windowTitle)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Sessions (StartTime, WindowTitle)
                VALUES (@startTime, @windowTitle)";
            cmd.Parameters.AddWithValue("@startTime", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@windowTitle", windowTitle);
            cmd.ExecuteNonQuery();

            using var idCmd = _connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            _currentSessionId = Convert.ToInt32(idCmd.ExecuteScalar());
            _logger.Information("New session started: {Id} for window: {Title}", 
                _currentSessionId, windowTitle);
            return _currentSessionId;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start session");
            return 0;
        }
    }

    /// <summary>
    /// End current session and update statistics.
    /// </summary>
    /// <param name="totalCharacters">Total characters translated.</param>
    /// <param name="totalTranslations">Total translations performed.</param>
    public void EndSession(int totalCharacters, int totalTranslations)
    {
        if (_currentSessionId <= 0) return;

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE Sessions 
                SET TotalCharacters = @chars, TotalTranslations = @trans 
                WHERE Id = @id";
            cmd.Parameters.AddWithValue("@chars", totalCharacters);
            cmd.Parameters.AddWithValue("@trans", totalTranslations);
            cmd.Parameters.AddWithValue("@id", _currentSessionId);
            cmd.ExecuteNonQuery();

            _logger.Information("Session {Id} ended: {Chars} chars, {Trans} translations",
                _currentSessionId, totalCharacters, totalTranslations);
            _currentSessionId = 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to end session");
        }
    }

    /// <summary>
    /// Add translation entry to current session.
    /// </summary>
    /// <param name="originalText">Source text.</param>
    /// <param name="translatedText">Translated text.</param>
    /// <param name="sourceLanguage">Detected source language.</param>
    /// <param name="isFromCache">Whether from cache.</param>
    public void AddTranslation(string originalText, string translatedText,
        string sourceLanguage, bool isFromCache)
    {
        if (_currentSessionId <= 0) return;

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Translations 
                (SessionId, Timestamp, OriginalText, TranslatedText, SourceLanguage, IsFromCache)
                VALUES (@sessionId, @timestamp, @original, @translated, @sourceLang, @isCache)";
            cmd.Parameters.AddWithValue("@sessionId", _currentSessionId);
            cmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@original", originalText);
            cmd.Parameters.AddWithValue("@translated", translatedText);
            cmd.Parameters.AddWithValue("@sourceLang", sourceLanguage);
            cmd.Parameters.AddWithValue("@isCache", isFromCache ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to add translation to history");
        }
    }

    /// <summary>
    /// Get all sessions with their translation counts.
    /// </summary>
    /// <returns>List of sessions ordered by start time (newest first).</returns>
    public List<SessionHistory> GetSessions()
    {
        var sessions = new List<SessionHistory>();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, StartTime, WindowTitle, TotalCharacters, TotalTranslations
                FROM Sessions
                ORDER BY StartTime DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new SessionHistory
                {
                    Id = reader.GetInt32(0),
                    StartTime = DateTime.Parse(reader.GetString(1)),
                    WindowTitle = reader.GetString(2),
                    TotalCharacters = reader.GetInt32(3),
                    TotalTranslations = reader.GetInt32(4)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get sessions");
        }
        return sessions;
    }

    /// <summary>
    /// Get translations for a specific session.
    /// </summary>
    /// <param name="sessionId">Session ID.</param>
    /// <returns>List of translation entries.</returns>
    public List<TranslationEntry> GetSessionTranslations(int sessionId)
    {
        var entries = new List<TranslationEntry>();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, SessionId, Timestamp, OriginalText, TranslatedText, SourceLanguage, IsFromCache
                FROM Translations
                WHERE SessionId = @sessionId
                ORDER BY Timestamp DESC";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new TranslationEntry
                {
                    Id = reader.GetInt32(0),
                    SessionId = reader.GetInt32(1),
                    Timestamp = DateTime.Parse(reader.GetString(2)),
                    OriginalText = reader.GetString(3),
                    TranslatedText = reader.GetString(4),
                    SourceLanguage = reader.GetString(5),
                    IsFromCache = reader.GetInt32(6) == 1
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get session translations");
        }
        return entries;
    }

    /// <summary>
    /// Search translations by text.
    /// </summary>
    /// <param name="searchTerm">Search term.</param>
    /// <returns>List of matching translation entries.</returns>
    public List<TranslationEntry> SearchTranslations(string searchTerm)
    {
        var entries = new List<TranslationEntry>();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT t.Id, t.SessionId, t.Timestamp, t.OriginalText, t.TranslatedText, t.SourceLanguage, t.IsFromCache
                FROM Translations t
                WHERE t.OriginalText LIKE @search OR t.TranslatedText LIKE @search
                ORDER BY t.Timestamp DESC
                LIMIT 100";
            cmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new TranslationEntry
                {
                    Id = reader.GetInt32(0),
                    SessionId = reader.GetInt32(1),
                    Timestamp = DateTime.Parse(reader.GetString(2)),
                    OriginalText = reader.GetString(3),
                    TranslatedText = reader.GetString(4),
                    SourceLanguage = reader.GetString(5),
                    IsFromCache = reader.GetInt32(6) == 1
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to search translations");
        }
        return entries;
    }

    /// <summary>
    /// Clear all history.
    /// </summary>
    public void ClearHistory()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Translations";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM Sessions";
            cmd.ExecuteNonQuery();
            _logger.Information("History cleared");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to clear history");
        }
    }

    /// <summary>
    /// Delete a specific session.
    /// </summary>
    /// <param name="sessionId">Session ID to delete.</param>
    public void DeleteSession(int sessionId)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Translations WHERE SessionId = @id";
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM Sessions WHERE Id = @id";
            cmd.ExecuteNonQuery();
            _logger.Information("Session {Id} deleted", sessionId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete session");
        }
    }

    /// <summary>
    /// Dispose database connection.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_currentSessionId > 0)
            {
                EndSession(0, 0);
            }
            _connection?.Close();
            _connection?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
