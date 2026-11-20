using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Utilities;

namespace PlanLite.ViewModels;

public sealed partial class WeeklyPlanViewModel : ObservableObject
{
    private readonly PlanRepository _repository;
    private readonly Action _onDataMutated;
    private readonly Action<long> _openWeeklyEditor;
    private readonly Action<string> _showToast;
    private readonly Action<bool> _saveCompletedExpanded;

    public WeeklyPlanViewModel(
        PlanRepository repository,
        Action onDataMutated,
        Action<long> openWeeklyEditor,
        Action<string> showToast,
        bool initialCompletedExpanded,
        Action<bool> saveCompletedExpanded)
    {
        _repository = repository;
        _onDataMutated = onDataMutated;
        _openWeeklyEditor = openWeeklyEditor;
        _showToast = showToast;
        _saveCompletedExpanded = saveCompletedExpanded;
        _isCompletedExpanded = initialCompletedExpanded;
        _selectedWeekStart = WeekHelper.GetWeekStart(DateOnly.FromDateTime(DateTime.Now));
    }

    [ObservableProperty]
    private DateOnly _selectedWeekStart;

    [ObservableProperty]
    private string _selectedWeekLabel = string.Empty;

    [ObservableProperty]
    private string _periodBadgeText = "本周";

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private int _averageProgress;

    [ObservableProperty]
    private string _newTaskTitle = string.Empty;

    [ObservableProperty]
    private bool _newTaskAutoMode;

    [ObservableProperty]
    private bool _hasTasks;

    [ObservableProperty]
    private bool _hasUnfinishedTasks;

    [ObservableProperty]
    private bool _hasCompletedTasks;

    [ObservableProperty]
    private bool _isCompletedExpanded;

    public ObservableCollection<WeeklyTaskItemViewModel> UnfinishedTasks { get; } = new();
    public ObservableCollection<WeeklyTaskItemViewModel> CompletedTasks { get; } = new();

    partial void OnIsCompletedExpandedChanged(bool value)
    {
        _saveCompletedExpanded(value);
    }

    public void SetWeek(DateOnly dateInWeek)
    {
        FlushPendingSaves();
        SelectedWeekStart = WeekHelper.GetWeekStart(dateInWeek);
        Reload();
    }

    public void Reload()
    {
        DetachAllItems();

        var currentWeekStart = WeekHelper.GetWeekStart(DateOnly.FromDateTime(DateTime.Now));
        SelectedWeekLabel = WeekHelper.FormatWeekLabel(SelectedWeekStart);
        PeriodBadgeText = SelectedWeekStart == currentWeekStart
            ? "本周"
            : SelectedWeekStart < currentWeekStart ? "历史" : "规划";

        var tasks = _repository.GetWeeklyTasks(SelectedWeekStart);
        var summary = PlanRepository.ComputeSummary(tasks.Select(x => x.Progress));
        AverageProgress = summary.AverageProgress;
        SummaryText = summary.TotalCount == 0
            ? "本周暂无计划"
            : $"已完成 {summary.CompletedCount}/{summary.TotalCount} · 总体进度 {summary.AverageProgress}%";
        HasTasks = tasks.Count > 0;

        foreach (var wt in tasks)
        {
            var vm = new WeeklyTaskItemViewModel(wt, OnCommitProgress, item => _openWeeklyEditor(item.Id));
            if (wt.Status == PlanTaskStatus.Completed)
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
    private void PreviousWeek() => SetWeek(SelectedWeekStart.AddDays(-7));

    [RelayCommand]
    private void NextWeek() => SetWeek(SelectedWeekStart.AddDays(7));

    [RelayCommand]
    private void GoToCurrentWeek() => SetWeek(DateOnly.FromDateTime(DateTime.Now));

    [RelayCommand]
    private void AddTask()
    {
        var title = NewTaskTitle?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        var mode = NewTaskAutoMode ? WeeklyProgressMode.Auto : WeeklyProgressMode.Manual;
        _repository.CreateWeeklyTask(SelectedWeekStart, title, progressMode: mode);
        NewTaskTitle = string.Empty;
        _onDataMutated();
    }

    [RelayCommand]
    private void CarryOverToNextWeek()
    {
        FlushPendingSaves();
        var nextWeek = SelectedWeekStart.AddDays(7);
        var result = _repository.CarryOverWeeklyTasks(SelectedWeekStart, nextWeek);
        if (result.CarriedCount > 0)
        {
            _onDataMutated();
            _showToast($"已结转 {result.CarriedCount} 项未完成周计划至下周");
        }
        else if (result.SkippedCount > 0)
        {
            _showToast("未完成周计划已结转至下周，无需重复结转");
        }
        else
        {
            _showToast("本周没有可结转的未完成计划");
        }
    }

    [RelayCommand]
    private void ReorderUnfinished()
    {
        var ids = UnfinishedTasks.Select(x => x.Id).ToList();
        _repository.ReorderWeeklyTasks(SelectedWeekStart, ids);
        _onDataMutated();
    }

    [RelayCommand]
    private void ToggleCompletedExpanded() => IsCompletedExpanded = !IsCompletedExpanded;

    private void OnCommitProgress(long id, int progress)
    {
        _repository.UpdateWeeklyProgress(id, progress);
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
