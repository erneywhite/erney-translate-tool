using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ErneyTranslateTool.Core.Ocr;
using Serilog;
using Xunit.Abstractions;

namespace ErneyTranslateTool.Tests;

/// <summary>
/// Manual OCR benchmark: a 2560 px "web page" with 14 px text, read with
/// different models and scale limits. Runs only with ETT_OCR_BENCH=1 (it
/// downloads models and takes a while). Prints word recall and time.
/// </summary>
public class OcrBenchmark
{
    private readonly ITestOutputHelper _out;
    public OcrBenchmark(ITestOutputHelper o) => _out = o;

    private static readonly string[] Paragraph =
    {
        "A Telegram astrology product in one bot:",
        "Free: daily horoscopes for all 12 signs, posted every morning to the EN, RU and ES channels (London, Moscow and",
        "Bogota time) and available in the bot with Today's horoscope (for the reader's own date: their subscription's",
        "timezone, else their birthplace's). Each sign post has buttons that open a paid product straight in the bot.",
        "Paid (Telegram Stars): Personal, Love, Career and Match readings built from the user's own natal chart. Available",
        "one-time, or as a monthly subscription with a fresh reading every day at the user's chosen time.",
        "The bot, the background job worker and the scheduler all run in one Python process, with one SQLite database (a file,",
        "no database server). The only outside services are Telegram and one AI provider (Anthropic or OpenAI, switchable).",
        "The bot polls Telegram, so it needs no domain, web server or open ports.",
    };

    private static byte[] RenderPage(float fontPx)
    {
        using var bmp = new Bitmap(2560, 1392, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(13, 17, 23));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI", fontPx, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(230, 237, 243));
        float y = 370;
        foreach (var line in Paragraph)
        {
            g.DrawString(line, font, brush, 700, y);
            y += fontPx * 1.8f;
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static double Recall(IEnumerable<string> recognised)
    {
        static IEnumerable<string> Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()).Where(w => w.Length > 0);
        var got = Words(string.Join(" ", recognised)).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var want = Words(string.Join(" ", Paragraph)).ToList();
        var hit = 0;
        foreach (var w in want)
            if (got.TryGetValue(w, out var n) && n > 0) { hit++; got[w] = n - 1; }
        return (double)hit / want.Count;
    }

    [Fact]
    public void DenseSmallText()
    {
        if (Environment.GetEnvironmentVariable("ETT_OCR_BENCH") != "1") return;
        var log = new LoggerConfiguration().CreateLogger();
        foreach (var lang in new[] { "en" })
        {
            var backend = new PaddleOcrBackend(log, lang);
            var sw = Stopwatch.StartNew();
            while (backend.State != OcrBackendState.Ready && sw.Elapsed < TimeSpan.FromMinutes(5)) Thread.Sleep(200);
            Assert.Equal(OcrBackendState.Ready, backend.State);

            foreach (var px in new[] { 14f, 18f })
            {
                var png = RenderPage(px);
                foreach (var (maxWidth, detMax) in new (int, int?)[] { (4096, 1600), (4096, 1920), (4096, 2048), (4096, 2560) })
                {
                    backend.MaxOcrWidth = maxWidth;
                    backend.DetectorMaxSize = detMax;
                    backend.ProcessFrame(png); // warm-up
                    var t = Stopwatch.StartNew();
                    List<ErneyTranslateTool.Models.TranslationRegion> r = null!;
                    for (int i = 0; i < 3; i++) r = backend.ProcessFrame(png);
                    var ms = t.ElapsedMilliseconds / 3;
                    _out.WriteLine($"{lang,-3} text {px,2}px  scale-cap {maxWidth,4}  det-max {(detMax?.ToString() ?? "default"),7}: " +
                                   $"recall {Recall(r.Select(x => x.OriginalText)):P0}, {r.Count,2} lines, {ms,4} ms");
                }
            }
            backend.Dispose();
        }
    }
}
