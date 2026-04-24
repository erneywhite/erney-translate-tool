using System;
using System.Collections.Generic;
using System.IO;
using ErneyTranslateTool.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace ErneyTranslateTool.Data;

/// <summary>
/// SQLite store for game profiles. Stored in <c>profiles.db</c> alongside
/// cache/history/glossary so import/export can ship just the profiles
/// without dragging the user's translation cache.
/// </summary>
public class GameProfileRepository : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public GameProfileRepository(string appDataPath, ILogger logger)
    {
        _dbPath = Path.Combine(appDataPath, "profiles.db");
        _logger = logger;
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        InitializeDatabase();
        _logger.Information("GameProfile repository initialized: {Path}", _dbPath);
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS GameProfiles (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                Name                TEXT NOT NULL,
                MatchPattern        TEXT NOT NULL DEFAULT '',
                MatchByProcessName  INTEGER NOT NULL DEFAULT 0,

                OcrEngine           TEXT NOT NULL DEFAULT 'PaddleOCR',
                SourceLanguage      TEXT NOT NULL DEFAULT '',
                TesseractLanguage   TEXT NOT NULL DEFAULT 'eng',
                PaddleLanguage      TEXT NOT NULL DEFAULT 'en',

                TargetLanguage      TEXT NOT NULL DEFAULT 'RU',
                TranslationProvider TEXT NOT NULL DEFAULT 'MyMemory',

                OverlayFontFamily   TEXT NOT NULL DEFAULT 'Segoe UI',
                FontSizeMode        TEXT NOT NULL DEFAULT 'Auto',
                ManualFontSize      REAL NOT NULL DEFAULT 14,
                OverlayOpacity      REAL NOT NULL DEFAULT 0.95,
                BackgroundColor     TEXT NOT NULL DEFAULT '#000000',
                TextColor           TEXT NOT NULL DEFAULT '#FFFFFF',
                OverlayCornerRadius REAL NOT NULL DEFAULT 4,

                GlossaryEnabled     INTEGER NOT NULL DEFAULT 1
            )";
        cmd.ExecuteNonQuery();
    }

    public List<GameProfile> GetAll()
    {
        var list = new List<GameProfile>();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM GameProfiles ORDER BY Id ASC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(Read(reader));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GameProfile read-all failed");
        }
        return list;
    }

    public GameProfile? GetById(long id)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM GameProfiles WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GameProfile GetById failed");
            return null;
        }
    }

    /// <summary>
    /// Insert a profile. If the supplied entry has Id==0 SQLite assigns the
    /// next free id; pass an explicit id (e.g. <see cref="GameProfile.DefaultProfileId"/>)
    /// to seed the migration row.
    /// </summary>
    public long Add(GameProfile p)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            // ROWID seeding trick: when Id == DefaultProfileId we insert the
            // explicit value so the row owns id=1; otherwise let SQLite assign.
            if (p.Id == GameProfile.DefaultProfileId)
            {
                cmd.CommandText = @"
                    INSERT INTO GameProfiles (Id, Name, MatchPattern, MatchByProcessName,
                        OcrEngine, SourceLanguage, TesseractLanguage, PaddleLanguage,
                        TargetLanguage, TranslationProvider,
                        OverlayFontFamily, FontSizeMode, ManualFontSize, OverlayOpacity,
                        BackgroundColor, TextColor, OverlayCornerRadius, GlossaryEnabled)
                    VALUES (@id, @name, @pat, @byProc,
                        @ocr, @src, @tess, @paddle,
                        @target, @provider,
                        @font, @sizeMode, @manualSize, @opacity,
                        @bg, @fg, @radius, @gloss);
                    SELECT @id;";
                BindAll(cmd, p);
                cmd.Parameters.AddWithValue("@id", p.Id);
                p.Id = (long)(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                cmd.CommandText = @"
                    INSERT INTO GameProfiles (Name, MatchPattern, MatchByProcessName,
                        OcrEngine, SourceLanguage, TesseractLanguage, PaddleLanguage,
                        TargetLanguage, TranslationProvider,
                        OverlayFontFamily, FontSizeMode, ManualFontSize, OverlayOpacity,
                        BackgroundColor, TextColor, OverlayCornerRadius, GlossaryEnabled)
                    VALUES (@name, @pat, @byProc,
                        @ocr, @src, @tess, @paddle,
                        @target, @provider,
                        @font, @sizeMode, @manualSize, @opacity,
                        @bg, @fg, @radius, @gloss);
                    SELECT last_insert_rowid();";
                BindAll(cmd, p);
                p.Id = (long)(cmd.ExecuteScalar() ?? 0L);
            }
            return p.Id;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GameProfile add failed for {Name}", p.Name);
            return 0;
        }
    }

    public bool Update(GameProfile p)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE GameProfiles SET
                    Name = @name, MatchPattern = @pat, MatchByProcessName = @byProc,
                    OcrEngine = @ocr, SourceLanguage = @src,
                    TesseractLanguage = @tess, PaddleLanguage = @paddle,
                    TargetLanguage = @target, TranslationProvider = @provider,
                    OverlayFontFamily = @font, FontSizeMode = @sizeMode,
                    ManualFontSize = @manualSize, OverlayOpacity = @opacity,
                    BackgroundColor = @bg, TextColor = @fg,
                    OverlayCornerRadius = @radius, GlossaryEnabled = @gloss
                WHERE Id = @id";
            BindAll(cmd, p);
            cmd.Parameters.AddWithValue("@id", p.Id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GameProfile update failed Id={Id}", p.Id);
            return false;
        }
    }

    public bool Delete(long id)
    {
        if (id == GameProfile.DefaultProfileId)
        {
            // Hard guard — the default profile must always exist as fallback.
            _logger.Warning("Refusing to delete default profile (Id=1)");
            return false;
        }
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM GameProfiles WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GameProfile delete failed Id={Id}", id);
            return false;
        }
    }

    public int Count()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM GameProfiles";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GameProfile count failed");
            return 0;
        }
    }

    private static void BindAll(SqliteCommand cmd, GameProfile p)
    {
        cmd.Parameters.AddWithValue("@name", p.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("@pat", p.MatchPattern ?? string.Empty);
        cmd.Parameters.AddWithValue("@byProc", p.MatchByProcessName ? 1 : 0);

        cmd.Parameters.AddWithValue("@ocr", p.OcrEngine ?? "PaddleOCR");
        cmd.Parameters.AddWithValue("@src", p.SourceLanguage ?? string.Empty);
        cmd.Parameters.AddWithValue("@tess", p.TesseractLanguage ?? "eng");
        cmd.Parameters.AddWithValue("@paddle", p.PaddleLanguage ?? "en");

        cmd.Parameters.AddWithValue("@target", p.TargetLanguage ?? "RU");
        cmd.Parameters.AddWithValue("@provider", p.TranslationProvider ?? "MyMemory");

        cmd.Parameters.AddWithValue("@font", p.OverlayFontFamily ?? "Segoe UI");
        cmd.Parameters.AddWithValue("@sizeMode", p.FontSizeMode ?? "Auto");
        cmd.Parameters.AddWithValue("@manualSize", p.ManualFontSize);
        cmd.Parameters.AddWithValue("@opacity", p.OverlayOpacity);
        cmd.Parameters.AddWithValue("@bg", p.BackgroundColor ?? "#000000");
        cmd.Parameters.AddWithValue("@fg", p.TextColor ?? "#FFFFFF");
        cmd.Parameters.AddWithValue("@radius", p.OverlayCornerRadius);
        cmd.Parameters.AddWithValue("@gloss", p.GlossaryEnabled ? 1 : 0);
    }

    private static GameProfile Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Name = r.GetString(r.GetOrdinal("Name")),
        MatchPattern = r.GetString(r.GetOrdinal("MatchPattern")),
        MatchByProcessName = r.GetInt32(r.GetOrdinal("MatchByProcessName")) != 0,

        OcrEngine = r.GetString(r.GetOrdinal("OcrEngine")),
        SourceLanguage = r.GetString(r.GetOrdinal("SourceLanguage")),
        TesseractLanguage = r.GetString(r.GetOrdinal("TesseractLanguage")),
        PaddleLanguage = r.GetString(r.GetOrdinal("PaddleLanguage")),

        TargetLanguage = r.GetString(r.GetOrdinal("TargetLanguage")),
        TranslationProvider = r.GetString(r.GetOrdinal("TranslationProvider")),

        OverlayFontFamily = r.GetString(r.GetOrdinal("OverlayFontFamily")),
        FontSizeMode = r.GetString(r.GetOrdinal("FontSizeMode")),
        ManualFontSize = r.GetDouble(r.GetOrdinal("ManualFontSize")),
        OverlayOpacity = r.GetDouble(r.GetOrdinal("OverlayOpacity")),
        BackgroundColor = r.GetString(r.GetOrdinal("BackgroundColor")),
        TextColor = r.GetString(r.GetOrdinal("TextColor")),
        OverlayCornerRadius = r.GetDouble(r.GetOrdinal("OverlayCornerRadius")),
        GlossaryEnabled = r.GetInt32(r.GetOrdinal("GlossaryEnabled")) != 0,
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
