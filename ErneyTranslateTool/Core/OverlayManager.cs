using System;
using System.Runtime.InteropServices;
using System.Windows;
using ErneyTranslateTool.Models;
using Serilog;

namespace ErneyTranslateTool.Core
{
    public class OverlayManager : IOverlayManager
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private OverlayWindow? _overlayWindow;
        private bool _disposed;

        public bool IsVisible => _overlayWindow?.IsVisible ?? false;

        public void ShowTranslation(string text, Rect targetRect)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_overlayWindow == null || !_overlayWindow.IsLoaded)
                {
                    _overlayWindow = new OverlayWindow();
                    _overlayWindow.Show();
                }
                _overlayWindow.SetTranslation(text, targetRect);
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
