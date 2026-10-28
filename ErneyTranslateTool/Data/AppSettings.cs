using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using ErneyTranslateTool.Core.Translators;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Data;

/// <summary>
/// Manages application settings persistence with DPAPI encryption for sensitive data.
/// </summary>
public class AppSettings
{
    private readonly string _settingsPath;
    private readonly ILogger _logger;
    private AppConfig _config = new();

    /// <summary>
    /// Current application configuration.
    /// </summary>
    public AppConfig Config => _config;

    /// <summary>
    /// Initialize settings manager.
    /// </summary>
    /// <param name="appDataPath">Application data directory path.</param>
    /// <param name="logger">Logger instance.</param>
    public AppSettings(string appDataPath, ILogger logger)
    {
        _settingsPath = Path.Combine(appDataPath, "settings.json");
        _logger = logger;
    }

    /// <summary>
    /// Load settings from disk.
    /// </summary>
    public void Load()
    {
        try
        {
            var loadedConfig = TryRead(_settingsPath) ?? TryRead(BackupPath);
            if (loadedConfig == null && (File.Exists(_settingsPath) || File.Exists(BackupPath)))
                _logger.Error("settings.json and its backup are unreadable — starting with defaults");
            if (loadedConfig != null)
            {
                _config = loadedConfig;
                _logger.Information("Settings loaded from {Path}", _settingsPath);

                // Providers shut models down; a saved dead id would make
                // every request fail until the user noticed and changed it.
                var migrated = ModelCatalog.MigrateRetired(_config);
                if (migrated.Count > 0)
                {
                    foreach (var change in migrated)
                        _logger.Information("Retired model replaced: {Change}", change);
                    Save();
                }
            }
            else
            {
                _logger.Information("No settings file found, using defaults");
            }

            // Reset daily stats if new day
            if (_config.CharactersResetDate.Date < DateTime.UtcNow.Date)
            {
                _config.CharactersTranslatedToday = 0;
                _config.CharactersResetDate = DateTime.UtcNow;
                _logger.Information("Daily statistics reset");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load settings");
            _config = new AppConfig();
        }
    }

    private string BackupPath => _settingsPath + ".bak";
    private readonly object _saveLock = new();

    private AppConfig? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
            if (config != null && path == BackupPath)
                _logger.Warning("settings.json unreadable, restored from {Path}", path);
            return config;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Save settings to disk. v1.0.32: atomic — written to a temp file and
    /// swapped in with File.Replace, the previous file kept as .bak. A crash
    /// mid-write used to leave a truncated settings.json; Load then fell back
    /// to defaults and the next Save wiped every setting and stored key.
    /// Locked because stats updates save from the pipeline too.
    /// </summary>
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                var tmp = _settingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_settingsPath))
                    File.Replace(tmp, _settingsPath, BackupPath);
                else
                    File.Move(tmp, _settingsPath);
                _logger.Debug("Settings saved");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save settings");
            }
        }
    }

    /// <summary>
    /// Set DeepL API key (encrypts before storing). Like the other key
    /// setters it doesn't write the file — callers Save() once afterwards.
    /// </summary>
    /// <param name="apiKey">Plain text API key.</param>
    public void SetApiKey(string apiKey)
    {
        try
        {
            if (apiKey == GetApiKey()) return;
            var encrypted = Protect(apiKey);
            _config.EncryptedApiKey = Convert.ToBase64String(encrypted);
            _logger.Information("API key updated");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to encrypt API key");
            throw;
        }
    }

    /// <summary>
    /// Get decrypted DeepL API key.
    /// </summary>
    /// <returns>Plain text API key or null if not set.</returns>
    public string? GetApiKey()
    {
        if (string.IsNullOrEmpty(_config.EncryptedApiKey))
            return null;

        try
        {
            var encrypted = Convert.FromBase64String(_config.EncryptedApiKey);
            var decrypted = Unprotect(encrypted);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to decrypt API key");
            return null;
        }
    }

    /// <summary>
    /// Check if API key is configured.
    /// </summary>
    public bool HasApiKey() => !string.IsNullOrEmpty(_config.EncryptedApiKey);

    /// <summary>Persist (DPAPI-encrypt) the OpenAI key. Empty/whitespace clears it.</summary>
    public void SetOpenAIKey(string apiKey)
    {
        try
        {
            // DPAPI output differs on every call — only touch the config when
            // the key really changed, or every auto-save rewrites it.
            if ((string.IsNullOrWhiteSpace(apiKey) ? null : apiKey) == GetOpenAIKey()) return;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _config.EncryptedOpenAIKey = null;
            }
            else
            {
                _config.EncryptedOpenAIKey = Convert.ToBase64String(Protect(apiKey));
            }
            _logger.Information("OpenAI key updated");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to encrypt OpenAI key");
            throw;
        }
    }

    /// <summary>Returns the decrypted OpenAI key or null if not set.</summary>
    public string? GetOpenAIKey() => DecryptOrNull(_config.EncryptedOpenAIKey, "OpenAI");

    /// <summary>Persist (DPAPI-encrypt) the Anthropic key. Empty/whitespace clears it.</summary>
    public void SetAnthropicKey(string apiKey)
    {
        try
        {
            // DPAPI output differs on every call — only touch the config when
            // the key really changed, or every auto-save rewrites it.
            if ((string.IsNullOrWhiteSpace(apiKey) ? null : apiKey) == GetAnthropicKey()) return;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _config.EncryptedAnthropicKey = null;
            }
            else
            {
                _config.EncryptedAnthropicKey = Convert.ToBase64String(Protect(apiKey));
            }
            _logger.Information("Anthropic key updated");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to encrypt Anthropic key");
            throw;
        }
    }

    /// <summary>Returns the decrypted Anthropic key or null if not set.</summary>
    public string? GetAnthropicKey() => DecryptOrNull(_config.EncryptedAnthropicKey, "Anthropic");

    /// <summary>Persist (DPAPI-encrypt) the Gemini key. Empty/whitespace clears it.</summary>
    public void SetGeminiKey(string apiKey)
    {
        try
        {
            // DPAPI output differs on every call — only touch the config when
            // the key really changed, or every auto-save rewrites it.
            if ((string.IsNullOrWhiteSpace(apiKey) ? null : apiKey) == GetGeminiKey()) return;
            if (string.IsNullOrWhiteSpace(apiKey))
                _config.EncryptedGeminiKey = null;
            else
                _config.EncryptedGeminiKey = Convert.ToBase64String(Protect(apiKey));
            _logger.Information("Gemini key updated");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to encrypt Gemini key");
            throw;
        }
    }

    /// <summary>Returns the decrypted Gemini key or null if not set.</summary>
    public string? GetGeminiKey() => DecryptOrNull(_config.EncryptedGeminiKey, "Gemini");

    /// <summary>Persist (DPAPI-encrypt) the Groq key. Empty/whitespace clears it.</summary>
    public void SetGroqKey(string apiKey)
    {
        try
        {
            // DPAPI output differs on every call — only touch the config when
            // the key really changed, or every auto-save rewrites it.
            if ((string.IsNullOrWhiteSpace(apiKey) ? null : apiKey) == GetGroqKey()) return;
            if (string.IsNullOrWhiteSpace(apiKey))
                _config.EncryptedGroqKey = null;
            else
                _config.EncryptedGroqKey = Convert.ToBase64String(Protect(apiKey));
            _logger.Information("Groq key updated");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to encrypt Groq key");
            throw;
        }
    }

    /// <summary>Returns the decrypted Groq key or null if not set.</summary>
    public string? GetGroqKey() => DecryptOrNull(_config.EncryptedGroqKey, "Groq");

    /// <summary>Key for the OpenAI-compatible server. Empty clears it (local servers need none).</summary>
    public void SetCustomLlmKey(string apiKey)
    {
        var value = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (value == GetCustomLlmKey()) return;
        _config.EncryptedCustomLlmKey = value == null ? null : Convert.ToBase64String(Protect(value));
        _logger.Information("Custom LLM key updated");
    }

    public string? GetCustomLlmKey() => DecryptOrNull(_config.EncryptedCustomLlmKey, "Custom LLM");

    /// <summary>Shared decrypt helper — DRY for the LLM-key getters.</summary>
    private string? DecryptOrNull(string? encryptedB64, string label)
    {
        if (string.IsNullOrEmpty(encryptedB64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(encryptedB64);
            return Encoding.UTF8.GetString(Unprotect(bytes));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to decrypt {Label} key", label);
            return null;
        }
    }

    /// <summary>
    /// Protect data using Windows DPAPI.
    /// </summary>
    private static byte[] Protect(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Unprotect data using Windows DPAPI.
    /// </summary>
    private static byte[] Unprotect(byte[] encryptedData)
    {
        return ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Update translation statistics.
    /// </summary>
    /// <param name="charactersCount">Number of characters translated.</param>
    /// <param name="isCacheHit">Whether translation was from cache.</param>
    public void UpdateStats(int charactersCount, bool isCacheHit)
    {
        // v1.0.35: blocks of a frame are translated in parallel.
        lock (_statsLock)
            UpdateStatsLocked(charactersCount, isCacheHit);
    }

    private readonly object _statsLock = new();

    private void UpdateStatsLocked(int charactersCount, bool isCacheHit)
    {
        if (_config.CharactersResetDate.Date < DateTime.UtcNow.Date)
        {
            _config.CharactersTranslatedToday = 0;
            _config.CharactersResetDate = DateTime.UtcNow;
        }

        _config.CharactersTranslatedToday += charactersCount;
        
        if (isCacheHit)
            _config.CacheHits++;
        else
            _config.CacheMisses++;

        // Auto-save periodically (every 10 updates)
        if ((_config.CacheHits + _config.CacheMisses) % 10 == 0)
            Save();
    }

    /// <summary>
    /// Get cache hit rate percentage.
    /// </summary>
    public double GetCacheHitRate()
    {
        var total = _config.CacheHits + _config.CacheMisses;
        return total > 0 ? (double)_config.CacheHits / total * 100 : 0;
    }
}
