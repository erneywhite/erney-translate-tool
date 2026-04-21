namespace ErneyTranslateTool.Models
{
    public class LanguageInfo
    {
        public string Code { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;

        public override string ToString() => DisplayName;

        public static LanguageInfo[] GetSupportedTargetLanguages() =>
        [
            new() { Code = "RU", DisplayName = "Русский" },
            new() { Code = "EN-US", DisplayName = "Английский" },
            new() { Code = "DE", DisplayName = "Немецкий" },
            new() { Code = "FR", DisplayName = "Французский" },
            new() { Code = "ES", DisplayName = "Испанский" },
            new() { Code = "IT", DisplayName = "Итальянский" },
            new() { Code = "PT-BR", DisplayName = "Португальский" },
            new() { Code = "ZH", DisplayName = "Китайский" },
            new() { Code = "JA", DisplayName = "Японский" },
            new() { Code = "KO", DisplayName = "Корейский" },
            new() { Code = "PL", DisplayName = "Польский" },
            new() { Code = "UK", DisplayName = "Украинский" },
        ];
    }
}
