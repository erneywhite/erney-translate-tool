using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using ErneyTranslateTool.Data;
using ErneyTranslateTool.Models;
using ErneyTranslateTool.Views;

namespace ErneyTranslateTool.Core
{
    public class OverlayManager : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private readonly AppSettings _settings;
        private OverlayWindow? _overlayWindow;
        private bool _disposed;

        public OverlayManager(AppSettings settings)
        {
            _settings = settings;
        }

        public bool IsVisible => _overlayWindow?.IsVisible ?? false;

        /// <summary>
        /// Draw a translation label on top of every detected region.
        /// </summary>
        public void ShowRegions(IReadOnlyList<TranslationRegion> regions, Rect targetRect)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                EnsureWindow();
                _overlayWindow!.SetRegions(regions, targetRect, _settings.Config);
            });
        }

        public void Hide()
        {
            Application.Current.Dispatcher.Invoke(() =>
                _overlayWindow?.Hide());
        }

        public void UpdatePosition(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out RECT rect)) return;
            Application.Current.Dispatcher.Invoke(() =>
                _overlayWindow?.UpdateBounds(
                    new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
        }

        private void EnsureWindow()
        {
            if (_overlayWindow == null || !_overlayWindow.IsLoaded)
            {
                _overlayWindow = new OverlayWindow();
                _overlayWindow.Show();
            }
            else if (!_overlayWindow.IsVisible)
            {
                _overlayWindow.Show();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                _overlayWindow?.Close();
                _overlayWindow = null;
            });
            _disposed = true;
        }
    }
}
