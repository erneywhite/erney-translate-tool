using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Manages the transparent overlay window for displaying translations.
/// </summary>
public class OverlayManager
{
    private readonly ILogger _logger;
    private OverlayWindow? _overlayWindow;
    private IntPtr _targetWindowHandle;
    private bool _disposed;

    /// <summary>
    /// Whether overlay is currently visible.
    /// </summary>
    public bool IsVisible => _overlayWindow?.IsVisible == true;

    /// <summary>
    /// Initialize overlay manager.
    /// </summary>
    public OverlayManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Show overlay window.
    /// </summary>
    /// <param name="targetWindowHandle">Handle of window to overlay.</param>
    public void Show(IntPtr targetWindowHandle)
    {
        if (_overlayWindow == null)
        {
            _overlayWindow = new OverlayWindow();
            _logger.Debug("Overlay window created");
        }

        _targetWindowHandle = targetWindowHandle;
        
        // Position overlay over target window
        Reposition();
        
        _overlayWindow.Show();
        _logger.Information("Overlay shown");
    }

    /// <summary>
    /// Hide overlay window.
    /// </summary>
    public void Hide()
    {
        _overlayWindow?.Hide();
        _logger.Information("Overlay hidden");
    }

    /// <summary>
    /// Close overlay window.
    /// </summary>
    public void Close()
    {
        _overlayWindow?.Close();
        _overlayWindow = null;
        _logger.Information("Overlay closed");
    }

    /// <summary>
    /// Reposition overlay to match target window.
    /// </summary>
    public void Reposition()
    {
        if (_overlayWindow == null || _targetWindowHandle == IntPtr.Zero)
            return;

        if (CaptureService.GetWindowRect(_targetWindowHandle, out var rect))
        {
            _overlayWindow.Left = rect.Left;
            _overlayWindow.Top = rect.Top;
            _overlayWindow.Width = rect.Width;
            _overlayWindow.Height = rect.Height;
        }
    }

    /// <summary>
    /// Update displayed translation regions.
    /// </summary>
    /// <param name="regions">List of regions to display.</param>
    public void UpdateRegions(List<TranslationRegion> regions)
    {
        if (_overlayWindow == null)
            return;

        _overlayWindow.UpdateRegions(regions);
    }

    /// <summary>
    /// Set overlay visual settings.
    /// </summary>
    public void SetVisualSettings(string fontFamily, double fontSize, 
        string fontSizeMode, double opacity, string backgroundColor, string textColor)
    {
        if (_overlayWindow == null)
            return;

        _overlayWindow.SetVisualSettings(
            fontFamily, fontSize, fontSizeMode, opacity, backgroundColor, textColor);
    }

    /// <summary>
    /// Set click-through mode.
    /// </summary>
    /// <param name="enabled">Whether to enable click-through.</param>
    public void SetClickThrough(bool enabled)
    {
        if (_overlayWindow == null)
            return;

        var hwnd = new WindowInteropHelper(_overlayWindow).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        
        if (enabled)
        {
            exStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        }
        else
        {
            exStyle &= ~(WS_EX_TRANSPARENT | WS_EX_LAYERED);
        }

        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        _logger.Debug("Overlay click-through: {Enabled}", enabled);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    /// <summary>
    /// Dispose overlay resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _disposed = true;
        }
    }
}

/// <summary>
/// P/Invoke helper for capture service.
/// </summary>
public static class CaptureService
{
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
