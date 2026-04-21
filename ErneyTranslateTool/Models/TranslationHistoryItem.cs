using System;

namespace ErneyTranslateTool.Models
{
    public class TranslationHistoryItem
    {
        public long Id { get; set; }
        public string SourceText { get; set; } = string.Empty;
        public string TranslatedText { get; set; } = string.Empty;
        public string SourceLanguage { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string GameName { get; set; } = string.Empty;
    }
}
