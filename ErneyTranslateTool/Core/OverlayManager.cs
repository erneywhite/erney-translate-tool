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
        private readonly AppSettings _settings;
        private OverlayWindow? _overlayWindow;
        private bool _disposed;

        public OverlayManager(AppSettings settings)
        {
            _settings = settings;
        }

        public bool IsVisible => _overlayWindow?.IsVisible ?? false;

        /// <summary>
        /// v1.0.31: the user hid the overlay (button / hotkey). Every render
        /// path funnels through <see cref="ShowRegions"/>, so the gate here
        /// keeps it hidden until the user shows it again — before, the next
        /// frame with new text silently brought it back.
        /// </summary>
        public bool HiddenByUser { get; set; }

        /// <summary>The translated window; the overlay is kept just above it (see OverlayWindow.AttachTo).</summary>
        public IntPtr TargetWindow { get; set; }

        /// <summary>
        /// Draw a translation label on top of every detected region.
        /// <paramref name="isPartial"/> marks an in-progress update (streaming
        /// or per-region network translation): <paramref name="regions"/> is
        /// only what's been translated so far, so labels missing from it must
        /// not be treated as gone.
        /// </summary>
        /// <param name="stillWanted">Re-checked on the UI thread right before
        /// showing. The pipeline calls this from a worker thread; by the time
        /// the dispatcher runs the lambda the user may have hidden the
        /// overlay, paused or stopped — a check done before Invoke would
        /// bring the overlay back over a hidden/paused/stopped engine.</param>
        public void ShowRegions(IReadOnlyList<TranslationRegion> regions, Rect targetRect, bool isPartial = false,
            Func<bool>? stillWanted = null)
        {
            if (HiddenByUser) return;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (HiddenByUser || (stillWanted != null && !stillWanted())) return;
                EnsureWindow();
                _overlayWindow!.SetRegions(regions, targetRect, _settings.Config, isPartial);
            });
        }

        public void Hide()
        {
            Application.Current.Dispatcher.Invoke(() =>
                _overlayWindow?.Hide());
        }

        public void UpdatePosition(IntPtr windowHandle)
        {
            if (!WindowGeometry.TryGetClientRect(windowHandle, out var rect)) return;
            Application.Current.Dispatcher.Invoke(() => _overlayWindow?.UpdateBounds(rect));
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
            _overlayWindow.AttachTo(TargetWindow);
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
