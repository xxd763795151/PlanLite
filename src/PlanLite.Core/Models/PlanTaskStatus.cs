namespace PlanLite.Core.Models;

public enum PlanTaskStatus
{
    NotStarted = 0,
    InProgress = 1,
    Completed = 2
}

public static class PlanTaskStatusHelper
{
    public static int ClampProgress(int progress) => Math.Clamp(progress, 0, 100);

    public static PlanTaskStatus FromProgress(int progress)
    {
        var clamped = ClampProgress(progress);
        return clamped switch
        {
            0 => PlanTaskStatus.NotStarted,
            100 => PlanTaskStatus.Completed,
            _ => PlanTaskStatus.InProgress
        };
    }

    public static string ToChineseLabel(this PlanTaskStatus status) => status switch
    {
        PlanTaskStatus.NotStarted => "未开始",
        PlanTaskStatus.InProgress => "进行中",
        PlanTaskStatus.Completed => "已完成",
        _ => "未开始"
    };
}
