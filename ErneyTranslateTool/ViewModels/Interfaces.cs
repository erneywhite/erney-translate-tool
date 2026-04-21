using System;
using System.Drawing;
using System.Threading.Tasks;

namespace ErneyTranslateTool.Models
{
    public interface ICaptureService : IDisposable
    {
        event EventHandler<Bitmap>? FrameCaptured;
        event EventHandler? CaptureStopped;
        bool IsCapturing { get; }
        IntPtr TargetWindowHandle { get; }
        Task StartCaptureAsync(IntPtr windowHandle);
        Task StopCaptureAsync();
    }

    public interface IOcrService
    {
        Task<string> RecognizeTextAsync(Bitmap bitmap);
        System.Collections.Generic.IReadOnlyList<string> GetInstalledLanguages();
    }

    public interface ITranslationService
    {
        Task<string> TranslateAsync(string text, string targetLanguage);
        Task<bool> ValidateApiKeyAsync(string apiKey);
        int CacheHits { get; }
        int TotalRequests { get; }
    }

    public interface IOverlayManager : IDisposable
    {
        bool IsVisible { get; }
        void ShowTranslation(string text, System.Windows.Rect targetRect);
        void Hide();
        void UpdatePosition(IntPtr windowHandle);
    }

    public interface IHotkeyService : IDisposable
    {
        void RegisterHotkey(string id, int modifiers, int key, Action callback);
        void UnregisterHotkey(string id);
        void UnregisterAll();
    }

    public interface ICacheRepository : IDisposable
    {
        Task<string?> GetAsync(string sourceText, string targetLanguage);
        Task SetAsync(string sourceText, string targetLanguage, string translation);
        Task ClearAsync();
    }

    public interface IHistoryRepository : IDisposable
    {
        Task AddAsync(TranslationHistoryItem item);
        Task<System.Collections.Generic.List<TranslationHistoryItem>> GetPageAsync(
            int page, int pageSize, string? searchQuery = null,
            string? sourceLanguage = null, string? targetLanguage = null,
            DateTime? fromDate = null, DateTime? toDate = null);
        Task<int> GetTotalCountAsync(string? searchQuery = null,
            string? sourceLanguage = null, string? targetLanguage = null,
            DateTime? fromDate = null, DateTime? toDate = null);
        Task DeleteAsync(long id);
        Task ClearAllAsync();
    }
}
