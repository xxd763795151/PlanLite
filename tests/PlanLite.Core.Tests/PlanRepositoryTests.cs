using Microsoft.Data.Sqlite;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using Xunit;

namespace PlanLite.Core.Tests;

public sealed class PlanRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDatabase _database;
    private readonly PlanRepository _repo;

    public PlanRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"planlite-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _database = new SqliteDatabase(Path.Combine(_tempDir, "planlite.db"));
        _database.Initialize();
        _repo = new PlanRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup locks.
        }
    }

    [Theory]
    [InlineData(-10, 0, PlanTaskStatus.NotStarted)]
    [InlineData(0, 0, PlanTaskStatus.NotStarted)]
    [InlineData(1, 1, PlanTaskStatus.InProgress)]
    [InlineData(55, 55, PlanTaskStatus.InProgress)]
    [InlineData(99, 99, PlanTaskStatus.InProgress)]
    [InlineData(100, 100, PlanTaskStatus.Completed)]
    [InlineData(150, 100, PlanTaskStatus.Completed)]
    public void StatusDerivation_ClampsProgressAndMapsCorrectStatus(
        int inputProgress,
        int expectedProgress,
        PlanTaskStatus expectedStatus)
    {
        var day = new DateOnly(2026, 11, 19);
        var task = _repo.CreateDailyTask(day, "测试任务", progress: inputProgress);

        Assert.Equal(expectedProgress, task.Progress);
        Assert.Equal(expectedStatus, task.Status);
        Assert.Equal(expectedStatus == PlanTaskStatus.Completed, task.CompletedAtUtc.HasValue);
    }

    [Fact]
    public void WeeklyCrudAndReorder_PersistsOrderAndComputesSummary()
    {
        var thursday = new DateOnly(2026, 11, 19);
        var w1 = _repo.CreateWeeklyTask(thursday, "目标A", "备注A", 0);
        var w2 = _repo.CreateWeeklyTask(thursday, "目标B", "备注B", 50);
        var w3 = _repo.CreateWeeklyTask(thursday, "目标C", "", 100);

        Assert.Equal(new DateOnly(2026, 11, 16), w1.WeekStart);

        _repo.ReorderWeeklyTasks(thursday, new[] { w3.Id, w1.Id, w2.Id });

        var list = _repo.GetWeeklyTasks(new DateOnly(2026, 11, 22)); // Sunday in same week
        Assert.Equal(new[] { w3.Id, w1.Id, w2.Id }, list.Select(x => x.Id).ToArray());

        var summary = PlanRepository.ComputeSummary(list.Select(x => x.Progress));
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(1, summary.InProgressCount);
        Assert.Equal(1, summary.NotStartedCount);
        Assert.Equal(50, summary.AverageProgress);

        var updated = _repo.UpdateWeeklyProgress(w1.Id, 100);
        Assert.Equal(PlanTaskStatus.Completed, updated.Status);
        Assert.NotNull(updated.CompletedAtUtc);

        _repo.DeleteWeeklyTask(w2.Id);
        Assert.Null(_repo.GetWeeklyTaskById(w2.Id));
    }

    [Fact]
    public void DailyToggleComplete_PreservesAndRestoresPriorNonCompleteProgress()
    {
        var day = new DateOnly(2026, 11, 19);
        var d1 = _repo.CreateDailyTask(day, "接口联调", progress: 45);

        var completed = _repo.ToggleDailyComplete(d1.Id);
        Assert.Equal(100, completed.Progress);
        Assert.Equal(PlanTaskStatus.Completed, completed.Status);
        Assert.Equal(45, completed.LastNonCompleteProgress);
        Assert.NotNull(completed.CompletedAtUtc);

        var restored = _repo.ToggleDailyComplete(d1.Id);
        Assert.Equal(45, restored.Progress);
        Assert.Equal(PlanTaskStatus.InProgress, restored.Status);
        Assert.Null(restored.CompletedAtUtc);

        var d0 = _repo.CreateDailyTask(day, "文档编写", progress: 0);
        var d0Done = _repo.ToggleDailyComplete(d0.Id);
        Assert.Equal(100, d0Done.Progress);
        var d0Undo = _repo.ToggleDailyComplete(d0.Id);
        Assert.Equal(0, d0Undo.Progress);
        Assert.Equal(PlanTaskStatus.NotStarted, d0Undo.Status);
    }

    [Fact]
    public void AutoWeeklyProgress_CalculatesEqualWeightAverageAcrossDailyLifecycle()
    {
        var monday = new DateOnly(2026, 11, 16);
        var tuesday = new DateOnly(2026, 11, 17);

        var weeklyA = _repo.CreateWeeklyTask(monday, "核心模块开发", progressMode: WeeklyProgressMode.Auto);
        var weeklyB = _repo.CreateWeeklyTask(monday, "测试覆盖", progressMode: WeeklyProgressMode.Auto);

        Assert.Equal(0, weeklyA.Progress);

        var d1 = _repo.CreateDailyTask(monday, "子任务1", progress: 20, weeklyTaskId: weeklyA.Id);
        var d2 = _repo.CreateDailyTask(monday, "子任务2", progress: 50, weeklyTaskId: weeklyA.Id);
        var d3 = _repo.CreateDailyTask(tuesday, "子任务3", progress: 100, weeklyTaskId: weeklyA.Id);

        // (20 + 50 + 100) / 3 = 56.666 -> 57
        var refreshedA = _repo.GetWeeklyTaskById(weeklyA.Id)!;
        Assert.Equal(57, refreshedA.Progress);
        Assert.Equal(PlanTaskStatus.InProgress, refreshedA.Status);
        Assert.Equal(3, refreshedA.LinkedDailyCount);

        // Update d1 to 80 -> (80 + 50 + 100) / 3 = 76.666 -> 77
        _repo.UpdateDailyProgress(d1.Id, 80);
        Assert.Equal(77, _repo.GetWeeklyTaskById(weeklyA.Id)!.Progress);

        // Relink d2 from weeklyA to weeklyB -> weeklyA=(80+100)/2=90, weeklyB=50
        _repo.UpdateDailyTask(d2.Id, d2.Title, d2.Notes, d2.Progress, weeklyB.Id);
        Assert.Equal(90, _repo.GetWeeklyTaskById(weeklyA.Id)!.Progress);
        Assert.Equal(50, _repo.GetWeeklyTaskById(weeklyB.Id)!.Progress);

        // Delete d1 and d3 -> weeklyA has 0 linked tasks -> resets to 0
        _repo.DeleteDailyTask(d1.Id);
        _repo.DeleteDailyTask(d3.Id);
        var emptyA = _repo.GetWeeklyTaskById(weeklyA.Id)!;
        Assert.Equal(0, emptyA.Progress);
        Assert.Equal(PlanTaskStatus.NotStarted, emptyA.Status);

        // Manual weekly task throws on direct progress update only when Auto
        Assert.Throws<InvalidOperationException>(() => _repo.UpdateWeeklyProgress(weeklyB.Id, 10));

        // Switching Auto -> Manual allows manual edit; switching back recalculates
        _repo.SetWeeklyProgressMode(weeklyB.Id, WeeklyProgressMode.Manual);
        _repo.UpdateWeeklyProgress(weeklyB.Id, 15);
        Assert.Equal(15, _repo.GetWeeklyTaskById(weeklyB.Id)!.Progress);

        _repo.SetWeeklyProgressMode(weeklyB.Id, WeeklyProgressMode.Auto);
        Assert.Equal(50, _repo.GetWeeklyTaskById(weeklyB.Id)!.Progress);
    }

    [Fact]
    public void DeleteWeeklyTask_ClearsDailyLinkWithoutDeletingDailyTasks()
    {
        var day = new DateOnly(2026, 11, 19);
        var weekly = _repo.CreateWeeklyTask(day, "周目标");
        var daily = _repo.CreateDailyTask(day, "今日任务", progress: 40, weeklyTaskId: weekly.Id);

        _repo.DeleteWeeklyTask(weekly.Id);

        var reloadedDaily = _repo.GetDailyTaskById(daily.Id)!;
        Assert.NotNull(reloadedDaily);
        Assert.Null(reloadedDaily.WeeklyTaskId);
        Assert.Equal(40, reloadedDaily.Progress);
    }

    [Fact]
    public void CarryOverDailyTasks_SameWeekAndCrossWeek_PreservesHistoryAndIsIdempotent()
    {
        var thu = new DateOnly(2026, 11, 19);
        var fri = new DateOnly(2026, 11, 20);
        var nextMon = new DateOnly(2026, 11, 23);

        var weeklyThisWeek = _repo.CreateWeeklyTask(thu, "本周重点", progressMode: WeeklyProgressMode.Auto);
        var doneTask = _repo.CreateDailyTask(thu, "已完成项", progress: 100, weeklyTaskId: weeklyThisWeek.Id);
        var unfinishedLinked = _repo.CreateDailyTask(thu, "未完成关联项", "带备注", progress: 40, weeklyTaskId: weeklyThisWeek.Id);
        var unfinishedStandalone = _repo.CreateDailyTask(thu, "未完成独立项", progress: 0);

        // Same-week carry-over to Friday
        var sameWeekResult = _repo.CarryOverDailyTasks(thu, fri);
        Assert.Equal(2, sameWeekResult.CarriedCount);
        Assert.Equal(0, sameWeekResult.SkippedCount);

        // Idempotent repeat
        var repeatResult = _repo.CarryOverDailyTasks(thu, fri);
        Assert.Equal(0, repeatResult.CarriedCount);
        Assert.Equal(2, repeatResult.SkippedCount);

        // Original Thursday tasks untouched
        var thuTasks = _repo.GetDailyTasks(thu);
        Assert.Equal(3, thuTasks.Count);
        Assert.Contains(thuTasks, t => t.Id == doneTask.Id && t.Progress == 100);

        var friTasks = _repo.GetDailyTasks(fri);
        Assert.Equal(2, friTasks.Count);
        Assert.Contains(friTasks, t => t.Title == unfinishedLinked.Title && t.WeeklyTaskId == weeklyThisWeek.Id && t.Notes == "带备注");
        Assert.Contains(friTasks, t => t.Title == unfinishedStandalone.Title && t.WeeklyTaskId is null);

        // Weekly auto progress reflects linked tasks on Thu & Fri: (100 + 40 + 40)/3 = 60
        Assert.Equal(60, _repo.GetWeeklyTaskById(weeklyThisWeek.Id)!.Progress);

        // Cross-week carry-over WITHOUT carried weekly task clears link so old week is never affected
        var crossNoWeekly = _repo.CarryOverDailyTasks(fri, nextMon);
        Assert.Equal(2, crossNoWeekly.CarriedCount);
        var nextMonTasks = _repo.GetDailyTasks(nextMon);
        Assert.All(nextMonTasks, t => Assert.Null(t.WeeklyTaskId));
        Assert.Equal(60, _repo.GetWeeklyTaskById(weeklyThisWeek.Id)!.Progress);

        // Cross-week carry-over WITH carried weekly task maps link to target week's carried weekly task
        var weeklyCarry = _repo.CarryOverWeeklyTasks(thu, nextMon);
        Assert.Equal(1, weeklyCarry.CarriedCount);
        var nextWeekTasks = _repo.GetWeeklyTasks(nextMon);
        var carriedWeekly = Assert.Single(nextWeekTasks);
        Assert.Equal(weeklyThisWeek.Id, carriedWeekly.CarriedFromId);
        Assert.Equal(0, carriedWeekly.Progress);

        var nextTue = new DateOnly(2026, 11, 24);
        var crossWithWeekly = _repo.CarryOverDailyTasks(fri, nextTue);
        Assert.Equal(2, crossWithWeekly.CarriedCount);
        var nextTueTasks = _repo.GetDailyTasks(nextTue);
        var mappedDaily = Assert.Single(nextTueTasks, t => t.Title == unfinishedLinked.Title);
        Assert.Equal(carriedWeekly.Id, mappedDaily.WeeklyTaskId);
        Assert.Equal(40, _repo.GetWeeklyTaskById(carriedWeekly.Id)!.Progress);
    }

    [Fact]
    public void CarryOverWeeklyTasks_PreservesSourceAndIsIdempotent()
    {
        var week1 = new DateOnly(2026, 11, 16);
        var week2 = new DateOnly(2026, 11, 23);

        _repo.CreateWeeklyTask(week1, "已完成周任务", progress: 100);
        var manualOpen = _repo.CreateWeeklyTask(week1, "手动进行中", "notes", progress: 35);

        var first = _repo.CarryOverWeeklyTasks(week1, week2);
        Assert.Equal(1, first.CarriedCount);
        Assert.Equal(0, first.SkippedCount);

        var second = _repo.CarryOverWeeklyTasks(week1, week2);
        Assert.Equal(0, second.CarriedCount);
        Assert.Equal(1, second.SkippedCount);

        var week2List = _repo.GetWeeklyTasks(week2);
        var carried = Assert.Single(week2List);
        Assert.Equal(manualOpen.Title, carried.Title);
        Assert.Equal(35, carried.Progress);
        Assert.Equal(manualOpen.Id, carried.CarriedFromId);
        Assert.Equal(2, _repo.GetWeeklyTasks(week1).Count);
    }

    [Fact]
    public void SearchTasksAndHistoryPeriods_ReturnExpectedMatches()
    {
        var d1 = new DateOnly(2026, 11, 18);
        var d2 = new DateOnly(2026, 11, 19);

        _repo.CreateWeeklyTask(d1, "架构重构 100%_测试", "关注 SQLite 性能");
        _repo.CreateDailyTask(d1, "修复托盘点击抖动", "包含 100%_edge case", progress: 100);
        _repo.CreateDailyTask(d2, "编写用户手册", "导出 Markdown 报表", progress: 30);

        var sqlMatches = _repo.SearchTasks("SQLite");
        Assert.Single(sqlMatches);
        Assert.Equal(HistoryTaskKind.Weekly, sqlMatches[0].Kind);

        var percentMatches = _repo.SearchTasks("100%_");
        Assert.Equal(2, percentMatches.Count);

        var dailyPeriods = _repo.GetHistoricalDailyPeriods();
        Assert.Equal(2, dailyPeriods.Count);
        Assert.Equal(d2, dailyPeriods[0].PeriodDate);
        Assert.Equal(d1, dailyPeriods[1].PeriodDate);

        var weeklyPeriods = _repo.GetHistoricalWeeklyPeriods();
        Assert.Single(weeklyPeriods);
        Assert.Equal(new DateOnly(2026, 11, 16), weeklyPeriods[0].PeriodDate);
    }
}
