using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
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
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loadedConfig = JsonSerializer.Deserialize<AppConfig>(json);
                if (loadedConfig != null)
                {
                    _config = loadedConfig;
                    _logger.Information("Settings loaded from {Path}", _settingsPath);
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

    /// <summary>
    /// Save settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_settingsPath, json);
            _logger.Debug("Settings saved");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save settings");
        }
    }

    /// <summary>
    /// Set DeepL API key (encrypts before storing).
    /// </summary>
    /// <param name="apiKey">Plain text API key.</param>
    public void SetApiKey(string apiKey)
    {
        try
        {
            var encrypted = Protect(apiKey);
            _config.EncryptedApiKey = Convert.ToBase64String(encrypted);
            Save();
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
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _config.EncryptedOpenAIKey = null;
            }
            else
            {
                _config.EncryptedOpenAIKey = Convert.ToBase64String(Protect(apiKey));
            }
            Save();
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
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _config.EncryptedAnthropicKey = null;
            }
            else
            {
                _config.EncryptedAnthropicKey = Convert.ToBase64String(Protect(apiKey));
            }
            Save();
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
