using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Neuroset.App.Services;
using Neuroset.App.Localization;
using Neuroset.App.ViewModels;
using Neuroset.Application;
using Neuroset.Infrastructure;
using Serilog;

namespace Neuroset.App;

public partial class App : Microsoft.UI.Xaml.Application, IDisposable
{
    private readonly IHost _host;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _hostStarted;
    private bool _databaseInitialized;
    private bool _schedulerStarted;
    private bool _disposed;

    public static Window Window { get; private set; } = null!;
    // Each application host supplies the operating-system services.
    static partial void ConfigurePlatformServices(IServiceCollection services);
    static partial void ConfigurePlatformHost(IHostBuilder builder);
    static partial void ReportPlatformStartupFailure(Exception exception);
    static partial void CompletePlatformStartup();
    public static void ReportStartupFailure(Exception exception) => ReportPlatformStartupFailure(exception);
    public static void ReportStartupSuccess() => CompletePlatformStartup();
    public static IServiceProvider Services { get; private set; } = null!;
    public static Task Initialization { get; private set; } = Task.CompletedTask;

    public App()
    {
        LocalizationService.Initialize();
        InitializeComponent();
        var hostBuilder = Host.CreateDefaultBuilder();
        ConfigurePlatformHost(hostBuilder);
        _host = hostBuilder
            .UseNeuroset()
            .ConfigureServices(services =>
            {
                ConfigurePlatformServices(services);
                services.AddSingleton<ThemeService>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainViewModel>();
            })
            .Build();
        Services = _host.Services;
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Information("Startup: launch received");
        Initialization = InitializeCoreAsync();

        Log.Information("Startup: creating main window");
        Window = new MainWindow();
        Window.Closed += OnWindowClosed;
        Window.Activate();
        Log.Information("Startup: main window activated");
    }

    public static Task RetryInitializationAsync(CancellationToken cancellationToken = default)
    {
        Initialization = ((App)Current).InitializeCoreAsync(cancellationToken);
        return Initialization;
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!_hostStarted)
            {
                Log.Information("Startup: starting application host");
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
                _hostStarted = true;
                Log.Information("Startup: application host started in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
            }

            if (!_databaseInitialized)
            {
                Log.Information("Startup: initializing database");
                await Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
                _databaseInitialized = true;
                Log.Information("Startup: database initialized in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
            }

            if (!_schedulerStarted)
            {
                Log.Information("Startup: starting scheduler");
                await Services.GetRequiredService<ISchedulerService>().StartAsync(cancellationToken).ConfigureAwait(false);
                _schedulerStarted = true;
                Log.Information("Startup: scheduler started in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
            }

            Log.Information("Startup: core initialization completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Log.Error(ex, "Startup: core initialization failed after {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            try
            {
                await Initialization;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Startup was incomplete when the window closed");
            }
            if (_schedulerStarted)
                await Services.GetRequiredService<ISchedulerService>().StopAsync();
            if (_hostStarted)
                await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Application shutdown failed");
        }
        finally
        {
            Dispose();
            Log.CloseAndFlush();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.Dispose();
        _initializationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled UI exception");
        // Unknown UI exceptions must not be silently swallowed: doing so leaves a
        // half-initialized, apparently frozen window with no actionable error.
        e.Handled = false;
    }
}
