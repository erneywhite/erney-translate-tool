using System;
using System.Collections.Generic;
using System.IO;
using ErneyTranslateTool.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace ErneyTranslateTool.Data;

/// <summary>
/// SQLite store for the proper-noun glossary. Sits in its own DB file so
/// import/export can move just the glossary without touching the user's
/// translation cache or history.
/// </summary>
public class GlossaryRepository : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public GlossaryRepository(string appDataPath, ILogger logger)
    {
        _dbPath = Path.Combine(appDataPath, "glossary.db");
        _logger = logger;
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        InitializeDatabase();
        _logger.Information("Glossary repository initialized: {Path}", _dbPath);
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Glossary (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceText      TEXT NOT NULL,
                TargetText      TEXT NOT NULL,
                TargetLanguage  TEXT NOT NULL,
                IsCaseSensitive INTEGER NOT NULL DEFAULT 0,
                IsWholeWord     INTEGER NOT NULL DEFAULT 1,
                Notes           TEXT NOT NULL DEFAULT ''
            )";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Glossary_Lang
            ON Glossary(TargetLanguage)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Return every rule for the given target language (e.g. "RU"). Sorted
    /// longest-source-first so when two rules overlap (rare but possible)
    /// the more specific one wins — replacing "Geralt of Rivia" before a
    /// plain "Geralt" rule would have a chance to mangle it.
    /// </summary>
    public List<GlossaryEntry> GetForLanguage(string targetLanguage)
    {
        var list = new List<GlossaryEntry>();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, SourceText, TargetText, TargetLanguage,
                       IsCaseSensitive, IsWholeWord, Notes
                FROM Glossary
                WHERE TargetLanguage = @lang COLLATE NOCASE
                ORDER BY length(SourceText) DESC, Id ASC";
            cmd.Parameters.AddWithValue("@lang", targetLanguage);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(Read(reader));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary read failed for {Lang}", targetLanguage);
        }
        return list;
    }

    public List<GlossaryEntry> GetAll()
    {
        var list = new List<GlossaryEntry>();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, SourceText, TargetText, TargetLanguage,
                       IsCaseSensitive, IsWholeWord, Notes
                FROM Glossary
                ORDER BY TargetLanguage ASC, SourceText ASC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(Read(reader));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary read-all failed");
        }
        return list;
    }

    /// <summary>Insert a new rule and return its assigned Id (or 0 on failure).</summary>
    public long Add(GlossaryEntry entry)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Glossary (SourceText, TargetText, TargetLanguage,
                    IsCaseSensitive, IsWholeWord, Notes)
                VALUES (@src, @dst, @lang, @cs, @ww, @notes);
                SELECT last_insert_rowid();";
            BindEntry(cmd, entry);
            entry.Id = (long)(cmd.ExecuteScalar() ?? 0L);
            return entry.Id;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary add failed for {Source} -> {Target}",
                entry.SourceText, entry.TargetText);
            return 0;
        }
    }

    public bool Update(GlossaryEntry entry)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE Glossary
                SET SourceText = @src, TargetText = @dst, TargetLanguage = @lang,
                    IsCaseSensitive = @cs, IsWholeWord = @ww, Notes = @notes
                WHERE Id = @id";
            BindEntry(cmd, entry);
            cmd.Parameters.AddWithValue("@id", entry.Id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary update failed for Id={Id}", entry.Id);
            return false;
        }
    }

    public bool Delete(long id)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Glossary WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary delete failed for Id={Id}", id);
            return false;
        }
    }

    public int Count()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Glossary";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glossary count failed");
            return 0;
        }
    }

    private static void BindEntry(SqliteCommand cmd, GlossaryEntry entry)
    {
        cmd.Parameters.AddWithValue("@src", entry.SourceText ?? string.Empty);
        cmd.Parameters.AddWithValue("@dst", entry.TargetText ?? string.Empty);
        cmd.Parameters.AddWithValue("@lang", entry.TargetLanguage ?? "RU");
        cmd.Parameters.AddWithValue("@cs", entry.IsCaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@ww", entry.IsWholeWord ? 1 : 0);
        cmd.Parameters.AddWithValue("@notes", entry.Notes ?? string.Empty);
    }

    private static GlossaryEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        SourceText = r.GetString(1),
        TargetText = r.GetString(2),
        TargetLanguage = r.GetString(3),
        IsCaseSensitive = r.GetInt32(4) != 0,
        IsWholeWord = r.GetInt32(5) != 0,
        Notes = r.IsDBNull(6) ? string.Empty : r.GetString(6),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _connection.Close();
        _connection.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
