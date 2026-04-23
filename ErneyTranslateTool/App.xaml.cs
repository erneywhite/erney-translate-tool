using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ErneyTranslateTool.Core;
using ErneyTranslateTool.Core.Ocr;
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

    public static string AppDataPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ErneyTranslateTool");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(Path.Combine(AppDataPath, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
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

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

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

        // Core services
        services.AddSingleton<CaptureService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<OverlayManager>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<WindowPickerService>();
        services.AddSingleton<TranslationEngine>();

        // ViewModels
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.SettingsViewModel>();
        services.AddSingleton<ViewModels.HistoryViewModel>();

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

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Information("Application shutting down");
        if (Services is IDisposable disposable)
            disposable.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
