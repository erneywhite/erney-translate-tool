using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Service for enumerating and selecting windows for capture.
/// </summary>
public class WindowPickerService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initialize window picker service.
    /// </summary>
    public WindowPickerService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Process names that have no business being in the "pick a window
    /// to translate" list under normal use — Windows shell hosts, the
    /// app's own window, common messengers/VPNs/peripheral driver UIs.
    /// Hidden by default; the user can opt in to seeing them via the
    /// "Show hidden apps" checkbox if their game happened to land in
    /// this list (or just to translate Telegram for fun).
    ///
    /// <para>Browsers are deliberately NOT here — translating web pages
    /// is a perfectly normal use case.</para>
    /// </summary>
    private static readonly HashSet<string> FilteredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Own app — translating our own UI is pointless and clutters the list.
        "ErneyTranslateTool",

        // Windows shell + system UI hosts
        "explorer",
        "ApplicationFrameHost",
        "ShellExperienceHost",
        "SearchHost",
        "SearchUI",
        "TextInputHost",
        "LockApp",
        "StartMenuExperienceHost",
        "SystemSettings",
        "Cortana",
        "Widgets",
        "WidgetService",

        // Messengers / VPNs / driver-suite trayware that have UI windows
        // but rarely contain text someone would translate.
        "Telegram",
        "AmneziaVPN",
        "OpenVPN",
        "OpenVPNConnect",
        "NordVPN",
        "ExpressVPN",
        "ProtonVPN",
        "WireGuard",
        "CyberGhost",
        "TunnelBear",
        "RzMonitorForegroundWindow",
        "RazerCentral",
        "RazerSynapse",
        "Razer.Synapse",
        "LCore",
        "lghub",
        "LogiOptionsPlus",
        "GHUB",

        // Generic system / dev tools
        "TaskMgr",
        "regedit",
        "mmc",
        "cmd",
        "powershell",
        "WindowsTerminal",
        "conhost",
    };

    /// <summary>
    /// Get list of visible top-level windows.
    /// </summary>
    /// <param name="includeFiltered">When true, system / utility windows
    /// (taskbar shells, messengers, VPN clients, etc.) that are normally
    /// hidden are included in the result. Default false matches the
    /// "Show hidden apps" checkbox being off in the picker UI.</param>
    /// <returns>List of window information.</returns>
    public List<WindowInfo> GetVisibleWindows(bool includeFiltered = false)
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, lParam) =>
        {
            // Skip invisible windows
            if (!IsWindowVisible(hWnd))
                return true;

            // Skip windows without title
            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            // Skip certain system windows
            if (IsSystemWindow(hWnd))
                return true;

            // Get process info
            var processId = GetWindowProcessId(hWnd);
            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)processId);
            }
            catch
            {
                // Process may have exited
            }

            // v1.0.28: hide noise-tier processes from the picker by default.
            // The "Show hidden apps" checkbox in the UI maps to
            // includeFiltered=true so the user can still find their game
            // if it happens to share a name with something in the list.
            var processName = process?.ProcessName ?? "Unknown";
            if (!includeFiltered && FilteredProcessNames.Contains(processName))
                return true;

            windows.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ProcessName = processName,
                ProcessId = processId,
                IsFullScreen = IsWindowFullScreen(hWnd)
            });

            return true;
        }, IntPtr.Zero);

        _logger.Debug("Found {Count} visible windows (includeFiltered={Inc})",
            windows.Count, includeFiltered);
        return windows;
    }

    /// <summary>
    /// Get window title.
    /// </summary>
    private static string GetWindowTitle(IntPtr hWnd)
    {
        var capacity = 256;
        var builder = new StringBuilder(capacity);
        GetWindowText(hWnd, builder, capacity);
        return builder.ToString();
    }

    /// <summary>
    /// Check if window is a system window to skip.
    /// </summary>
    private static bool IsSystemWindow(IntPtr hWnd)
    {
        var className = GetWindowClassName(hWnd);
        
        // Skip common system windows
        var systemClasses = new[]
        {
            "Shell_TrayWnd",      // Taskbar
            "Progman",            // Desktop
            "WorkerW",            // Desktop
            "Windows.UI.Core.CoreWindow", // UWP system windows
            "ApplicationFrameWindow" // UWP apps (handled separately)
        };

        return systemClasses.Any(c => className.Contains(c));
    }

    /// <summary>
    /// Get window class name.
    /// </summary>
    private static string GetWindowClassName(IntPtr hWnd)
    {
        var className = new string('\0', 256);
        GetClassName(hWnd, className, 256);
        return className.TrimEnd('\0');
    }

    /// <summary>
    /// Get process ID owning the window.
    /// </summary>
    private static uint GetWindowProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var processId);
        return processId;
    }

    /// <summary>
    /// Check if window appears to be fullscreen.
    /// </summary>
    private static bool IsWindowFullScreen(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var rect))
            return false;

        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);

        return rect.Left <= 0 && rect.Top <= 0 &&
               rect.Right >= screenWidth && rect.Bottom >= screenHeight;
    }

    /// <summary>
    /// Get window rectangle in screen coordinates.
    /// </summary>
    public bool GetWindowRectangle(IntPtr hWnd, out System.Windows.Rect rect)
    {
        rect = new System.Windows.Rect();
        
        if (!GetWindowRect(hWnd, out var nativeRect))
            return false;

        rect = new System.Windows.Rect(
            nativeRect.Left,
            nativeRect.Top,
            nativeRect.Width,
            nativeRect.Height);
        
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, string lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}

/// <summary>
/// Information about a window.
/// </summary>
public class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public uint ProcessId { get; set; }
    public bool IsFullScreen { get; set; }

    public override string ToString() => $"{Title} ({ProcessName})";
}
