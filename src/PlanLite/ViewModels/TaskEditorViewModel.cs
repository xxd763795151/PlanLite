using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Utilities;

namespace PlanLite.ViewModels;

public sealed partial class TaskEditorViewModel : ObservableObject
{
    private readonly PlanRepository _repository;
    private readonly Action<string?> _onSaved;
    private readonly DispatcherTimer _autoSaveTimer;
    private bool _suppressAutoSave;
    private bool _hasUnsavedChanges;

    public TaskEditorViewModel(PlanRepository repository, Action<string?> onSaved)
    {
        _repository = repository;
        _onSaved = onSaved;

        _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _autoSaveTimer.Tick += (_, _) => CommitPendingChanges();
    }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isDailyTask;

    [ObservableProperty]
    private bool _isWeeklyTask;

    [ObservableProperty]
    private long _taskId;

    [ObservableProperty]
    private DateOnly _periodDate;

    [ObservableProperty]
    private string _periodDescription = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private PlanTaskStatus _status;

    [ObservableProperty]
    private bool _isAutoMode;

    [ObservableProperty]
    private bool _canEditProgress = true;

    [ObservableProperty]
    private WeeklyLinkOption? _selectedWeeklyLink;

    [ObservableProperty]
    private bool _isDeleteConfirmVisible;

    [ObservableProperty]
    private string? _validationMessage;

    [ObservableProperty]
    private string _linkedDailySummaryText = string.Empty;

    public ObservableCollection<WeeklyLinkOption> WeeklyLinkOptions { get; } = new();
    public ObservableCollection<DailyTask> LinkedDailyTasks { get; } = new();

    public void OpenDaily(long dailyTaskId)
    {
        var task = _repository.GetDailyTaskById(dailyTaskId);
        if (task is null)
        {
            return;
        }

        _suppressAutoSave = true;
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        IsDeleteConfirmVisible = false;
        ValidationMessage = null;

        IsDailyTask = true;
        IsWeeklyTask = false;
        TaskId = task.Id;
        PeriodDate = task.PlanDate;
        PeriodDescription = $"每日计划 · {WeekHelper.FormatChineseDateWithDay(task.PlanDate)}";
        Title = task.Title;
        Notes = task.Notes;
        Progress = task.Progress;
        Status = task.Status;
        IsAutoMode = false;
        CanEditProgress = true;

        WeeklyLinkOptions.Clear();
        WeeklyLinkOptions.Add(new WeeklyLinkOption(null, "不关联周计划（独立任务）"));
        var weekTasks = _repository.GetWeeklyTasks(task.PlanDate);
        foreach (var wt in weekTasks)
        {
            WeeklyLinkOptions.Add(new WeeklyLinkOption(wt.Id, wt.Title));
        }

        SelectedWeeklyLink = WeeklyLinkOptions.FirstOrDefault(o => o.Id == task.WeeklyTaskId)
            ?? WeeklyLinkOptions[0];

        LinkedDailyTasks.Clear();
        LinkedDailySummaryText = string.Empty;

        IsOpen = true;
        _suppressAutoSave = false;
    }

    public void OpenWeekly(long weeklyTaskId)
    {
        var task = _repository.GetWeeklyTaskById(weeklyTaskId);
        if (task is null)
        {
            return;
        }

        _suppressAutoSave = true;
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        IsDeleteConfirmVisible = false;
        ValidationMessage = null;

        IsDailyTask = false;
        IsWeeklyTask = true;
        TaskId = task.Id;
        PeriodDate = task.WeekStart;
        PeriodDescription = $"每周计划 · {WeekHelper.FormatWeekLabel(task.WeekStart)}";
        Title = task.Title;
        Notes = task.Notes;
        Progress = task.Progress;
        Status = task.Status;
        IsAutoMode = task.ProgressMode == WeeklyProgressMode.Auto;
        CanEditProgress = !IsAutoMode;

        WeeklyLinkOptions.Clear();
        SelectedWeeklyLink = null;

        RefreshLinkedDailyPreview(task.Id);

        IsOpen = true;
        _suppressAutoSave = false;
    }

    private void RefreshLinkedDailyPreview(long weeklyTaskId)
    {
        LinkedDailyTasks.Clear();
        var linked = _repository.GetDailyTasksByWeeklyTaskId(weeklyTaskId);
        foreach (var item in linked)
        {
            LinkedDailyTasks.Add(item);
        }

        LinkedDailySummaryText = linked.Count == 0
            ? "暂无关联的每日任务"
            : $"已关联 {linked.Count} 项每日任务（自动计算采用等权平均）";
    }

    partial void OnTitleChanged(string value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        ValidationMessage = string.IsNullOrWhiteSpace(value) ? "标题不能为空" : null;
        ScheduleAutoSave();
    }

    partial void OnNotesChanged(string value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        ScheduleAutoSave();
    }

    partial void OnProgressChanged(int value)
    {
        var clamped = PlanTaskStatusHelper.ClampProgress(value);
        if (clamped != value)
        {
            Progress = clamped;
            return;
        }

        Status = PlanTaskStatusHelper.FromProgress(clamped);
        if (_suppressAutoSave)
        {
            return;
        }

        ScheduleAutoSave();
    }

    partial void OnIsAutoModeChanged(bool value)
    {
        CanEditProgress = !value;
        if (_suppressAutoSave || !IsWeeklyTask)
        {
            return;
        }

        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;

        var mode = value ? WeeklyProgressMode.Auto : WeeklyProgressMode.Manual;
        var updated = _repository.SetWeeklyProgressMode(TaskId, mode);
        _suppressAutoSave = true;
        Progress = updated.Progress;
        Status = updated.Status;
        _suppressAutoSave = false;
        _onSaved(null);
    }

    partial void OnSelectedWeeklyLinkChanged(WeeklyLinkOption? value)
    {
        if (_suppressAutoSave || !IsDailyTask)
        {
            return;
        }

        CommitPendingChanges(forceImmediate: true);
    }

    [RelayCommand]
    private void SetQuickProgress(string percentText)
    {
        if (!CanEditProgress)
        {
            return;
        }

        if (int.TryParse(percentText, out var percent))
        {
            Progress = percent;
            CommitPendingChanges(forceImmediate: true);
        }
    }

    [RelayCommand]
    private void MoveUp()
    {
        MoveInPeriod(direction: -1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        MoveInPeriod(direction: 1);
    }

    [RelayCommand]
    private void RequestDelete()
    {
        IsDeleteConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmVisible = false;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        IsDeleteConfirmVisible = false;

        if (IsDailyTask)
        {
            _repository.DeleteDailyTask(TaskId);
        }
        else if (IsWeeklyTask)
        {
            _repository.DeleteWeeklyTask(TaskId);
        }

        IsOpen = false;
        _onSaved("已删除任务");
    }

    [RelayCommand]
    private void Close()
    {
        CommitPendingChanges(forceImmediate: true);
        IsDeleteConfirmVisible = false;
        IsOpen = false;
    }

    public void FlushIfOpen()
    {
        if (!IsOpen)
        {
            return;
        }

        CommitPendingChanges(forceImmediate: true);
    }

    private void ScheduleAutoSave()
    {
        _hasUnsavedChanges = true;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void CommitPendingChanges(bool forceImmediate = false)
    {
        _autoSaveTimer.Stop();
        if (!IsOpen || (!_hasUnsavedChanges && !forceImmediate))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            ValidationMessage = "任务标题不能为空";
            return;
        }

        ValidationMessage = null;
        _hasUnsavedChanges = false;

        if (IsDailyTask)
        {
            var updated = _repository.UpdateDailyTask(
                TaskId,
                Title,
                Notes,
                Progress,
                SelectedWeeklyLink?.Id);

            _suppressAutoSave = true;
            Progress = updated.Progress;
            Status = updated.Status;
            _suppressAutoSave = false;
        }
        else if (IsWeeklyTask)
        {
            var mode = IsAutoMode ? WeeklyProgressMode.Auto : WeeklyProgressMode.Manual;
            var updated = _repository.UpdateWeeklyTask(
                TaskId,
                Title,
                Notes,
                Progress,
                mode);

            _suppressAutoSave = true;
            Progress = updated.Progress;
            Status = updated.Status;
            _suppressAutoSave = false;
        }

        _onSaved(null);
    }

    private void MoveInPeriod(int direction)
    {
        CommitPendingChanges(forceImmediate: true);

        if (IsDailyTask)
        {
            var list = _repository.GetDailyTasks(PeriodDate).Select(x => x.Id).ToList();
            if (TrySwapOrder(list, TaskId, direction))
            {
                _repository.ReorderDailyTasks(PeriodDate, list);
                _onSaved("已调整任务顺序");
            }
        }
        else if (IsWeeklyTask)
        {
            var list = _repository.GetWeeklyTasks(PeriodDate).Select(x => x.Id).ToList();
            if (TrySwapOrder(list, TaskId, direction))
            {
                _repository.ReorderWeeklyTasks(PeriodDate, list);
                _onSaved("已调整任务顺序");
            }
        }
    }

    private static bool TrySwapOrder(List<long> ids, long targetId, int direction)
    {
        var idx = ids.IndexOf(targetId);
        if (idx < 0)
        {
            return false;
        }

        var nextIdx = idx + direction;
        if (nextIdx < 0 || nextIdx >= ids.Count)
        {
            return false;
        }

        (ids[idx], ids[nextIdx]) = (ids[nextIdx], ids[idx]);
        return true;
    }
}
