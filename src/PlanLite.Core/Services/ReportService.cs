using System.Text;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Utilities;

namespace PlanLite.Core.Services;

public sealed class ReportService
{
    public string GenerateDailyReport(DateOnly date, IReadOnlyList<DailyTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var summary = PlanRepository.ComputeSummary(tasks.Select(t => t.Progress));
        var sb = new StringBuilder();
        sb.AppendLine($"# 工作日报 - {date:yyyy-MM-dd} ({WeekHelper.FormatChineseDayOfWeek(date.DayOfWeek)})");
        sb.AppendLine();
        sb.AppendLine($"> 共 {summary.TotalCount} 项任务 · 已完成 {summary.CompletedCount} 项 · 总体进度 {summary.AverageProgress}%");
        sb.AppendLine();

        AppendDailySection(sb, "## 已完成", tasks.Where(t => t.Status == PlanTaskStatus.Completed).ToList());
        AppendDailySection(sb, "## 进行中", tasks.Where(t => t.Status == PlanTaskStatus.InProgress).ToList());
        AppendDailySection(sb, "## 未开始", tasks.Where(t => t.Status == PlanTaskStatus.NotStarted).ToList());

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public string GenerateWeeklyReport(
        DateOnly dateInWeek,
        IReadOnlyList<WeeklyTask> weeklyTasks,
        IReadOnlyList<DailyTask>? weekDailyTasks = null)
    {
        ArgumentNullException.ThrowIfNull(weeklyTasks);

        var weekStart = WeekHelper.GetWeekStart(dateInWeek);
        var weekEnd = WeekHelper.GetWeekEnd(weekStart);
        var (isoYear, weekNumber) = WeekHelper.GetIsoWeekInfo(weekStart);
        var summary = PlanRepository.ComputeSummary(weeklyTasks.Select(t => t.Progress));

        var sb = new StringBuilder();
        sb.AppendLine($"# 工作周报 - {isoYear}年第{weekNumber}周 ({weekStart:yyyy-MM-dd} ~ {weekEnd:yyyy-MM-dd})");
        sb.AppendLine();

        if (weekDailyTasks is { Count: > 0 })
        {
            var dailyCompleted = weekDailyTasks.Count(d => d.Status == PlanTaskStatus.Completed);
            sb.AppendLine($"> 周目标共 {summary.TotalCount} 项 · 已完成 {summary.CompletedCount} 项 · 总体进度 {summary.AverageProgress}% · 本周每日任务已完成 {dailyCompleted}/{weekDailyTasks.Count} 项");
        }
        else
        {
            sb.AppendLine($"> 周目标共 {summary.TotalCount} 项 · 已完成 {summary.CompletedCount} 项 · 总体进度 {summary.AverageProgress}%");
        }

        sb.AppendLine();

        AppendWeeklySection(sb, "## 已完成", weeklyTasks.Where(t => t.Status == PlanTaskStatus.Completed).ToList());
        AppendWeeklySection(sb, "## 进行中", weeklyTasks.Where(t => t.Status == PlanTaskStatus.InProgress).ToList());
        AppendWeeklySection(sb, "## 未开始", weeklyTasks.Where(t => t.Status == PlanTaskStatus.NotStarted).ToList());

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendDailySection(StringBuilder sb, string heading, IReadOnlyList<DailyTask> items)
    {
        sb.AppendLine(heading);
        if (items.Count == 0)
        {
            sb.AppendLine("- 无");
            sb.AppendLine();
            return;
        }

        foreach (var task in items)
        {
            var linkSuffix = string.IsNullOrWhiteSpace(task.WeeklyTaskTitle)
                ? string.Empty
                : $" · 关联周计划: {task.WeeklyTaskTitle}";
            sb.AppendLine($"- {task.Title} ({task.Progress}%){linkSuffix}");
            AppendNotes(sb, task.Notes);
        }

        sb.AppendLine();
    }

    private static void AppendWeeklySection(StringBuilder sb, string heading, IReadOnlyList<WeeklyTask> items)
    {
        sb.AppendLine(heading);
        if (items.Count == 0)
        {
            sb.AppendLine("- 无");
            sb.AppendLine();
            return;
        }

        foreach (var task in items)
        {
            var modeText = task.ProgressMode == WeeklyProgressMode.Auto ? "自动计算" : "手动";
            sb.AppendLine($"- {task.Title} ({task.Progress}% · {modeText})");
            AppendNotes(sb, task.Notes);
        }

        sb.AppendLine();
    }

    private static void AppendNotes(StringBuilder sb, string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return;
        }

        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            sb.AppendLine($"  - 备注: {line.Trim()}");
        }
    }
}
