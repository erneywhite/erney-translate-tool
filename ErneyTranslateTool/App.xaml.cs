using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Ocr;
using ErneyTranslateTool.Core.Updates;
using ErneyTranslateTool.Data;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ErneyTranslateTool;

/// <summary>
/// Application entry point and dependency injection container.
/// </summary>
public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public ILogger Logger { get; private set; } = null!;

    /// <summary>
    /// True when launched with <c>--minimized</c> (the autostart entry sets
    /// this). Tells <see cref="MainWindow"/> to start hidden in the tray
    /// and tells the app-startup code to defer modal dialogs (update
    /// notification, what's-new) until the user opens the window.
    /// </summary>
    public bool StartedMinimized { get; private set; }

    public static string AppDataPath { get; } = DetermineAppDataPath();

    /// <summary>
    /// Prefer the install folder so the app behaves portably (settings, cache,
    /// history, downloaded tessdata stay together with the exe). Falls back to
    /// %AppData% when the install folder is read-only (e.g. Program Files).
    /// </summary>
    private static string DetermineAppDataPath()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(exeDir) && CanWriteTo(exeDir))
            return exeDir;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ErneyTranslateTool");
    }

    private static bool CanWriteTo(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Detect autostart-minimised mode early; everything downstream
        // (MainWindow visibility, dialog deferral) reads it.
        StartedMinimized = e.Args != null && Array.Exists(e.Args, a =>
            string.Equals(a, Core.Startup.AutoStartManager.MinimizedFlag, StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(Path.Combine(AppDataPath, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(AppDataPath, "logs", "ett-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 30)
            .WriteTo.Debug()
            .CreateLogger();

        Logger = Log.Logger;

        try
        {
            Logger.Information("Application starting. Version: {Version}",
                Assembly.GetExecutingAssembly().GetName().Version);

            Settings = new AppSettings(AppDataPath, Logger);
            Settings.Load();

            // Apply the persisted UI theme before any window is shown.
            ThemeManager.Apply(Settings.Config.AppTheme);

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // Exit the process when the user closes the main window, so the
            // still-visible OverlayWindow (another WPF Window) doesn't keep
            // the app alive in the background.
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var mainWindow = Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;

            if (StartedMinimized)
            {
                // Show + Hide is the canonical WPF dance to construct a window
                // and trigger Loaded (which builds the tray icon!) without
                // ever rendering it on screen.
                mainWindow.Show();
                mainWindow.Hide();
                Logger.Information("Started minimised — main window hidden, tray only");
            }
            else
            {
                mainWindow.Show();
            }

            // Surface release notes once if the user just upgraded — but if
            // we started minimised, defer until the window is actually opened
            // (the dialog would otherwise be invisible behind a hidden owner).
            _ = ShowWhatsNewIfUpdatedAsync(mainWindow);

            Logger.Information("Application started successfully");
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "Failed to start application");
            MessageBox.Show(
                $"Критическая ошибка при запуске:\n{ex.Message}\n\nПроверьте файл лога для деталей.",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(Settings);
        services.AddSingleton(Logger);

        // Repositories require appDataPath in their constructors.
        services.AddSingleton(sp => new CacheRepository(AppDataPath, sp.GetRequiredService<ILogger>()));
        services.AddSingleton(sp => new HistoryRepository(AppDataPath, sp.GetRequiredService<ILogger>()));
        services.AddSingleton(sp => new TessdataManager(AppDataPath, sp.GetRequiredService<ILogger>()));
        services.AddSingleton(sp => new Data.GlossaryRepository(AppDataPath, sp.GetRequiredService<ILogger>()));
        services.AddSingleton(sp => new Data.GameProfileRepository(AppDataPath, sp.GetRequiredService<ILogger>()));

        // Core services
        services.AddSingleton<CaptureService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<OverlayManager>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<WindowPickerService>();
        services.AddSingleton<TranslationEngine>();
        services.AddSingleton<Core.Glossary.GlossaryApplier>();
        services.AddSingleton<Core.Profiles.ProfileManager>();
        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<UpdateDownloader>();

        // ViewModels
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.SettingsViewModel>();
        services.AddSingleton<ViewModels.HistoryViewModel>();
        services.AddSingleton<ViewModels.GlossaryViewModel>();
        services.AddSingleton<ViewModels.ProfilesViewModel>();

        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error(e.Exception, "Unhandled WPF dispatcher exception");
        MessageBox.Show(
            $"Произошла непредвиденная ошибка:\n{e.Exception.Message}\n\nОшибка записана в лог.",
            "Ошибка",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Logger.Fatal(ex, "Unhandled AppDomain exception");
    }

    /// <summary>
    /// Compare the running assembly version against the last version we
    /// surfaced release notes for. If they differ — and the stored value is
    /// non-empty (i.e. this is an upgrade, not a fresh install) — fetch the
    /// notes from GitHub and pop a one-time "what's new" dialog. Failures are
    /// silent: this is a nice-to-have, not a critical path.
    /// </summary>
    private async System.Threading.Tasks.Task ShowWhatsNewIfUpdatedAsync(Window owner)
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (current == null) return;

            // Assembly Version is always 4-part (e.g. 1.0.2.0), but our git
            // tags and release notes are 3-part (v1.0.2). Normalise so the
            // stored marker and the GitHub tag URL both line up.
            var currentStr = $"{current.Major}.{current.Minor}.{Math.Max(0, current.Build)}";
            var lastSeen = Settings.Config.LastSeenReleaseVersion;

            // Fresh install: nothing to compare to. Record current version
            // silently so the next upgrade triggers the dialog.
            if (string.IsNullOrEmpty(lastSeen))
            {
                Settings.Config.LastSeenReleaseVersion = currentStr;
                Settings.Save();
                return;
            }

            // Same as last time → no upgrade happened.
            if (lastSeen == currentStr) return;

            // Downgrade or weird case → just bring the marker forward without
            // showing anything (the user didn't "just upgrade" in the usual sense).
            if (Version.TryParse(lastSeen, out var lastVer) && lastVer >= current)
            {
                Settings.Config.LastSeenReleaseVersion = currentStr;
                Settings.Save();
                return;
            }

            // Genuine upgrade — fetch notes and show the dialog.
            var displayVersion = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            var checker = Services.GetRequiredService<Core.Updates.UpdateChecker>();
            var notes = await checker.FetchReleaseNotesAsync(displayVersion);

            // Record the new version BEFORE showing the dialog so a crash
            // mid-display doesn't keep nagging on every relaunch.
            Settings.Config.LastSeenReleaseVersion = currentStr;
            Settings.Save();

            // Build the show-action once and either run it now or stash it
            // for the first window-open if we started in tray-only mode.
            Action show = () =>
            {
                var dlg = new Views.Dialogs.WhatsNewDialog(
                    displayVersion,
                    notes ?? string.Empty,
                    $"https://github.com/erneywhite/erney-translate-tool/releases/tag/v{displayVersion}",
                    Logger)
                {
                    Owner = owner
                };
                dlg.ShowDialog();
            };

            if (StartedMinimized && owner is MainWindow mw)
            {
                mw.DeferWhatsNewDialog(show);
            }
            else
            {
                owner.Dispatcher.Invoke(show);
            }
        }
        catch (Exception ex)
        {
            Logger.Information(ex, "What's-new dialog failed (non-fatal)");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Information("Application shutting down");
        if (Services is IDisposable disposable)
            disposable.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
