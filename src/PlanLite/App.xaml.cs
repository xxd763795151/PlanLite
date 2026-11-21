using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Paths;
using PlanLite.Core.Services;
using PlanLite.Services;
using PlanLite.ViewModels;
using PlanLite.Views;

namespace PlanLite;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private CrashLogger? _crashLogger;
    private SettingsStore? _settingsStore;
    private AppSettings? _settings;
    private ThemeService? _themeService;
    private GlobalHotkeyService? _hotkeyService;
    private MidnightRefreshService? _midnightService;
    private TrayService? _trayService;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private MiniWidgetWindow? _miniWidgetWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.ResolveDefault();
        _crashLogger = new CrashLogger(paths.LogsDirectory);
        HookUnhandledExceptions();

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquireOrNotifyExisting())
        {
            Shutdown(0);
            return;
        }

        paths.EnsureDirectories();

        var database = new SqliteDatabase(paths.DatabasePath);
        database.Initialize();

        var repository = new PlanRepository(database);
        var reportService = new ReportService();
        var backupService = new BackupService(paths, database);
        _settingsStore = new SettingsStore(paths.SettingsPath);
        _settings = _settingsStore.Load();
        if (!System.IO.File.Exists(paths.SettingsPath))
        {
            _settingsStore.Save(_settings);
        }

        var startupService = new StartupService();
        _themeService = new ThemeService();
        _hotkeyService = new GlobalHotkeyService();

        _mainViewModel = new MainViewModel(
            paths,
            _settings,
            repository,
            _settingsStore,
            reportService,
            backupService,
            startupService,
            _themeService,
            _hotkeyService,
            PickSaveFilePath,
            PickOpenFilePath,
            SetMiniWidgetVisible);

        _mainWindow = new MainWindow(_mainViewModel, _themeService);
        MainWindow = _mainWindow;

        // Ensure hidden window HWND exists immediately so Win32 window styling is ready before first show.
        _ = new WindowInteropHelper(_mainWindow).EnsureHandle();

        _themeService.Initialize(_mainWindow, _settings.ThemeMode);

        var (hotkeyOk, hotkeyErr) = _hotkeyService.Apply(
            _settings.HotkeyEnabled,
            _settings.HotkeyModifiers,
            _settings.HotkeyVirtualKey);
        _mainViewModel.Settings.RefreshHotkeyRegistrationStatus(hotkeyOk, hotkeyErr);
        _hotkeyService.HotkeyPressed += () => Dispatcher.BeginInvoke(() => _mainWindow?.ToggleFromHotkey());

        _trayService = new TrayService();
        _trayService.Initialize(_mainWindow, _settings.IsWindowPinned, _settings.IsMiniWidgetEnabled);
        _trayService.UpdateState(_settings.IsWindowPinned, _settings.IsMiniWidgetEnabled, _mainViewModel.BuildTrayTooltipText());

        _trayService.ToggleMainWindowRequested += () => _mainWindow?.ToggleFromTray();
        _trayService.OpenMainWindowRequested += () => _mainWindow?.ShowAndActivate();
        _trayService.TogglePinRequested += () => _mainViewModel?.TogglePin();
        _trayService.ToggleMiniWidgetRequested += () =>
        {
            _mainViewModel?.ToggleMiniWidgetFromTray();
            SetMiniWidgetVisible(_mainViewModel?.IsMiniWidgetEnabled ?? false);
        };
        _trayService.OpenReportsRequested += () =>
        {
            _mainViewModel?.Navigate(MainNavigationTab.Reports);
            _mainWindow?.ShowAndActivate();
        };
        _trayService.OpenSettingsRequested += () =>
        {
            _mainViewModel?.Navigate(MainNavigationTab.Settings);
            _mainWindow?.ShowAndActivate();
        };
        _trayService.ExitRequested += ExitApplication;

        _mainViewModel.HostStateChanged += () =>
        {
            if (_mainViewModel is not null && _trayService is not null)
            {
                _trayService.UpdateState(
                    _mainViewModel.IsPinned,
                    _mainViewModel.IsMiniWidgetEnabled,
                    _mainViewModel.BuildTrayTooltipText());
            }
        };

        _singleInstance.ActivateRequested += () => Dispatcher.BeginInvoke(() => _mainWindow?.ShowAndActivate());

        _midnightService = new MidnightRefreshService(Dispatcher);
        _midnightService.DateChanged += newToday => _mainViewModel?.HandleDateRollover(newToday);

        if (_settings.IsMiniWidgetEnabled)
        {
            SetMiniWidgetVisible(true);
        }

        if (e.Args.Any(a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase)))
        {
            _mainWindow.ShowAndActivate();
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Win32Interop.TrimWorkingSet);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        _midnightService?.Dispose();
        _hotkeyService?.Dispose();
        _themeService?.Dispose();
        _trayService?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void ExitApplication()
    {
        _miniWidgetWindow?.Close();
        _miniWidgetWindow = null;

        if (_mainWindow is not null)
        {
            _mainWindow.PrepareForExit();
            _mainWindow.Close();
        }

        Shutdown(0);
    }

    private void SetMiniWidgetVisible(bool visible)
    {
        if (_mainViewModel is null || _settings is null || _settingsStore is null)
        {
            return;
        }

        if (visible)
        {
            if (_miniWidgetWindow is null)
            {
                _miniWidgetWindow = new MiniWidgetWindow(
                    _mainViewModel.MiniWidget,
                    _settings.MiniWidgetLeft,
                    _settings.MiniWidgetTop,
                    onClicked: () => _mainWindow?.ShowAndActivate(),
                    onPositionSaved: (left, top) =>
                    {
                        _settings.MiniWidgetLeft = left;
                        _settings.MiniWidgetTop = top;
                        _settingsStore.Save(_settings);
                    });
            }

            if (!_miniWidgetWindow.IsVisible)
            {
                _miniWidgetWindow.Show();
            }
        }
        else if (_miniWidgetWindow is not null)
        {
            _miniWidgetWindow.Close();
            _miniWidgetWindow = null;
        }
    }

    private string? PickSaveFilePath(string defaultFileName, string filter, string title)
    {
        if (_mainWindow is null)
        {
            return null;
        }

        return _mainWindow.RunWithAutoHideSuppressed(() =>
        {
            var dialog = new SaveFileDialog
            {
                FileName = defaultFileName,
                Filter = filter,
                Title = title,
                AddExtension = true
            };
            return dialog.ShowDialog(_mainWindow) == true ? dialog.FileName : null;
        });
    }

    private string? PickOpenFilePath(string filter, string title)
    {
        if (_mainWindow is null)
        {
            return null;
        }

        return _mainWindow.RunWithAutoHideSuppressed(() =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = filter,
                Title = title,
                CheckFileExists = true
            };
            return dialog.ShowDialog(_mainWindow) == true ? dialog.FileName : null;
        });
    }

    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _crashLogger?.Log("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            _mainViewModel?.ShowToast("操作出现异常，详情已写入本地错误日志");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _crashLogger?.Log("AppDomain.UnhandledException", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _crashLogger?.Log("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }
}
