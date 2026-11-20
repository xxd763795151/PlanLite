namespace PlanLite.Core.Models;

public sealed record PeriodSummary(
    int TotalCount,
    int CompletedCount,
    int InProgressCount,
    int NotStartedCount,
    int AverageProgress)
{
    public static PeriodSummary Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record CarryOverResult(int CarriedCount, int SkippedCount);

public enum HistoryTaskKind
{
    Daily = 0,
    Weekly = 1
}

public sealed class HistorySearchResult
{
    public HistoryTaskKind Kind { get; set; }
    public long TaskId { get; set; }
    public DateOnly PeriodDate { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int Progress { get; set; }
    public PlanTaskStatus Status { get; set; }
    public string? LinkedWeeklyTitle { get; set; }
}

public sealed class HistoryPeriodItem
{
    public HistoryTaskKind Kind { get; set; }
    public DateOnly PeriodDate { get; set; }
    public int TotalCount { get; set; }
    public int CompletedCount { get; set; }
    public int AverageProgress { get; set; }
}
