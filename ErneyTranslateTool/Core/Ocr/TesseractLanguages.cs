using System.Collections.Generic;

namespace ErneyTranslateTool.Core.Ocr;

/// <summary>
/// Curated catalog of common gaming/visual-novel languages available
/// from tessdata_fast on GitHub.
/// </summary>
public static class TesseractLanguages
{
    public record Entry(string Code, string DisplayName, double SizeMb);

    public static readonly IReadOnlyList<Entry> Catalog = new List<Entry>
    {
        new("eng", "Английский", 4.0),
        new("rus", "Русский", 4.4),
        new("jpn", "Японский", 2.4),
        new("jpn_vert", "Японский (вертикальный)", 1.8),
        new("chi_sim", "Китайский (упрощённый)", 4.6),
        new("chi_sim_vert", "Китайский упр. (вертикальный)", 2.0),
        new("chi_tra", "Китайский (традиционный)", 5.0),
        new("chi_tra_vert", "Китайский трад. (вертикальный)", 2.4),
        new("kor", "Корейский", 4.7),
        new("kor_vert", "Корейский (вертикальный)", 2.1),
        new("deu", "Немецкий", 5.4),
        new("fra", "Французский", 4.4),
        new("spa", "Испанский", 5.8),
        new("ita", "Итальянский", 4.4),
        new("por", "Португальский", 4.0),
        new("pol", "Польский", 4.5),
        new("ukr", "Украинский", 4.4),
        new("ces", "Чешский", 4.7),
        new("nld", "Голландский", 5.0),
        new("tur", "Турецкий", 4.6),
        new("ara", "Арабский", 1.9),
    };

    public static string DisplayNameFor(string code)
    {
        foreach (var e in Catalog)
            if (e.Code == code) return e.DisplayName;
        return code;
    }
}
