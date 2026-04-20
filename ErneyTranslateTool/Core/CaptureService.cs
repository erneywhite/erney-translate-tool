using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Screen capture service using Windows Graphics Capture API.
/// </summary>
public class CaptureService : IDisposable
{
    private readonly ILogger _logger;
    private GraphicsCaptureItem? _captureItem;
    private GraphicsCaptureSession? _captureSession;
    private Direct3D11CaptureFramePool? _framePool;
    private ID3D11Device? _d3dDevice;
    private IntPtr _targetWindowHandle;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private bool _disposed;
    private RECT _lastWindowRect;

    /// <summary>
    /// Event raised when a new frame is captured.
    /// </summary>
    public event EventHandler<byte[]>? FrameCaptured;

    /// <summary>
    /// Event raised when capture stops.
    /// </summary>
    public event EventHandler? CaptureStopped;

    /// <summary>
    /// Whether capture is currently active.
    /// </summary>
    public bool IsCapturing { get; private set; }

    /// <summary>
    /// Target window handle being captured.
    /// </summary>
    public IntPtr TargetWindowHandle => _targetWindowHandle;

    /// <summary>
    /// Initialize capture service.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public CaptureService(ILogger logger)
    {
        _logger = logger;
        InitializeD3D();
    }

    /// <summary>
    /// Initialize Direct3D device for capture.
    /// </summary>
    private void InitializeD3D()
    {
        try
        {
            _d3dDevice = Direct3D11Helper.CreateDevice();
            _logger.Debug("Direct3D device initialized");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize Direct3D device");
            throw;
        }
    }

    /// <summary>
    /// Start capturing a specific window.
    /// </summary>
    /// <param name="windowHandle">Window handle to capture.</param>
    /// <returns>True if capture started successfully.</returns>
    public async Task<bool> StartCaptureAsync(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            _logger.Error("Invalid window handle");
            return false;
        }

        if (IsCapturing)
        {
            await StopCaptureAsync();
        }

        _targetWindowHandle = windowHandle;

        try
        {
            // Create capture item from window handle
            _captureItem = GraphicsCaptureItem.TryCreateFromWindowHandle(windowHandle);
            if (_captureItem == null)
            {
                _logger.Error("Failed to create capture item for window handle: {Handle}", windowHandle);
                return false;
            }

            _logger.Information("Starting capture for window: {Title}, Size: {Width}x{Height}",
                GetWindowTitle(windowHandle),
                _captureItem.Size.Width,
                _captureItem.Size.Height);

            // Create frame pool
            _framePool = Direct3D11CaptureFramePool.Create(
                _d3dDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _captureItem.Size);

            _framePool.FrameArrived += OnFrameArrived;

            // Create capture session
            _captureSession = _framePool.CreateCaptureSession(_captureItem);
            _captureSession.IsCursorCaptureEnabled = false;
            _captureSession.IsBorderRequired = false;

            // Start capture
            _captureSession.StartCapture();
            IsCapturing = true;

            // Start window position tracking
            _captureCts = new CancellationTokenSource();
            _captureTask = TrackWindowPositionAsync(_captureCts.Token);

            _logger.Information("Capture started successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start capture");
            await StopCaptureAsync();
            return false;
        }
    }

    /// <summary>
    /// Stop capture.
    /// </summary>
    public async Task StopCaptureAsync()
    {
        if (!IsCapturing) return;

        _logger.Information("Stopping capture");

        try
        {
            // Stop position tracking
            _captureCts?.Cancel();
            if (_captureTask != null)
            {
                try
                {
                    await _captureTask;
                }
                catch (OperationCanceledException) { }
            }

            // Stop capture session
            _captureSession?.Dispose();
            _captureSession = null;

            // Dispose frame pool
            _framePool?.Dispose();
            _framePool = null;

            _captureItem = null;
            IsCapturing = false;

            CaptureStopped?.Invoke(this, EventArgs.Empty);
            _logger.Information("Capture stopped");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while stopping capture");
        }
    }

    /// <summary>
    /// Handle new frame arrival.
    /// </summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!IsCapturing) return;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            // Get frame data
            using var texture = Direct3D11Helper.CreateDirect3DDeviceFromSurface(frame.Surface);
            var frameData = ExtractFrameData(frame);

            if (frameData != null)
            {
                FrameCaptured?.Invoke(this, frameData);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing captured frame");
        }
    }

    /// <summary>
    /// Extract byte array from frame.
    /// </summary>
    private byte[]? ExtractFrameData(Direct3D11CaptureFrame frame)
    {
        try
        {
            var width = (int)frame.ContentSize.Width;
            var height = (int)frame.ContentSize.Height;

            if (width <= 0 || height <= 0) return null;

            // For now, we'll use a simpler approach with screen capture
            // In production, you'd use Direct3D to read the texture
            // This is a simplified version that captures via GDI
            return CaptureWindowGdi(_targetWindowHandle);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to extract frame data");
            return null;
        }
    }

    /// <summary>
    /// Capture window using GDI (fallback/simplified method).
    /// </summary>
    private byte[]? CaptureWindowGdi(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var rect))
            return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            return null;

        try
        {
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);

            using var hdcDest = graphics.GetHdc();
            using var hdcSrc = GetWindowDC(hWnd);
            
            BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, TernaryRasterOperations.SRCCOPY);
            
            graphics.ReleaseHdc(hdcDest);
            ReleaseDC(hWnd, hdcSrc);

            using var ms = new System.IO.MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GDI capture failed");
            return null;
        }
    }

    /// <summary>
    /// Track window position changes.
    /// </summary>
    private async Task TrackWindowPositionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (GetWindowRect(_targetWindowHandle, out var rect))
                {
                    if (rect != _lastWindowRect)
                    {
                        _lastWindowRect = rect;
                        // Notify overlay manager of position change
                        // This would trigger overlay repositioning
                    }
                }
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error tracking window position");
                await Task.Delay(500, ct);
            }
        }
    }

    /// <summary>
    /// Get window title from handle.
    /// </summary>
    private static string GetWindowTitle(IntPtr hWnd)
    {
        var title = new string('\0', 256);
        GetWindowText(hWnd, title, 256);
        return title.TrimEnd('\0');
    }

    /// <summary>
    /// Check if target window is minimized.
    /// </summary>
    public bool IsWindowMinimized()
    {
        return IsIconic(_targetWindowHandle);
    }

    /// <summary>
    /// Get current window rectangle.
    /// </summary>
    public RECT GetWindowRectangle()
    {
        GetWindowRect(_targetWindowHandle, out var rect);
        return rect;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, string lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, 
        int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, 
        TernaryRasterOperations dwRop);

    [Flags]
    private enum TernaryRasterOperations : uint
    {
        SRCCOPY = 0x00CC0020
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public static bool operator ==(RECT a, RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && 
                   a.Right == b.Right && a.Bottom == b.Bottom;
        }

        public static bool operator !=(RECT a, RECT b)
        {
            return !(a == b);
        }

        public override bool Equals(object? obj)
        {
            if (obj is RECT rect)
                return this == rect;
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Left, Top, Right, Bottom);
        }
    }

    /// <summary>
    /// Dispose capture resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _captureCts?.Cancel();
            _captureSession?.Dispose();
            _framePool?.Dispose();
            _captureCts?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
