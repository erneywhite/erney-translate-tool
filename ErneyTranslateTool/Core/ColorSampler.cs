using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using ErneyTranslateTool.Models;
using MediaColor = System.Windows.Media.Color;

namespace ErneyTranslateTool.Core;

/// <summary>
/// v1.0.35 "replace text" overlay: reads, from the captured frame, the
/// colour behind each text block and the colour of its letters, so the
/// translation can be drawn over the original like Google Translate's
/// camera mode — the box takes the background's colour instead of a fixed
/// plate colour.
/// </summary>
public static class ColorSampler
{
    /// <summary>Fill SampledBackground / SampledText / SourceLineHeight of every block.</summary>
    /// <param name="raw">OCR lines before grouping: their heights give the source line height of each block.</param>
    public static void Sample(Bitmap frame, IEnumerable<TranslationRegion> blocks, IReadOnlyList<TranslationRegion> raw)
    {
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[data.Stride / 4 * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var image = new Pixels(pixels, data.Stride / 4, frame.Width, frame.Height);
            foreach (var b in blocks)
            {
                var (bg, text) = SampleBlock(image, b.Bounds);
                b.SampledBackground = bg;
                b.SampledText = text;
                b.SourceLineHeight = LineHeight(b, raw);
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }

    public readonly record struct Pixels(int[] Argb, int Stride, int Width, int Height)
    {
        public int At(int x, int y) => Argb[y * Stride + x];
    }

    /// <summary>
    /// Background: the most common colour (16 levels per channel) in a band
    /// 2–6 px around the box, averaged within that bucket. Text: the mean
    /// of the pixels inside the box that differ most from the background;
    /// null when nothing stands out (then the overlay picks black or white).
    /// </summary>
    public static (MediaColor? Background, MediaColor? Text) SampleBlock(Pixels img, System.Windows.Rect box)
    {
        int l = (int)Math.Floor(box.Left), t = (int)Math.Floor(box.Top);
        int r = (int)Math.Ceiling(box.Right), btm = (int)Math.Ceiling(box.Bottom);
        if (r <= 0 || btm <= 0 || l >= img.Width || t >= img.Height) return (null, null);

        var buckets = new Dictionary<int, (long R, long G, long B, int N)>();
        void AddBand(int x, int y)
        {
            if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return;
            var c = img.At(x, y);
            int cr = (c >> 16) & 255, cg = (c >> 8) & 255, cb = c & 255;
            var key = (cr >> 4) << 8 | (cg >> 4) << 4 | (cb >> 4);
            buckets.TryGetValue(key, out var s);
            buckets[key] = (s.R + cr, s.G + cg, s.B + cb, s.N + 1);
        }
        for (int d = 2; d <= 6; d += 2)
        {
            for (int x = l - d; x <= r + d; x += 2) { AddBand(x, t - d); AddBand(x, btm + d); }
            for (int y = t - d; y <= btm + d; y += 2) { AddBand(l - d, y); AddBand(r + d, y); }
        }
        if (buckets.Count == 0) return (null, null);
        var top = buckets.Values.MaxBy(v => v.N);
        var bg = MediaColor.FromRgb((byte)(top.R / top.N), (byte)(top.G / top.N), (byte)(top.B / top.N));

        // Letters: inside the box, every other pixel.
        var inside = new List<(int D, int C)>();
        int maxD = 0;
        for (int y = Math.Max(0, t); y < Math.Min(img.Height, btm); y += 2)
        for (int x = Math.Max(0, l); x < Math.Min(img.Width, r); x += 2)
        {
            var c = img.At(x, y);
            var d = Math.Abs(((c >> 16) & 255) - bg.R) + Math.Abs(((c >> 8) & 255) - bg.G) + Math.Abs((c & 255) - bg.B);
            inside.Add((d, c));
            if (d > maxD) maxD = d;
        }
        if (maxD < 90) return (bg, null);
        long sr = 0, sg = 0, sb = 0; int n = 0;
        foreach (var (d, c) in inside)
        {
            if (d < maxD * 0.6) continue;
            sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; n++;
        }
        return (bg, MediaColor.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n)));
    }

    /// <summary>Median height of the OCR lines this block was built from.</summary>
    private static double LineHeight(TranslationRegion block, IReadOnlyList<TranslationRegion> raw)
    {
        var heights = raw.Where(r => block.Bounds.Contains(new System.Windows.Point(
                                         r.Bounds.Left + r.Bounds.Width / 2, r.Bounds.Top + r.Bounds.Height / 2)))
                         .Select(r => r.Bounds.Height).OrderBy(h => h).ToList();
        return heights.Count > 0 ? heights[heights.Count / 2] : block.Bounds.Height;
    }
}
