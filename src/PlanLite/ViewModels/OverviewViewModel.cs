using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Utilities;

namespace PlanLite.ViewModels;

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly PlanRepository _repository;
    private readonly Action _onDataMutated;
    private readonly Action<long> _openDailyEditor;
    private readonly Action<long> _openWeeklyEditor;
    private readonly Action _navigateToToday;
    private readonly Action _navigateToThisWeek;

    public OverviewViewModel(
        PlanRepository repository,
        Action onDataMutated,
        Action<long> openDailyEditor,
        Action<long> openWeeklyEditor,
        Action navigateToToday,
        Action navigateToThisWeek)
    {
        _repository = repository;
        _onDataMutated = onDataMutated;
        _openDailyEditor = openDailyEditor;
        _openWeeklyEditor = openWeeklyEditor;
        _navigateToToday = navigateToToday;
        _navigateToThisWeek = navigateToThisWeek;
    }

    [ObservableProperty]
    private string _weekHeaderLabel = string.Empty;

    [ObservableProperty]
    private string _weeklySummaryText = string.Empty;

    [ObservableProperty]
    private int _weeklyAverageProgress;

    [ObservableProperty]
    private string _dailySummaryText = string.Empty;

    [ObservableProperty]
    private int _dailyAverageProgress;

    [ObservableProperty]
    private string _quickDailyTitle = string.Empty;

    [ObservableProperty]
    private bool _hasWeeklyTasks;

    [ObservableProperty]
    private bool _hasDailyTasks;

    [ObservableProperty]
    private bool _hasCompletedWeeklyTasks;

    [ObservableProperty]
    private bool _hasCompletedDailyTasks;

    [ObservableProperty]
    private bool _isCompletedWeeklyExpanded;

    [ObservableProperty]
    private bool _isCompletedDailyExpanded;

    public ObservableCollection<WeeklyTaskItemViewModel> UnfinishedWeeklyTasks { get; } = new();
    public ObservableCollection<WeeklyTaskItemViewModel> CompletedWeeklyTasks { get; } = new();
    public ObservableCollection<DailyTaskItemViewModel> UnfinishedDailyTasks { get; } = new();
    public ObservableCollection<DailyTaskItemViewModel> CompletedDailyTasks { get; } = new();

    public void Load(DateOnly today)
    {
        DetachAllItems();

        WeekHeaderLabel = WeekHelper.FormatWeekLabel(today);

        var weeklyTasks = _repository.GetWeeklyTasks(today);
        var weekSummary = PlanRepository.ComputeSummary(weeklyTasks.Select(x => x.Progress));
        WeeklyAverageProgress = weekSummary.AverageProgress;
        WeeklySummaryText = weekSummary.TotalCount == 0
            ? "本周暂无目标"
            : $"已完成 {weekSummary.CompletedCount}/{weekSummary.TotalCount} · 总体 {weekSummary.AverageProgress}%";
        HasWeeklyTasks = weeklyTasks.Count > 0;

        foreach (var wt in weeklyTasks)
        {
            var vm = new WeeklyTaskItemViewModel(wt, OnCommitWeeklyProgress, item => _openWeeklyEditor(item.Id));
            if (wt.Status == PlanTaskStatus.Completed)
            {
                CompletedWeeklyTasks.Add(vm);
            }
            else
            {
                UnfinishedWeeklyTasks.Add(vm);
            }
        }

        HasCompletedWeeklyTasks = CompletedWeeklyTasks.Count > 0;

        var dailyTasks = _repository.GetDailyTasks(today);
        var daySummary = PlanRepository.ComputeSummary(dailyTasks.Select(x => x.Progress));
        DailyAverageProgress = daySummary.AverageProgress;
        DailySummaryText = daySummary.TotalCount == 0
            ? "今日暂无计划"
            : $"已完成 {daySummary.CompletedCount}/{daySummary.TotalCount} · 总体 {daySummary.AverageProgress}%";
        HasDailyTasks = dailyTasks.Count > 0;

        foreach (var dt in dailyTasks)
        {
            var vm = new DailyTaskItemViewModel(dt, OnToggleDailyComplete, OnCommitDailyProgress, item => _openDailyEditor(item.Id));
            if (dt.Status == PlanTaskStatus.Completed)
            {
                CompletedDailyTasks.Add(vm);
            }
            else
            {
                UnfinishedDailyTasks.Add(vm);
            }
        }

        HasCompletedDailyTasks = CompletedDailyTasks.Count > 0;
    }

    public void FlushPendingSaves()
    {
        foreach (var item in UnfinishedWeeklyTasks) item.FlushPendingSave();
        foreach (var item in CompletedWeeklyTasks) item.FlushPendingSave();
        foreach (var item in UnfinishedDailyTasks) item.FlushPendingSave();
        foreach (var item in CompletedDailyTasks) item.FlushPendingSave();
    }

    [RelayCommand]
    private void QuickAddDailyTask()
    {
        var title = QuickDailyTitle?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        _repository.CreateDailyTask(today, title);
        QuickDailyTitle = string.Empty;
        _onDataMutated();
    }

    [RelayCommand]
    private void ToggleCompletedWeeklyExpanded() => IsCompletedWeeklyExpanded = !IsCompletedWeeklyExpanded;

    [RelayCommand]
    private void ToggleCompletedDailyExpanded() => IsCompletedDailyExpanded = !IsCompletedDailyExpanded;

    [RelayCommand]
    private void OpenTodayTab() => _navigateToToday();

    [RelayCommand]
    private void OpenThisWeekTab() => _navigateToThisWeek();

    private void OnToggleDailyComplete(long id)
    {
        _repository.ToggleDailyComplete(id);
        _onDataMutated();
    }

    private void OnCommitDailyProgress(long id, int progress)
    {
        _repository.UpdateDailyProgress(id, progress);
        _onDataMutated();
    }

    private void OnCommitWeeklyProgress(long id, int progress)
    {
        _repository.UpdateWeeklyProgress(id, progress);
        _onDataMutated();
    }

    private void DetachAllItems()
    {
        foreach (var item in UnfinishedWeeklyTasks) item.Detach();
        foreach (var item in CompletedWeeklyTasks) item.Detach();
        foreach (var item in UnfinishedDailyTasks) item.Detach();
        foreach (var item in CompletedDailyTasks) item.Detach();

        UnfinishedWeeklyTasks.Clear();
        CompletedWeeklyTasks.Clear();
        UnfinishedDailyTasks.Clear();
        CompletedDailyTasks.Clear();
    }
}
