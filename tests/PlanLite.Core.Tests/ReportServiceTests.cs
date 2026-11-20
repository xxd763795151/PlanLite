using PlanLite.Core.Models;
using PlanLite.Core.Services;
using Xunit;

namespace PlanLite.Core.Tests;

public sealed class ReportServiceTests
{
    private readonly ReportService _service = new();

    [Fact]
    public void GenerateDailyReport_FormatsAllSectionsAndNotes()
    {
        var date = new DateOnly(2026, 11, 19);
        var tasks = new List<DailyTask>
        {
            new()
            {
                Id = 1,
                PlanDate = date,
                Title = "完成数据库设计",
                Notes = "WAL 模式已启用",
                Progress = 100,
                Status = PlanTaskStatus.Completed,
                WeeklyTaskTitle = "基础设施搭建"
            },
            new()
            {
                Id = 2,
                PlanDate = date,
                Title = "实现托盘菜单",
                Notes = "第一行\n第二行",
                Progress = 60,
                Status = PlanTaskStatus.InProgress
            },
            new()
            {
                Id = 3,
                PlanDate = date,
                Title = "编写单测",
                Progress = 0,
                Status = PlanTaskStatus.NotStarted
            }
        };

        var md = _service.GenerateDailyReport(date, tasks);

        Assert.Contains("# 工作日报 - 2026-11-19 (周四)", md);
        Assert.Contains("共 3 项任务 · 已完成 1 项 · 总体进度 53%", md);
        Assert.Contains("## 已完成", md);
        Assert.Contains("- 完成数据库设计 (100%) · 关联周计划: 基础设施搭建", md);
        Assert.Contains("  - 备注: WAL 模式已启用", md);
        Assert.Contains("## 进行中", md);
        Assert.Contains("- 实现托盘菜单 (60%)", md);
        Assert.Contains("  - 备注: 第一行", md);
        Assert.Contains("  - 备注: 第二行", md);
        Assert.Contains("## 未开始", md);
        Assert.Contains("- 编写单测 (0%)", md);
    }

    [Fact]
    public void GenerateWeeklyReport_FormatsEmptyAndPopulatedSections()
    {
        var thursday = new DateOnly(2026, 11, 19);
        var weeklyTasks = new List<WeeklyTask>
        {
            new()
            {
                Id = 1,
                WeekStart = new DateOnly(2026, 11, 16),
                Title = "核心功能上线",
                Progress = 80,
                Status = PlanTaskStatus.InProgress,
                ProgressMode = WeeklyProgressMode.Auto,
                Notes = "关联 4 个每日任务"
            }
        };
        var dailyTasks = new List<DailyTask>
        {
            new() { Id = 1, Progress = 100, Status = PlanTaskStatus.Completed },
            new() { Id = 2, Progress = 60, Status = PlanTaskStatus.InProgress }
        };

        var md = _service.GenerateWeeklyReport(thursday, weeklyTasks, dailyTasks);

        Assert.Contains("# 工作周报 - 2026年第47周 (2026-11-16 ~ 2026-11-22)", md);
        Assert.Contains("周目标共 1 项 · 已完成 0 项 · 总体进度 80% · 本周每日任务已完成 1/2 项", md);
        Assert.Contains("## 已完成\n- 无", md.Replace("\r\n", "\n"));
        Assert.Contains("- 核心功能上线 (80% · 自动计算)", md);
        Assert.Contains("  - 备注: 关联 4 个每日任务", md);
    }
}
