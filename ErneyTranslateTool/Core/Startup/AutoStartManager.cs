using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Serilog;

namespace ErneyTranslateTool.Core.Startup;

/// <summary>
/// Toggles "run at Windows logon" by writing/removing a value under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Per-user (no UAC).
/// The exe is launched with <c>--minimized</c> so the user lands straight
/// in the tray instead of having a window pop up at every login.
/// </summary>
public static class AutoStartManager
{
    /// <summary>Registry value name. Stays stable across versions so toggling on/off doesn't leave orphans.</summary>
    public const string AppRegistryName = "ErneyTranslateTool";

    /// <summary>Command-line flag we look for in <c>App.OnStartup</c> to skip showing the main window.</summary>
    public const string MinimizedFlag = "--minimized";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Returns true if the registry already has us in the per-user Run key.
    /// We read the registry every call rather than caching — keeps the
    /// settings UI a single source of truth even if another tool clears it.
    /// </summary>
    public static bool IsEnabled(ILogger? logger = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppRegistryName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex)
        {
            logger?.Information(ex, "AutoStart: read failed");
            return false;
        }
    }

    /// <summary>Add (or refresh) the autorun entry pointing at the current exe.</summary>
    public static void Enable(ILogger? logger = null)
    {
        try
        {
            var exe = GetCurrentExePath();
            if (string.IsNullOrEmpty(exe))
            {
                logger?.Warning("AutoStart: cannot determine current exe path; aborting");
                return;
            }

            // Quote the exe in case the install path has a space (it does
            // for default installs under "Program Files" — and for
            // "AppData\Local\Programs\ErneyTranslateTool" the join is fine
            // but quoting costs nothing).
            var command = $"\"{exe}\" {MinimizedFlag}";

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(AppRegistryName, command, RegistryValueKind.String);
            logger?.Information("AutoStart: enabled -> {Command}", command);
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "AutoStart: enable failed");
        }
    }

    public static void Disable(ILogger? logger = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(AppRegistryName) != null)
            {
                key.DeleteValue(AppRegistryName, throwOnMissingValue: false);
                logger?.Information("AutoStart: disabled");
            }
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "AutoStart: disable failed");
        }
    }

    /// <summary>
    /// Best-effort lookup of the running exe's path. Falls back to the
    /// executing assembly when the process module isn't available (e.g.
    /// inside the WPF designer).
    /// </summary>
    private static string? GetCurrentExePath()
    {
        try
        {
            var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
            if (!string.IsNullOrEmpty(module?.FileName)) return module.FileName;
        }
        catch { }

        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var loc = asm.Location;
        // For self-contained .NET 8, Assembly.Location can be empty; in that
        // case AppContext.BaseDirectory + filename derived from assembly name
        // is the safest fallback.
        if (string.IsNullOrEmpty(loc))
            return Path.Combine(AppContext.BaseDirectory, asm.GetName().Name + ".exe");
        return loc;
    }
}
