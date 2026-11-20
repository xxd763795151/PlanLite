namespace PlanLite.Core.Models;

public sealed class DailyTask
{
    public long Id { get; set; }
    public DateOnly PlanDate { get; set; }
    public long? WeeklyTaskId { get; set; }
    public string? WeeklyTaskTitle { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int Progress { get; set; }
    public PlanTaskStatus Status { get; set; }
    public int LastNonCompleteProgress { get; set; }
    public int SortOrder { get; set; }
    public long? CarriedFromId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
