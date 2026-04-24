using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Core
{
    public class CaptureService : IDisposable
    {
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        private readonly ILogger _logger;
        private IntPtr _targetWindowHandle;
        private CancellationTokenSource? _captureCts;
        private Task? _captureTask;
        private bool _disposed;
        private RECT _lastWindowRect;
        private bool _debugFrameSaved;
        private bool _capturePathLogged;

        public event EventHandler<Bitmap>? FrameCaptured;
        public event EventHandler? CaptureStopped;
        /// <summary>Raised when the loop pauses/resumes because the target window was minimised/restored.</summary>
        public event EventHandler<bool>? PauseStateChanged;
        public bool IsCapturing { get; private set; }
        /// <summary>True while the loop is skipping work because the target window is iconic.</summary>
        public bool IsPaused { get; private set; }
        public IntPtr TargetWindowHandle => _targetWindowHandle;

        public CaptureService(ILogger logger)
        {
            _logger = logger;
        }

        public async Task StartCaptureAsync(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                _logger.Error("Invalid window handle");
                return;
            }

            if (IsCapturing)
                await StopCaptureAsync();

            _targetWindowHandle = windowHandle;

            try
            {
                IsCapturing = true;
                _debugFrameSaved = false;
                _capturePathLogged = false;
                _captureCts = new CancellationTokenSource();
                _captureTask = CaptureLoopAsync(_captureCts.Token);
                _logger.Information("Capture started for handle: {Handle}", windowHandle);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to start capture");
                IsCapturing = false;
            }
        }

        public async Task StopCaptureAsync()
        {
            if (!IsCapturing) return;

            _logger.Information("Stopping capture");

            try
            {
                _captureCts?.Cancel();
                if (_captureTask != null)
                {
                    try { await _captureTask; }
                    catch (OperationCanceledException) { }
                }

                IsCapturing = false;
                CaptureStopped?.Invoke(this, EventArgs.Empty);
                _logger.Information("Capture stopped");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error stopping capture");
            }
        }

        private async Task CaptureLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (IsIconic(_targetWindowHandle))
                    {
                        // Edge-trigger the pause event so subscribers can flip
                        // the status text exactly once instead of every 500 ms.
                        if (!IsPaused)
                        {
                            IsPaused = true;
                            PauseStateChanged?.Invoke(this, true);
                        }
                        await Task.Delay(500, ct);
                        continue;
                    }

                    if (IsPaused)
                    {
                        IsPaused = false;
                        PauseStateChanged?.Invoke(this, false);
                    }

                    var bitmap = CaptureWindow(_targetWindowHandle);
                    if (bitmap != null)
                    {
                        SaveDebugFrameOnce(bitmap);
                        FrameCaptured?.Invoke(this, bitmap);
                    }

                    if (GetWindowRect(_targetWindowHandle, out RECT rect) && rect != _lastWindowRect)
                        _lastWindowRect = rect;

                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in capture loop");
                    await Task.Delay(500, ct);
                }
            }
        }

        /// <summary>
        /// Capture a window's pixels. Tries PrintWindow with PW_RENDERFULLCONTENT first
        /// (works for hardware-rendered apps like Chromium browsers and most modern UI),
        /// falls back to GDI BitBlt when PrintWindow can't honor the request.
        /// </summary>
        private Bitmap? CaptureWindow(IntPtr hWnd)
        {
            if (!GetWindowRect(hWnd, out RECT rect))
                return null;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return null;

            try
            {
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                IntPtr hdcDest = graphics.GetHdc();
                bool printOk = false;
                try
                {
                    printOk = PrintWindow(hWnd, hdcDest, PW_RENDERFULLCONTENT);
                    if (!printOk)
                    {
                        // Older / GDI-only windows may need BitBlt instead.
                        IntPtr hdcSrc = GetWindowDC(hWnd);
                        if (hdcSrc != IntPtr.Zero)
                        {
                            BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, 0x00CC0020);
                            ReleaseDC(hWnd, hdcSrc);
                        }
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdcDest);
                }

                if (!_capturePathLogged)
                {
                    _logger.Information("Capture path: {Path} ({Width}x{Height})",
                        printOk ? "PrintWindow(RENDERFULLCONTENT)" : "BitBlt fallback",
                        width, height);
                    _capturePathLogged = true;
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Window capture failed");
                return null;
            }
        }

        /// <summary>Persist the first captured frame so we can eyeball whether the source pixels look real.</summary>
        private void SaveDebugFrameOnce(Bitmap bitmap)
        {
            if (_debugFrameSaved) return;
            try
            {
                var dir = AppContext.BaseDirectory;
                // App.AppDataPath isn't visible from here without a circular ref;
                // use the logs dir which we know exists.
                var logsDir = Path.Combine(dir, "logs");
                Directory.CreateDirectory(logsDir);
                var path = Path.Combine(logsDir, "debug-frame.png");
                bitmap.Save(path, ImageFormat.Png);
                var brightness = SampleAverageBrightness(bitmap);
                _logger.Information("Saved debug frame to {Path}, avg brightness {B:F1}/255", path, brightness);
                if (brightness < 5)
                    _logger.Warning("Debug frame is essentially black — capture is likely failing for this window. " +
                                    "Most modern apps (Chromium browsers, UWP, DirectX games) need WGC or DWM thumbnail; " +
                                    "PrintWindow doesn't always honor RENDERFULLCONTENT.");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not save debug frame");
            }
            _debugFrameSaved = true;
        }

        private static double SampleAverageBrightness(Bitmap bmp)
        {
            // Sample a 32x32 grid of pixels — fast and good enough.
            const int samples = 32;
            double total = 0;
            int n = 0;
            for (int sx = 0; sx < samples; sx++)
            {
                for (int sy = 0; sy < samples; sy++)
                {
                    int x = (int)((sx + 0.5) / samples * bmp.Width);
                    int y = (int)((sy + 0.5) / samples * bmp.Height);
                    var c = bmp.GetPixel(x, y);
                    total += (c.R + c.G + c.B) / 3.0;
                    n++;
                }
            }
            return total / n;
        }

        public bool IsWindowMinimized() => IsIconic(_targetWindowHandle);

        public RECT GetWindowRectangle()
        {
            GetWindowRect(_targetWindowHandle, out RECT rect);
            return rect;
        }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public static bool operator ==(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
            public static bool operator !=(RECT a, RECT b) => !(a == b);
            public override bool Equals(object? obj) => obj is RECT r && this == r;
            public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _captureCts?.Cancel();
            _captureCts?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
