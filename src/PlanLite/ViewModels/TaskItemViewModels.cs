using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Models;

namespace PlanLite.ViewModels;

public sealed record WeeklyLinkOption(long? Id, string Title);

public sealed partial class DailyTaskItemViewModel : ObservableObject
{
    private readonly Action<long> _onToggleComplete;
    private readonly Action<long, int> _onCommitProgress;
    private readonly Action<DailyTaskItemViewModel> _onOpenEditor;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressCallbacks;
    private bool _hasPendingProgressSave;

    public DailyTaskItemViewModel(
        DailyTask model,
        Action<long> onToggleComplete,
        Action<long, int> onCommitProgress,
        Action<DailyTaskItemViewModel> onOpenEditor)
    {
        _onToggleComplete = onToggleComplete;
        _onCommitProgress = onCommitProgress;
        _onOpenEditor = onOpenEditor;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _saveTimer.Tick += OnSaveTimerTick;

        Id = model.Id;
        PlanDate = model.PlanDate;
        WeeklyTaskId = model.WeeklyTaskId;
        WeeklyTaskTitle = model.WeeklyTaskTitle;
        Title = model.Title;
        Notes = model.Notes;

        _suppressCallbacks = true;
        Progress = model.Progress;
        Status = model.Status;
        IsCompleted = model.Status == PlanTaskStatus.Completed;
        _suppressCallbacks = false;
    }

    public long Id { get; }
    public DateOnly PlanDate { get; }
    public long? WeeklyTaskId { get; }
    public string? WeeklyTaskTitle { get; }
    public string Title { get; }
    public string Notes { get; }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public bool HasWeeklyLink => !string.IsNullOrWhiteSpace(WeeklyTaskTitle);

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private PlanTaskStatus _status;

    [ObservableProperty]
    private bool _isCompleted;

    partial void OnProgressChanged(int value)
    {
        var clamped = PlanTaskStatusHelper.ClampProgress(value);
        if (clamped != value)
        {
            Progress = clamped;
            return;
        }

        Status = PlanTaskStatusHelper.FromProgress(clamped);

        if (_suppressCallbacks)
        {
            return;
        }

        _hasPendingProgressSave = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    [RelayCommand]
    private void ToggleComplete()
    {
        _saveTimer.Stop();
        _hasPendingProgressSave = false;
        _onToggleComplete(Id);
    }

    [RelayCommand]
    private void OpenEditor()
    {
        FlushPendingSave();
        _onOpenEditor(this);
    }

    public void FlushPendingSave()
    {
        _saveTimer.Stop();
        if (!_hasPendingProgressSave)
        {
            return;
        }

        _hasPendingProgressSave = false;
        _onCommitProgress(Id, Progress);
    }

    public void Detach()
    {
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        _saveTimer.Stop();
        if (!_hasPendingProgressSave)
        {
            return;
        }

        _hasPendingProgressSave = false;
        _onCommitProgress(Id, Progress);
    }
}

public sealed partial class WeeklyTaskItemViewModel : ObservableObject
{
    private readonly Action<long, int> _onCommitProgress;
    private readonly Action<WeeklyTaskItemViewModel> _onOpenEditor;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressCallbacks;
    private bool _hasPendingProgressSave;

    public WeeklyTaskItemViewModel(
        WeeklyTask model,
        Action<long, int> onCommitProgress,
        Action<WeeklyTaskItemViewModel> onOpenEditor)
    {
        _onCommitProgress = onCommitProgress;
        _onOpenEditor = onOpenEditor;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _saveTimer.Tick += OnSaveTimerTick;

        Id = model.Id;
        WeekStart = model.WeekStart;
        Title = model.Title;
        Notes = model.Notes;
        ProgressMode = model.ProgressMode;
        LinkedDailyCount = model.LinkedDailyCount;

        _suppressCallbacks = true;
        Progress = model.Progress;
        Status = model.Status;
        IsCompleted = model.Status == PlanTaskStatus.Completed;
        _suppressCallbacks = false;
    }

    public long Id { get; }
    public DateOnly WeekStart { get; }
    public string Title { get; }
    public string Notes { get; }
    public WeeklyProgressMode ProgressMode { get; }
    public int LinkedDailyCount { get; }
    public bool IsAutoMode => ProgressMode == WeeklyProgressMode.Auto;
    public bool IsManualMode => ProgressMode == WeeklyProgressMode.Manual;
    public string ModeLabel => IsAutoMode ? $"自动 · {LinkedDailyCount}项" : "手动";
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private PlanTaskStatus _status;

    [ObservableProperty]
    private bool _isCompleted;

    partial void OnProgressChanged(int value)
    {
        var clamped = PlanTaskStatusHelper.ClampProgress(value);
        if (clamped != value)
        {
            Progress = clamped;
            return;
        }

        Status = PlanTaskStatusHelper.FromProgress(clamped);

        if (_suppressCallbacks || IsAutoMode)
        {
            return;
        }

        _hasPendingProgressSave = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    [RelayCommand]
    private void ToggleManualComplete()
    {
        if (IsAutoMode)
        {
            return;
        }

        _saveTimer.Stop();
        _hasPendingProgressSave = false;
        var nextProgress = Status == PlanTaskStatus.Completed ? 0 : 100;
        _onCommitProgress(Id, nextProgress);
    }

    [RelayCommand]
    private void OpenEditor()
    {
        FlushPendingSave();
        _onOpenEditor(this);
    }

    public void FlushPendingSave()
    {
        _saveTimer.Stop();
        if (!_hasPendingProgressSave || IsAutoMode)
        {
            return;
        }

        _hasPendingProgressSave = false;
        _onCommitProgress(Id, Progress);
    }

    public void Detach()
    {
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        _saveTimer.Stop();
        if (!_hasPendingProgressSave || IsAutoMode)
        {
            return;
        }

        _hasPendingProgressSave = false;
        _onCommitProgress(Id, Progress);
    }
}
