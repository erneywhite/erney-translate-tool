using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace ErneyTranslateTool.Core
{
    public class HotkeyService : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;

        private HwndSource? _hwndSource;
        private readonly Dictionary<int, Action> _hotkeys = new();
        private readonly Dictionary<string, int> _hotkeyIds = new();
        private int _nextId = 9000;
        private bool _disposed;

        // Модификаторы Win32
        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;
        public const int MOD_WIN = 0x0008;

        public void Initialize(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
        }

        public bool RegisterHotkey(string id, int modifiers, int vk, Action callback)
        {
            if (_hwndSource == null)
            {
                Log.Warning("HotkeyService не инициализирован");
                return false;
            }

            if (_hotkeyIds.ContainsKey(id))
                UnregisterHotkey(id);

            int hotkeyId = _nextId++;
            if (RegisterHotKey(_hwndSource.Handle, hotkeyId, modifiers, vk))
            {
                _hotkeys[hotkeyId] = callback;
                _hotkeyIds[id] = hotkeyId;
                Log.Debug("Зарегистрирован хоткей {Id}: mod={Mod} vk={Vk}", id, modifiers, vk);
                return true;
            }

            Log.Warning("Не удалось зарегистрировать хоткей {Id}", id);
            return false;
        }

        public void UnregisterHotkey(string id)
        {
            if (_hwndSource == null || !_hotkeyIds.TryGetValue(id, out int hotkeyId)) return;
            UnregisterHotKey(_hwndSource.Handle, hotkeyId);
            _hotkeys.Remove(hotkeyId);
            _hotkeyIds.Remove(id);
        }

        public void UnregisterAll()
        {
            if (_hwndSource == null) return;
            foreach (var id in _hotkeyIds.Values)
                UnregisterHotKey(_hwndSource.Handle, id);
            _hotkeys.Clear();
            _hotkeyIds.Clear();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _hotkeys.TryGetValue(wParam.ToInt32(), out var callback))
            {
                callback();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            UnregisterAll();
            _hwndSource?.RemoveHook(WndProc);
            _disposed = true;
        }
    }
}
