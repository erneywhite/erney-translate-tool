using System.Text;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Tests;

/// <summary>
/// Replays recorded OCR frames through FrameLayout the way
/// TranslationEngine.OnFrameCaptured does, with the translator replaced by
/// "translation = original text". Mirrors the engine's branches (empty
/// frame, reuse, group + stabilise + remember); keep it in step when they
/// change.
/// </summary>
public sealed class LayoutReplay
{
    private const int VanishConfirmFrames = 2; // TranslationEngine.VanishConfirmFrames

    /// <summary>Overlay layout after each frame; null while the overlay is hidden.</summary>
    public List<string?> Layouts { get; } = new();

    public static LayoutReplay Run(IEnumerable<List<TranslationRegion>> frames, Action<int, string>? trace = null)
    {
        var replay = new LayoutReplay();
        var layout = new FrameLayout(new LoggerConfiguration().CreateLogger());
        string? shown = null;
        var vanish = 0;

        var index = -1;
        foreach (var recorded in frames)
        {
            index++;
            // FrameLayout rewrites regions in place; never hand it the caller's objects.
            var raw = recorded.Select(r => new TranslationRegion { Bounds = r.Bounds, OriginalText = r.OriginalText }).ToList();
            OcrTextHelpers.DropNoise(raw);
            OcrTextHelpers.DropOversized(raw);

            if (raw.Count == 0)
            {
                if (++vanish >= VanishConfirmFrames) shown = null;
            }
            else if (layout.TryReusePreviousFrame(raw, FrameLayout.BlockVanishFrames, out var reused))
            {
                trace?.Invoke(index, $"reuse {reused!.Count}");
                vanish = 0;
                shown = Signature(reused);
            }
            else
            {
                trace?.Invoke(index, "regroup");
                vanish = 0;
                var regions = layout.ApplyGroupingHysteresisOrGroup(raw);
                layout.StabiliseAgainstRecent(regions);
                foreach (var r in regions) r.TranslatedText = r.OriginalText;
                var translated = regions.Where(r => !string.IsNullOrWhiteSpace(r.TranslatedText)).ToList();
                if (translated.Count > 0)
                {
                    layout.RememberRecentRegions(translated);
                    translated = layout.HoldBriefDropouts(translated, FrameLayout.BlockVanishFrames);
                    layout.RememberCompletedFrame(raw, translated);
                    shown = Signature(translated);
                }
            }
            replay.Layouts.Add(shown);
        }
        return replay;
    }

    /// <summary>
    /// How the overlay is cut into blocks: one entry per block, its text
    /// reduced to letters and digits. Merged vs split lines change it;
    /// a pixel of position jitter does not.
    /// </summary>
    public static string Signature(IEnumerable<TranslationRegion> regions) =>
        string.Join(" | ", regions
            .OrderBy(r => Math.Round(r.Bounds.Top / 10)).ThenBy(r => r.Bounds.Left)
            .Select(r => new string((r.TranslatedText ?? r.OriginalText).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())));

    /// <summary>
    /// Flicker: the layout changes and then goes back to one it showed
    /// within the last <paramref name="window"/> frames (A → B → A). A
    /// scene change only moves forward, so it isn't counted. The window is
    /// short on purpose: B lasting a few seconds and then A again is the
    /// player opening a screen and going back (gallery → title), not flicker.
    /// </summary>
    public static int CountFlicker(IReadOnlyList<string?> layouts, int window = 4) =>
        FlickerFrames(layouts, window).Count;

    public static List<int> FlickerFrames(IReadOnlyList<string?> layouts, int window = 4)
    {
        var result = new List<int>();
        string? lastShown = null;
        var lastShownAt = -1;
        for (int i = 0; i < layouts.Count; i++)
        {
            var cur = layouts[i];
            if (cur == null) continue; // hidden: the text left, not a layout change
            if (cur != lastShown && lastShown != null)
            {
                for (int j = lastShownAt - 1; j >= Math.Max(0, i - window); j--)
                {
                    if (layouts[j] == cur) { result.Add(i); break; }
                }
            }
            lastShown = cur;
            lastShownAt = i;
        }
        return result;
    }

    /// <summary>Readable frame-by-frame dump around the flicker, for the test output.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        string? prev = "\0";
        for (int i = 0; i < Layouts.Count; i++)
        {
            if (Layouts[i] == prev) continue;
            sb.AppendLine($"#{i}: {Layouts[i] ?? "(hidden)"}");
            prev = Layouts[i];
        }
        return sb.ToString();
    }
}
