using CommunityToolkit.Mvvm.ComponentModel;
using PlanLite.Core.Data;
using PlanLite.Core.Models;

namespace PlanLite.ViewModels;

public sealed partial class MiniWidgetViewModel : ObservableObject
{
    private readonly PlanRepository _repository;

    public MiniWidgetViewModel(PlanRepository repository)
    {
        _repository = repository;
    }

    [ObservableProperty]
    private string _countBadgeText = "0/0";

    [ObservableProperty]
    private int _averageProgress;

    [ObservableProperty]
    private string _progressText = "0%";

    [ObservableProperty]
    private string _activeTaskTitle = "今日暂无任务";

    [ObservableProperty]
    private string _statusBadgeText = "空闲";

    public void Refresh(DateOnly today)
    {
        var tasks = _repository.GetDailyTasks(today);
        var summary = PlanRepository.ComputeSummary(tasks.Select(x => x.Progress));

        CountBadgeText = $"{summary.CompletedCount}/{summary.TotalCount}";
        AverageProgress = summary.AverageProgress;
        ProgressText = $"{summary.AverageProgress}%";

        if (tasks.Count == 0)
        {
            ActiveTaskTitle = "点击制定今日工作计划";
            StatusBadgeText = "今日";
            return;
        }

        var inProgress = tasks.FirstOrDefault(t => t.Status == PlanTaskStatus.InProgress);
        if (inProgress is not null)
        {
            ActiveTaskTitle = inProgress.Title;
            StatusBadgeText = $"{inProgress.Progress}%";
            return;
        }

        var notStarted = tasks.FirstOrDefault(t => t.Status == PlanTaskStatus.NotStarted);
        if (notStarted is not null)
        {
            ActiveTaskTitle = notStarted.Title;
            StatusBadgeText = "待开始";
            return;
        }

        ActiveTaskTitle = "今日任务已全部完成";
        StatusBadgeText = "100%";
    }
}
