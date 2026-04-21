using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Graphics.Capture;
using Serilog;
using ErneyTranslateTool.Models;

namespace ErneyTranslateTool.Core
{
    public class CaptureService : ICaptureService
    {
        private readonly ILogger _logger;
        private IntPtr _targetWindowHandle;
        private CancellationTokenSource? _captureCts;
        private Task? _captureTask;
        private bool _disposed;
        private RECT _lastWindowRect;

        public event EventHandler<Bitmap>? FrameCaptured;
        public event EventHandler? CaptureStopped;
        public bool IsCapturing { get; private set; }
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
                        await Task.Delay(500, ct);
                        continue;
                    }

                    var bitmap = CaptureWindowGdi(_targetWindowHandle);
                    if (bitmap != null)
                        FrameCaptured?.Invoke(this, bitmap);

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

        private Bitmap? CaptureWindowGdi(IntPtr hWnd)
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
                IntPtr hdcSrc = GetWindowDC(hWnd);

                BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, 0x00CC0020);

                ReleaseDC(hWnd, hdcSrc);
                graphics.ReleaseHdc(hdcDest);

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GDI capture failed");
                return null;
            }
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
