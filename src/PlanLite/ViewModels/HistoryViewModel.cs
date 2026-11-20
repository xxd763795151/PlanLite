using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Utilities;

namespace PlanLite.ViewModels;

public sealed class HistorySearchHitViewModel
{
    public HistorySearchHitViewModel(HistorySearchResult model)
    {
        Kind = model.Kind;
        TaskId = model.TaskId;
        PeriodDate = model.PeriodDate;
        Title = model.Title;
        Notes = model.Notes;
        Progress = model.Progress;
        Status = model.Status;
        KindLabel = model.Kind == HistoryTaskKind.Daily ? "日计划" : "周计划";
        PeriodLabel = model.Kind == HistoryTaskKind.Daily
            ? WeekHelper.FormatChineseDateWithDay(model.PeriodDate)
            : WeekHelper.FormatWeekLabel(model.PeriodDate);
        LinkedWeeklyTitle = model.LinkedWeeklyTitle;
    }

    public HistoryTaskKind Kind { get; }
    public long TaskId { get; }
    public DateOnly PeriodDate { get; }
    public string Title { get; }
    public string Notes { get; }
    public int Progress { get; }
    public PlanTaskStatus Status { get; }
    public string KindLabel { get; }
    public string PeriodLabel { get; }
    public string? LinkedWeeklyTitle { get; }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
}

public sealed class HistoryPeriodRowViewModel
{
    public HistoryPeriodRowViewModel(HistoryPeriodItem model)
    {
        Kind = model.Kind;
        PeriodDate = model.PeriodDate;
        TotalCount = model.TotalCount;
        CompletedCount = model.CompletedCount;
        AverageProgress = model.AverageProgress;
        PeriodTitle = model.Kind == HistoryTaskKind.Daily
            ? WeekHelper.FormatChineseDateWithDay(model.PeriodDate)
            : WeekHelper.FormatWeekLabel(model.PeriodDate);
        StatsText = $"完成 {model.CompletedCount}/{model.TotalCount} · 总体 {model.AverageProgress}%";
    }

    public HistoryTaskKind Kind { get; }
    public DateOnly PeriodDate { get; }
    public int TotalCount { get; }
    public int CompletedCount { get; }
    public int AverageProgress { get; }
    public string PeriodTitle { get; }
    public string StatsText { get; }
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private const int PageSize = 30;
    private readonly PlanRepository _repository;
    private readonly Action<DateOnly> _openDailyDate;
    private readonly Action<DateOnly> _openWeeklyStart;
    private readonly DispatcherTimer _searchDebounceTimer;
    private int _loadedCount;

    public HistoryViewModel(
        PlanRepository repository,
        Action<DateOnly> openDailyDate,
        Action<DateOnly> openWeeklyStart)
    {
        _repository = repository;
        _openDailyDate = openDailyDate;
        _openWeeklyStart = openWeeklyStart;

        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ExecuteSearch();
        };
    }

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private bool _isShowingSearchResults;

    [ObservableProperty]
    private bool _isDailyMode = true;

    [ObservableProperty]
    private bool _hasMorePeriods;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyText = "暂无历史记录";

    public ObservableCollection<HistorySearchHitViewModel> SearchResults { get; } = new();
    public ObservableCollection<HistoryPeriodRowViewModel> PeriodRows { get; } = new();

    partial void OnSearchKeywordChanged(string value)
    {
        _searchDebounceTimer.Stop();
        if (string.IsNullOrWhiteSpace(value))
        {
            IsShowingSearchResults = false;
            ReloadPeriods(reset: true);
            return;
        }

        _searchDebounceTimer.Start();
    }

    partial void OnIsDailyModeChanged(bool value)
    {
        if (!IsShowingSearchResults)
        {
            ReloadPeriods(reset: true);
        }
    }

    public void Reload()
    {
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            ExecuteSearch();
        }
        else
        {
            ReloadPeriods(reset: true);
        }
    }

    [RelayCommand]
    private void SearchNow()
    {
        _searchDebounceTimer.Stop();
        ExecuteSearch();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchKeyword = string.Empty;
    }

    [RelayCommand]
    private void SwitchPeriodMode(string mode)
    {
        IsDailyMode = string.Equals(mode, "Daily", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void LoadMore()
    {
        ReloadPeriods(reset: false);
    }

    [RelayCommand]
    private void OpenSearchHit(HistorySearchHitViewModel? hit)
    {
        if (hit is null)
        {
            return;
        }

        if (hit.Kind == HistoryTaskKind.Daily)
        {
            _openDailyDate(hit.PeriodDate);
        }
        else
        {
            _openWeeklyStart(hit.PeriodDate);
        }
    }

    [RelayCommand]
    private void OpenPeriod(HistoryPeriodRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.Kind == HistoryTaskKind.Daily)
        {
            _openDailyDate(row.PeriodDate);
        }
        else
        {
            _openWeeklyStart(row.PeriodDate);
        }
    }

    private void ExecuteSearch()
    {
        var kw = SearchKeyword?.Trim();
        if (string.IsNullOrEmpty(kw))
        {
            IsShowingSearchResults = false;
            ReloadPeriods(reset: true);
            return;
        }

        IsShowingSearchResults = true;
        SearchResults.Clear();

        var hits = _repository.SearchTasks(kw, limit: 100);
        foreach (var hit in hits)
        {
            SearchResults.Add(new HistorySearchHitViewModel(hit));
        }

        IsEmpty = SearchResults.Count == 0;
        EmptyText = $"未找到包含 “{kw}” 的任务记录";
    }

    private void ReloadPeriods(bool reset)
    {
        IsShowingSearchResults = false;

        if (reset)
        {
            PeriodRows.Clear();
            _loadedCount = 0;
        }

        var items = IsDailyMode
            ? _repository.GetHistoricalDailyPeriods(PageSize + 1, _loadedCount)
            : _repository.GetHistoricalWeeklyPeriods(PageSize + 1, _loadedCount);

        HasMorePeriods = items.Count > PageSize;
        var takeCount = Math.Min(items.Count, PageSize);
        for (var i = 0; i < takeCount; i++)
        {
            PeriodRows.Add(new HistoryPeriodRowViewModel(items[i]));
        }

        _loadedCount += takeCount;
        IsEmpty = PeriodRows.Count == 0;
        EmptyText = IsDailyMode ? "暂无每日历史计划" : "暂无每周历史计划";
    }
}
