using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Lidget.App.Services;
using Lidget.App.Shell;
using Lidget.App.ViewModels;
using Lidget.App.Views;
using Lidget.Core;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Lidget.Core.Refresh;
using Lidget.Core.StatusLine;
using Lidget.Core.Storage;
using Lidget.Providers;
using Lidget.Providers.Claude;
using Lidget.Providers.Codex;
using Microsoft.Extensions.Logging;
using Serilog;
using Wpf.Ui.Appearance;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Lidget.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private ILoggerFactory? _loggerFactory;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private HttpClient? _http;
    private SettingsStore? _settings;
    private ClaudeStatusLineSource? _statusLineSource;
    private IUsageProvider[] _providers = [];
    private RefreshScheduler? _scheduler;
    private UsageViewModel? _viewModel;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private TaskbarHost? _taskbarHost;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, $@"Local\{AppInfo.PackageId}.Instance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        Directory.CreateDirectory(AppInfo.DataDirectory);
        _loggerFactory = CreateLoggerFactory();
        _logger = _loggerFactory.CreateLogger("Lidget");
        _logger.LogInformation("Starting {App} {Version}", AppInfo.Name, typeof(App).Assembly.GetName().Version);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ApplicationThemeManager.ApplySystemTheme();
        TaskbarTheme.Start();

        var time = TimeProvider.System;
        _settings = SettingsStore.CreateDefault();
        _http = LidgetHttp.Create();

        _statusLineSource = new ClaudeStatusLineSource(AppInfo.DataDirectory, time);
        _statusLineSource.StartWatching();
        var claude = new ClaudeProvider(
            new ClaudeCredentialStore(_http, time, _loggerFactory.CreateLogger<ClaudeCredentialStore>()),
            new ClaudeUsageClient(_http, time),
            time,
            _loggerFactory.CreateLogger<ClaudeProvider>(),
            _statusLineSource,
            () => _settings.Current.ClaudeStatusLineFeed);
        _providers = [claude, CodexProvider.CreateDefault(time, _loggerFactory)];

        _scheduler = new RefreshScheduler(_providers, () => _settings.Current, time, _loggerFactory.CreateLogger<RefreshScheduler>());
        _viewModel = new UsageViewModel(_providers, _scheduler, _settings, time);
        _viewModel.SettingsRequested += (_, _) => ShowSettings();
        _viewModel.QuitRequested += (_, _) => Quit();

        _flyout = new FlyoutWindow(_viewModel);

        _taskbarHost = new TaskbarHost(CreateWidget, _logger, pill => pill.Clicked += (_, _) => ToggleFlyout())
        {
            TrayOffset = _settings.Current.WidgetTrayOffset,
        };
        _viewModel.WidgetChanged += (_, _) => _taskbarHost.UpdatePosition();
        _settings.Changed += (_, s) => Dispatcher.BeginInvoke(() => OnSettingsChanged(s));

        SyncStartupRegistration(_settings.Current.StartWithWindows, onlyIfChanged: true);

        _taskbarHost.Start();
        _scheduler.Start();
    }

    private static ILoggerFactory CreateLoggerFactory()
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(Environment.GetEnvironmentVariable("LIDGET_DEBUG_LOG") == "1"
                ? Serilog.Events.LogEventLevel.Debug
                : Serilog.Events.LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(AppInfo.DataDirectory, "logs", "lidget-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        return LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddSerilog(serilog, dispose: true));
    }

    private FrameworkElement CreateWidget()
    {
        var widget = new WidgetView(_viewModel!);
        widget.Clicked += (_, _) => ToggleFlyout();
        return widget;
    }

    private void ToggleFlyout()
    {
        if (_taskbarHost?.WidgetBounds is { } anchor)
        {
            _flyout?.Toggle(anchor);
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var wrapperPath = Path.Combine(AppContext.BaseDirectory, StatusLineFiles.WrapperFileName);
        var viewModel = new SettingsViewModel(
            _settings!,
            ClaudeStatusLineInstaller.CreateDefault(AppInfo.DataDirectory),
            wrapperPath,
            _logger);
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private LidgetSettings? _appliedSettings;

    private void OnSettingsChanged(LidgetSettings settings)
    {
        if (_taskbarHost is not null && _taskbarHost.TrayOffset != settings.WidgetTrayOffset)
        {
            _taskbarHost.TrayOffset = settings.WidgetTrayOffset;
            _taskbarHost.UpdatePosition();
        }

        if (_appliedSettings?.StartWithWindows != settings.StartWithWindows)
        {
            SyncStartupRegistration(settings.StartWithWindows, onlyIfChanged: false);
        }

        _appliedSettings = settings;
    }

    /// <summary>Writes the <c>Run</c> registry value. At startup, debug builds do not register themselves.</summary>
    private void SyncStartupRegistration(bool enabled, bool onlyIfChanged)
    {
        _appliedSettings ??= _settings?.Current;
#if DEBUG
        if (onlyIfChanged)
        {
            return;
        }
#endif
        if (Environment.ProcessPath is not { } path || (onlyIfChanged && StartupRegistration.IsRegistered() == enabled))
        {
            return;
        }

        try
        {
            StartupRegistration.Apply(enabled, path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not change the startup registration");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled exception on the UI thread");
    }

    private async void Quit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _logger.LogInformation("Quitting");
        _taskbarHost?.Dispose();
        _settingsWindow?.Close();
        _flyout?.CloseForExit();
        _viewModel?.Dispose();

        try
        {
            if (_scheduler is not null)
            {
                await _scheduler.DisposeAsync();
            }

            foreach (var provider in _providers)
            {
                await provider.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping providers");
        }

        _statusLineSource?.Dispose();
        _http?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskbarHost?.Dispose();
        _loggerFactory?.Dispose();
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
