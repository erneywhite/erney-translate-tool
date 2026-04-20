using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Global hotkey registration service using Windows RegisterHotKey API.
/// </summary>
public class HotkeyService : IDisposable
{
    private readonly ILogger _logger;
    private readonly Dictionary<int, HotkeyEntry> _registeredHotkeys = new();
    private HwndSource? _hwndSource;
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>
    /// Event raised when a registered hotkey is pressed.
    /// </summary>
    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// Initialize hotkey service.
    /// </summary>
    public HotkeyService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize with window handle for message processing.
    /// </summary>
    /// <param name="window">WPF window to hook into.</param>
    public void Initialize(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        _logger.Debug("Hotkey service initialized");
    }

    /// <summary>
    /// Register a global hotkey.
    /// </summary>
    /// <param name="modifier">Modifier keys (Ctrl, Alt, Shift, Win).</param>
    /// <param name="key">Virtual key code.</param>
    /// <param name="action">Action to execute when hotkey is pressed.</param>
    /// <returns>Hotkey ID or -1 if registration failed.</returns>
    public int RegisterHotkey(ModifierKeys modifier, Key key, Action action)
    {
        if (_hwndSource == null)
        {
            _logger.Error("Hotkey service not initialized");
            return -1;
        }

        var id = _nextId++;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);

        if (!RegisterHotKey(_hwndSource.Handle, id, (uint)modifier, (uint)virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.Error("Failed to register hotkey {Modifier}+{Key}, error: {Error}", 
                modifier, key, error);
            return -1;
        }

        _registeredHotkeys[id] = new HotkeyEntry
        {
            Id = id,
            Modifier = modifier,
            Key = key,
            Action = action
        };

        _logger.Information("Hotkey registered: {Modifier}+{Key} (ID: {Id})", 
            modifier, key, id);
        return id;
    }

    /// <summary>
    /// Unregister a hotkey.
    /// </summary>
    /// <param name="id">Hotkey ID to unregister.</param>
    public void UnregisterHotkey(int id)
    {
        if (_hwndSource == null || !_registeredHotkeys.ContainsKey(id))
            return;

        if (UnregisterHotKey(_hwndSource.Handle, id))
        {
            _registeredHotkeys.Remove(id);
            _logger.Debug("Hotkey unregistered: {Id}", id);
        }
        else
        {
            _logger.Warning("Failed to unregister hotkey: {Id}", id);
        }
    }

    /// <summary>
    /// Unregister all hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _registeredHotkeys.Keys.ToList())
        {
            UnregisterHotkey(id);
        }
    }

    /// <summary>
    /// Window procedure hook for hotkey messages.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;

        if (msg == WM_HOTKEY)
        {
            var hotkeyId = wParam.ToInt32();
            
            if (_registeredHotkeys.TryGetValue(hotkeyId, out var entry))
            {
                _logger.Debug("Hotkey pressed: {Id}", hotkeyId);
                
                try
                {
                    entry.Action();
                    HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs
                    {
                        HotkeyId = hotkeyId,
                        Modifier = entry.Modifier,
                        Key = entry.Key
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error executing hotkey action");
                }

                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private class HotkeyEntry
    {
        public int Id { get; set; }
        public ModifierKeys Modifier { get; set; }
        public Key Key { get; set; }
        public Action Action { get; set; } = null!;
    }

    /// <summary>
    /// Dispose and unregister all hotkeys.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            UnregisterAll();
            _disposed = true;
        }
    }
}

/// <summary>
/// Event args for hotkey pressed event.
/// </summary>
public class HotkeyPressedEventArgs : EventArgs
{
    public int HotkeyId { get; set; }
    public ModifierKeys Modifier { get; set; }
    public Key Key { get; set; }
}
