using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Data;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ErneyTranslateTool;

/// <summary>
/// Application entry point and dependency injection container.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Service provider for dependency injection.
    /// </summary>
    public IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Application settings instance.
    /// </summary>
    public AppSettings Settings { get; private set; } = null!;

    /// <summary>
    /// Logger instance.
    /// </summary>
    public ILogger Logger { get; private set; } = null!;

    /// <summary>
    /// Initialize application services and configure logging.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ensure app data directory exists
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ErneyTranslateTool");
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(Path.Combine(appDataPath, "logs"));

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(appDataPath, "logs", "ett-.log"),
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

            // Load settings
            Settings = new AppSettings(appDataPath, Logger);
            Settings.Load();

            // Configure DI
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            // Set up global exception handler
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

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

    /// <summary>
    /// Configure dependency injection services.
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // Core services
        services.AddSingleton(Settings);
        services.AddSingleton(Logger);
        services.AddSingleton<CaptureService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<OverlayManager>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<WindowPickerService>();

        // Data repositories
        services.AddSingleton<CacheRepository>();
        services.AddSingleton<HistoryRepository>();

        // ViewModels
        services.AddTransient<ViewModels.MainViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.HistoryViewModel>();

        // Main window
        services.AddSingleton<MainWindow>();
    }

    /// <summary>
    /// Handle WPF dispatcher unhandled exceptions.
    /// </summary>
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

    /// <summary>
    /// Handle AppDomain unhandled exceptions.
    /// </summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Logger.Fatal(ex, "Unhandled AppDomain exception");
    }

    /// <summary>
    /// Clean up resources on application exit.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Information("Application shutting down");
        
        // Dispose services
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Log.CloseAndFlush();
        
        base.OnExit(e);
    }
}
