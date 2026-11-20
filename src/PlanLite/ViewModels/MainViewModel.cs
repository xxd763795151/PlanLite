using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Paths;
using PlanLite.Core.Services;
using PlanLite.Core.Utilities;
using PlanLite.Services;

namespace PlanLite.ViewModels;

public enum MainNavigationTab
{
    Overview = 0,
    Today = 1,
    ThisWeek = 2,
    History = 3,
    Reports = 4,
    Settings = 5
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly PlanRepository _repository;
    private readonly SettingsStore _settingsStore;
    private readonly ThemeService _themeService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly Action<bool> _onMiniWidgetVisibilityChanged;
    private readonly DispatcherTimer _toastTimer;
    private AppSettings _settings;

    public MainViewModel(
        AppPaths paths,
        AppSettings settings,
        PlanRepository repository,
        SettingsStore settingsStore,
        ReportService reportService,
        BackupService backupService,
        StartupService startupService,
        ThemeService themeService,
        GlobalHotkeyService hotkeyService,
        Func<string, string, string, string?> pickSaveFilePath,
        Func<string, string, string?> pickOpenFilePath,
        Action<bool> onMiniWidgetVisibilityChanged)
    {
        _ = paths;
        _settings = settings;
        _repository = repository;
        _settingsStore = settingsStore;
        _themeService = themeService;
        _hotkeyService = hotkeyService;
        _onMiniWidgetVisibilityChanged = onMiniWidgetVisibilityChanged;

        _toastTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2.6)
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastMessage = null;
        };

        IsPinned = settings.IsWindowPinned;
        IsMiniWidgetEnabled = settings.IsMiniWidgetEnabled;

        TaskEditor = new TaskEditorViewModel(repository, toast => RefreshAllData(toast));

        Overview = new OverviewViewModel(
            repository,
            () => RefreshAllData(),
            id => TaskEditor.OpenDaily(id),
            id => TaskEditor.OpenWeekly(id),
            () => Navigate(MainNavigationTab.Today),
            () => Navigate(MainNavigationTab.ThisWeek));

        DailyPlan = new DailyPlanViewModel(
            repository,
            () => RefreshAllData(),
            id => TaskEditor.OpenDaily(id),
            ShowToast,
            settings.ShowCompletedDailyExpanded,
            expanded =>
            {
                _settings.ShowCompletedDailyExpanded = expanded;
                _settingsStore.Save(_settings);
            });

        WeeklyPlan = new WeeklyPlanViewModel(
            repository,
            () => RefreshAllData(),
            id => TaskEditor.OpenWeekly(id),
            ShowToast,
            settings.ShowCompletedWeeklyExpanded,
            expanded =>
            {
                _settings.ShowCompletedWeeklyExpanded = expanded;
                _settingsStore.Save(_settings);
            });

        History = new HistoryViewModel(
            repository,
            date =>
            {
                DailyPlan.SetDate(date);
                Navigate(MainNavigationTab.Today);
            },
            weekStart =>
            {
                WeeklyPlan.SetWeek(weekStart);
                Navigate(MainNavigationTab.ThisWeek);
            });

        Reports = new ReportViewModel(repository, reportService, pickSaveFilePath, ShowToast);

        MiniWidget = new MiniWidgetViewModel(repository);

        Settings = new SettingsViewModel(
            paths,
            settings,
            settingsStore,
            startupService,
            themeService,
            hotkeyService,
            backupService,
            pickSaveFilePath,
            pickOpenFilePath,
            pinned =>
            {
                IsPinned = pinned;
                OnStateChangedForHost();
            },
            enabled =>
            {
                IsMiniWidgetEnabled = enabled;
                onMiniWidgetVisibilityChanged(enabled);
                OnStateChangedForHost();
            },
            ReloadAfterBackupRestore,
            ShowToast);

        UpdateHeaderMetadata(DateOnly.FromDateTime(DateTime.Now));
        RefreshAllData();
    }

    public OverviewViewModel Overview { get; }
    public DailyPlanViewModel DailyPlan { get; }
    public WeeklyPlanViewModel WeeklyPlan { get; }
    public HistoryViewModel History { get; }
    public ReportViewModel Reports { get; }
    public SettingsViewModel Settings { get; }
    public TaskEditorViewModel TaskEditor { get; }
    public MiniWidgetViewModel MiniWidget { get; }

    public event Action? HostStateChanged;

    [ObservableProperty]
    private MainNavigationTab _activeTab = MainNavigationTab.Overview;

    [ObservableProperty]
    private bool _isPrimaryTabView = true;

    [ObservableProperty]
    private string _activeSecondaryTitle = string.Empty;

    [ObservableProperty]
    private string _todayDisplayText = string.Empty;

    [ObservableProperty]
    private string _weekBadgeText = string.Empty;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isMiniWidgetEnabled;

    [ObservableProperty]
    private string? _toastMessage;

    partial void OnActiveTabChanged(MainNavigationTab value)
    {
        IsPrimaryTabView = value is MainNavigationTab.Overview or MainNavigationTab.Today or MainNavigationTab.ThisWeek;
        ActiveSecondaryTitle = value switch
        {
            MainNavigationTab.History => "历史记录",
            MainNavigationTab.Reports => "日报与周报",
            MainNavigationTab.Settings => "软件设置",
            _ => string.Empty
        };

        switch (value)
        {
            case MainNavigationTab.History:
                History.Reload();
                break;
            case MainNavigationTab.Reports:
                Reports.RefreshReport();
                break;
        }
    }

    [RelayCommand]
    public void Navigate(MainNavigationTab tab)
    {
        FlushPendingSaves();
        ActiveTab = tab;
    }

    [RelayCommand]
    private void NavigateByName(string tabName)
    {
        if (Enum.TryParse<MainNavigationTab>(tabName, ignoreCase: true, out var tab))
        {
            Navigate(tab);
        }
    }

    [RelayCommand]
    private void BackToOverview()
    {
        Navigate(MainNavigationTab.Overview);
    }

    [RelayCommand]
    public void TogglePin()
    {
        IsPinned = !IsPinned;
        _settings.IsWindowPinned = IsPinned;
        _settingsStore.Save(_settings);
        Settings.SyncPinnedFromExternal(IsPinned);
        OnStateChangedForHost();
        ShowToast(IsPinned ? "已固定主窗口" : "已取消固定（点击外部自动隐藏）");
    }

    public void ToggleMiniWidgetFromTray()
    {
        var next = !IsMiniWidgetEnabled;
        IsMiniWidgetEnabled = next;
        _settings.IsMiniWidgetEnabled = next;
        _settingsStore.Save(_settings);
        Settings.SyncMiniWidgetFromExternal(next);
        OnStateChangedForHost();
    }

    public string BuildTrayTooltipText()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tasks = _repository.GetDailyTasks(today);
        var summary = PlanRepository.ComputeSummary(tasks.Select(x => x.Progress));
        return summary.TotalCount == 0
            ? "PlanLite · 今日暂无计划"
            : $"PlanLite · 今日 {summary.CompletedCount}/{summary.TotalCount} ({summary.AverageProgress}%)";
    }

    public void RefreshAllData(string? toastMessage = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        UpdateHeaderMetadata(today);
        Overview.Load(today);
        DailyPlan.Reload();
        WeeklyPlan.Reload();
        MiniWidget.Refresh(today);

        if (ActiveTab == MainNavigationTab.History)
        {
            History.Reload();
        }
        else if (ActiveTab == MainNavigationTab.Reports)
        {
            Reports.RefreshReport();
        }

        OnStateChangedForHost();
        if (!string.IsNullOrWhiteSpace(toastMessage))
        {
            ShowToast(toastMessage);
        }
    }

    public void FlushPendingSaves()
    {
        TaskEditor.FlushIfOpen();
        Overview.FlushPendingSaves();
        DailyPlan.FlushPendingSaves();
        WeeklyPlan.FlushPendingSaves();
    }

    public void HandleDateRollover(DateOnly newToday)
    {
        FlushPendingSaves();
        UpdateHeaderMetadata(newToday);
        DailyPlan.SetDate(newToday);
        WeeklyPlan.SetWeek(newToday);
        Reports.SelectedDate = newToday;
        RefreshAllData();
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _toastTimer.Stop();
        ToastMessage = message;
        _toastTimer.Start();
    }

    private void ReloadAfterBackupRestore()
    {
        _settings = _settingsStore.Load();
        IsPinned = _settings.IsWindowPinned;
        IsMiniWidgetEnabled = _settings.IsMiniWidgetEnabled;

        _themeService.ApplyTheme(_settings.ThemeMode);
        var (ok, err) = _hotkeyService.Apply(
            _settings.HotkeyEnabled,
            _settings.HotkeyModifiers,
            _settings.HotkeyVirtualKey);

        Settings.LoadFromSettings(_settings);
        Settings.RefreshHotkeyRegistrationStatus(ok, err);
        _onMiniWidgetVisibilityChanged(IsMiniWidgetEnabled);

        var today = DateOnly.FromDateTime(DateTime.Now);
        DailyPlan.SetDate(today);
        WeeklyPlan.SetWeek(today);
        RefreshAllData();
    }

    private void UpdateHeaderMetadata(DateOnly today)
    {
        TodayDisplayText = WeekHelper.FormatChineseDateWithDay(today);
        WeekBadgeText = WeekHelper.FormatShortWeekBadge(today);
    }

    private void OnStateChangedForHost() => HostStateChanged?.Invoke();

    public void Dispose()
    {
        _toastTimer.Stop();
        FlushPendingSaves();
    }
}
