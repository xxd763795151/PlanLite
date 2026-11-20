using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Utilities;

namespace PlanLite.ViewModels;

public sealed partial class DailyPlanViewModel : ObservableObject
{
    private readonly PlanRepository _repository;
    private readonly Action _onDataMutated;
    private readonly Action<long> _openDailyEditor;
    private readonly Action<string> _showToast;
    private readonly Action<bool> _saveCompletedExpanded;

    public DailyPlanViewModel(
        PlanRepository repository,
        Action onDataMutated,
        Action<long> openDailyEditor,
        Action<string> showToast,
        bool initialCompletedExpanded,
        Action<bool> saveCompletedExpanded)
    {
        _repository = repository;
        _onDataMutated = onDataMutated;
        _openDailyEditor = openDailyEditor;
        _showToast = showToast;
        _saveCompletedExpanded = saveCompletedExpanded;
        _isCompletedExpanded = initialCompletedExpanded;
        _selectedDate = DateOnly.FromDateTime(DateTime.Now);
    }

    [ObservableProperty]
    private DateOnly _selectedDate;

    [ObservableProperty]
    private string _selectedDateLabel = string.Empty;

    [ObservableProperty]
    private string _periodBadgeText = "今天";

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private int _averageProgress;

    [ObservableProperty]
    private string _newTaskTitle = string.Empty;

    [ObservableProperty]
    private WeeklyLinkOption? _selectedWeeklyLink;

    [ObservableProperty]
    private bool _hasTasks;

    [ObservableProperty]
    private bool _hasUnfinishedTasks;

    [ObservableProperty]
    private bool _hasCompletedTasks;

    [ObservableProperty]
    private bool _isCompletedExpanded;

    public ObservableCollection<WeeklyLinkOption> WeeklyLinkOptions { get; } = new();
    public ObservableCollection<DailyTaskItemViewModel> UnfinishedTasks { get; } = new();
    public ObservableCollection<DailyTaskItemViewModel> CompletedTasks { get; } = new();

    partial void OnIsCompletedExpandedChanged(bool value)
    {
        _saveCompletedExpanded(value);
    }

    public void SetDate(DateOnly date)
    {
        FlushPendingSaves();
        SelectedDate = date;
        Reload();
    }

    public void Reload()
    {
        DetachAllItems();

        var today = DateOnly.FromDateTime(DateTime.Now);
        SelectedDateLabel = WeekHelper.FormatChineseDateWithDay(SelectedDate);
        PeriodBadgeText = SelectedDate == today
            ? "今天"
            : SelectedDate < today ? "历史" : "计划";

        var previousSelectedId = SelectedWeeklyLink?.Id;
        WeeklyLinkOptions.Clear();
        WeeklyLinkOptions.Add(new WeeklyLinkOption(null, "独立任务（不关联周计划）"));
        foreach (var wt in _repository.GetWeeklyTasks(SelectedDate))
        {
            WeeklyLinkOptions.Add(new WeeklyLinkOption(wt.Id, wt.Title));
        }

        SelectedWeeklyLink = WeeklyLinkOptions.FirstOrDefault(o => o.Id == previousSelectedId)
            ?? WeeklyLinkOptions[0];

        var tasks = _repository.GetDailyTasks(SelectedDate);
        var summary = PlanRepository.ComputeSummary(tasks.Select(x => x.Progress));
        AverageProgress = summary.AverageProgress;
        SummaryText = summary.TotalCount == 0
            ? "当日暂无任务"
            : $"已完成 {summary.CompletedCount}/{summary.TotalCount} · 总体进度 {summary.AverageProgress}%";
        HasTasks = tasks.Count > 0;

        foreach (var dt in tasks)
        {
            var vm = new DailyTaskItemViewModel(dt, OnToggleComplete, OnCommitProgress, item => _openDailyEditor(item.Id));
            if (dt.Status == PlanTaskStatus.Completed)
            {
                CompletedTasks.Add(vm);
            }
            else
            {
                UnfinishedTasks.Add(vm);
            }
        }

        HasUnfinishedTasks = UnfinishedTasks.Count > 0;
        HasCompletedTasks = CompletedTasks.Count > 0;
    }

    public void FlushPendingSaves()
    {
        foreach (var item in UnfinishedTasks) item.FlushPendingSave();
        foreach (var item in CompletedTasks) item.FlushPendingSave();
    }

    [RelayCommand]
    private void PreviousDay() => SetDate(SelectedDate.AddDays(-1));

    [RelayCommand]
    private void NextDay() => SetDate(SelectedDate.AddDays(1));

    [RelayCommand]
    private void GoToToday() => SetDate(DateOnly.FromDateTime(DateTime.Now));

    [RelayCommand]
    private void AddTask()
    {
        var title = NewTaskTitle?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        _repository.CreateDailyTask(SelectedDate, title, weeklyTaskId: SelectedWeeklyLink?.Id);
        NewTaskTitle = string.Empty;
        _onDataMutated();
    }

    [RelayCommand]
    private void CarryOverToNextDay()
    {
        FlushPendingSaves();
        var nextDay = SelectedDate.AddDays(1);
        var result = _repository.CarryOverDailyTasks(SelectedDate, nextDay);
        if (result.CarriedCount > 0)
        {
            _onDataMutated();
            _showToast($"已结转 {result.CarriedCount} 项未完成任务至 {nextDay:MM/dd}");
        }
        else if (result.SkippedCount > 0)
        {
            _showToast("未完成任务已结转至下一天，无需重复结转");
        }
        else
        {
            _showToast("当日没有可结转的未完成任务");
        }
    }

    [RelayCommand]
    private void ReorderUnfinished()
    {
        var ids = UnfinishedTasks.Select(x => x.Id).ToList();
        _repository.ReorderDailyTasks(SelectedDate, ids);
        _onDataMutated();
    }

    [RelayCommand]
    private void ToggleCompletedExpanded() => IsCompletedExpanded = !IsCompletedExpanded;

    private void OnToggleComplete(long id)
    {
        _repository.ToggleDailyComplete(id);
        _onDataMutated();
    }

    private void OnCommitProgress(long id, int progress)
    {
        _repository.UpdateDailyProgress(id, progress);
        _onDataMutated();
    }

    private void DetachAllItems()
    {
        foreach (var item in UnfinishedTasks) item.Detach();
        foreach (var item in CompletedTasks) item.Detach();
        UnfinishedTasks.Clear();
        CompletedTasks.Clear();
    }
}
