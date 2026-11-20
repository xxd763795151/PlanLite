using System.Globalization;
using Microsoft.Data.Sqlite;
using PlanLite.Core.Models;
using PlanLite.Core.Utilities;

namespace PlanLite.Core.Data;

public sealed class PlanRepository
{
    private readonly SqliteDatabase _database;

    public PlanRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public static PeriodSummary ComputeSummary(IEnumerable<int> progresses)
    {
        ArgumentNullException.ThrowIfNull(progresses);

        var total = 0;
        var completed = 0;
        var inProgress = 0;
        var notStarted = 0;
        long sum = 0;

        foreach (var raw in progresses)
        {
            var clamped = PlanTaskStatusHelper.ClampProgress(raw);
            total++;
            sum += clamped;

            switch (PlanTaskStatusHelper.FromProgress(clamped))
            {
                case PlanTaskStatus.Completed:
                    completed++;
                    break;
                case PlanTaskStatus.InProgress:
                    inProgress++;
                    break;
                default:
                    notStarted++;
                    break;
            }
        }

        if (total == 0)
        {
            return PeriodSummary.Empty;
        }

        var average = (int)Math.Round((double)sum / total, MidpointRounding.AwayFromZero);
        return new PeriodSummary(total, completed, inProgress, notStarted, average);
    }

    // ========================
    // Weekly Tasks
    // ========================

    public IReadOnlyList<WeeklyTask> GetWeeklyTasks(DateOnly dateInWeek)
    {
        var weekStart = WeekHelper.ToIsoDateString(WeekHelper.GetWeekStart(dateInWeek));
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                w.id,
                w.week_start,
                w.title,
                w.notes,
                w.progress,
                w.status,
                w.progress_mode,
                w.sort_order,
                w.carried_from_id,
                w.created_at,
                w.updated_at,
                w.completed_at,
                (SELECT COUNT(1) FROM daily_tasks d WHERE d.weekly_task_id = w.id) AS linked_daily_count
            FROM weekly_tasks w
            WHERE w.week_start = @weekStart
            ORDER BY w.sort_order ASC, w.id ASC;
            """;
        cmd.Parameters.AddWithValue("@weekStart", weekStart);

        var list = new List<WeeklyTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadWeeklyTask(reader));
        }

        return list;
    }

    public WeeklyTask? GetWeeklyTaskById(long id)
    {
        using var conn = _database.OpenConnection();
        return GetWeeklyTaskById(conn, transaction: null, id);
    }

    public WeeklyTask CreateWeeklyTask(
        DateOnly dateInWeek,
        string title,
        string notes = "",
        int progress = 0,
        WeeklyProgressMode progressMode = WeeklyProgressMode.Manual)
    {
        var cleanTitle = RequireTitle(title);
        var weekStart = WeekHelper.ToIsoDateString(WeekHelper.GetWeekStart(dateInWeek));
        var normalizedProgress = progressMode == WeeklyProgressMode.Auto ? 0 : PlanTaskStatusHelper.ClampProgress(progress);
        var status = PlanTaskStatusHelper.FromProgress(normalizedProgress);
        var now = UtcNowString();
        var completedAt = status == PlanTaskStatus.Completed ? now : null;

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var nextOrder = GetNextSortOrder(conn, tx, "weekly_tasks", "week_start", weekStart);

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO weekly_tasks (
                    week_start, title, notes, progress, status, progress_mode,
                    sort_order, carried_from_id, created_at, updated_at, completed_at
                )
                VALUES (
                    @weekStart, @title, @notes, @progress, @status, @progressMode,
                    @sortOrder, NULL, @now, @now, @completedAt
                );
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@weekStart", weekStart);
            cmd.Parameters.AddWithValue("@title", cleanTitle);
            cmd.Parameters.AddWithValue("@notes", notes?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("@progress", normalizedProgress);
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@progressMode", (int)progressMode);
            cmd.Parameters.AddWithValue("@sortOrder", nextOrder);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }

        tx.Commit();
        return GetWeeklyTaskById(conn, null, id)!;
    }

    public WeeklyTask UpdateWeeklyTask(
        long id,
        string title,
        string notes,
        int progress,
        WeeklyProgressMode progressMode)
    {
        var cleanTitle = RequireTitle(title);
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetWeeklyTaskById(conn, tx, id)
            ?? throw new InvalidOperationException($"Weekly task {id} not found.");

        if (progressMode == WeeklyProgressMode.Auto)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE weekly_tasks
                SET title = @title,
                    notes = @notes,
                    progress_mode = @mode,
                    updated_at = @now
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@title", cleanTitle);
            cmd.Parameters.AddWithValue("@notes", notes?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("@mode", (int)progressMode);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();

            RecalculateWeeklyAutoProgress(conn, tx, id, now);
        }
        else
        {
            var clamped = PlanTaskStatusHelper.ClampProgress(progress);
            var status = PlanTaskStatusHelper.FromProgress(clamped);
            var completedAt = status == PlanTaskStatus.Completed
                ? (existing.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? now)
                : null;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE weekly_tasks
                SET title = @title,
                    notes = @notes,
                    progress = @progress,
                    status = @status,
                    progress_mode = @mode,
                    updated_at = @now,
                    completed_at = @completedAt
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@title", cleanTitle);
            cmd.Parameters.AddWithValue("@notes", notes?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("@progress", clamped);
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@mode", (int)progressMode);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return GetWeeklyTaskById(conn, null, id)!;
    }

    public WeeklyTask UpdateWeeklyProgress(long id, int progress)
    {
        var clamped = PlanTaskStatusHelper.ClampProgress(progress);
        var status = PlanTaskStatusHelper.FromProgress(clamped);
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetWeeklyTaskById(conn, tx, id)
            ?? throw new InvalidOperationException($"Weekly task {id} not found.");

        if (existing.ProgressMode == WeeklyProgressMode.Auto)
        {
            throw new InvalidOperationException("Cannot manually update progress of an Auto-calculated weekly task.");
        }

        var completedAt = status == PlanTaskStatus.Completed
            ? (existing.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? now)
            : null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE weekly_tasks
                SET progress = @progress,
                    status = @status,
                    updated_at = @now,
                    completed_at = @completedAt
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@progress", clamped);
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return GetWeeklyTaskById(conn, null, id)!;
    }

    public WeeklyTask SetWeeklyProgressMode(long id, WeeklyProgressMode mode)
    {
        var now = UtcNowString();
        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        _ = GetWeeklyTaskById(conn, tx, id)
            ?? throw new InvalidOperationException($"Weekly task {id} not found.");

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE weekly_tasks
                SET progress_mode = @mode,
                    updated_at = @now
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@mode", (int)mode);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        if (mode == WeeklyProgressMode.Auto)
        {
            RecalculateWeeklyAutoProgress(conn, tx, id, now);
        }

        tx.Commit();
        return GetWeeklyTaskById(conn, null, id)!;
    }

    public void DeleteWeeklyTask(long id)
    {
        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var clearLinks = conn.CreateCommand())
        {
            clearLinks.Transaction = tx;
            clearLinks.CommandText = "UPDATE daily_tasks SET weekly_task_id = NULL WHERE weekly_task_id = @id;";
            clearLinks.Parameters.AddWithValue("@id", id);
            clearLinks.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM weekly_tasks WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void ReorderWeeklyTasks(DateOnly dateInWeek, IReadOnlyList<long> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var weekStart = WeekHelper.ToIsoDateString(WeekHelper.GetWeekStart(dateInWeek));

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        ReorderPeriodItems(conn, tx, "weekly_tasks", "week_start", weekStart, orderedIds);
        tx.Commit();
    }

    public CarryOverResult CarryOverWeeklyTasks(DateOnly fromDateInWeek, DateOnly toDateInWeek)
    {
        var fromWeek = WeekHelper.GetWeekStart(fromDateInWeek);
        var toWeek = WeekHelper.GetWeekStart(toDateInWeek);
        if (fromWeek == toWeek)
        {
            return new CarryOverResult(0, 0);
        }

        var fromStr = WeekHelper.ToIsoDateString(fromWeek);
        var toStr = WeekHelper.ToIsoDateString(toWeek);
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existingCarriedIds = new HashSet<long>();
        using (var existingCmd = conn.CreateCommand())
        {
            existingCmd.Transaction = tx;
            existingCmd.CommandText = """
                SELECT carried_from_id
                FROM weekly_tasks
                WHERE week_start = @toWeek AND carried_from_id IS NOT NULL;
                """;
            existingCmd.Parameters.AddWithValue("@toWeek", toStr);
            using var reader = existingCmd.ExecuteReader();
            while (reader.Read())
            {
                existingCarriedIds.Add(reader.GetInt64(0));
            }
        }

        var sourceTasks = new List<WeeklyTask>();
        using (var sourceCmd = conn.CreateCommand())
        {
            sourceCmd.Transaction = tx;
            sourceCmd.CommandText = """
                SELECT
                    w.id, w.week_start, w.title, w.notes, w.progress, w.status, w.progress_mode,
                    w.sort_order, w.carried_from_id, w.created_at, w.updated_at, w.completed_at, 0
                FROM weekly_tasks w
                WHERE w.week_start = @fromWeek AND w.progress < 100
                ORDER BY w.sort_order ASC, w.id ASC;
                """;
            sourceCmd.Parameters.AddWithValue("@fromWeek", fromStr);
            using var reader = sourceCmd.ExecuteReader();
            while (reader.Read())
            {
                sourceTasks.Add(ReadWeeklyTask(reader));
            }
        }

        var nextOrder = GetNextSortOrder(conn, tx, "weekly_tasks", "week_start", toStr);
        var carried = 0;
        var skipped = 0;

        foreach (var src in sourceTasks)
        {
            if (existingCarriedIds.Contains(src.Id))
            {
                skipped++;
                continue;
            }

            var targetProgress = src.ProgressMode == WeeklyProgressMode.Auto ? 0 : src.Progress;
            var targetStatus = PlanTaskStatusHelper.FromProgress(targetProgress);

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO weekly_tasks (
                    week_start, title, notes, progress, status, progress_mode,
                    sort_order, carried_from_id, created_at, updated_at, completed_at
                )
                VALUES (
                    @toWeek, @title, @notes, @progress, @status, @mode,
                    @sortOrder, @carriedFromId, @now, @now, NULL
                );
                """;
            insertCmd.Parameters.AddWithValue("@toWeek", toStr);
            insertCmd.Parameters.AddWithValue("@title", src.Title);
            insertCmd.Parameters.AddWithValue("@notes", src.Notes);
            insertCmd.Parameters.AddWithValue("@progress", targetProgress);
            insertCmd.Parameters.AddWithValue("@status", (int)targetStatus);
            insertCmd.Parameters.AddWithValue("@mode", (int)src.ProgressMode);
            insertCmd.Parameters.AddWithValue("@sortOrder", nextOrder++);
            insertCmd.Parameters.AddWithValue("@carriedFromId", src.Id);
            insertCmd.Parameters.AddWithValue("@now", now);
            insertCmd.ExecuteNonQuery();

            carried++;
        }

        tx.Commit();
        return new CarryOverResult(carried, skipped);
    }

    // ========================
    // Daily Tasks
    // ========================

    public IReadOnlyList<DailyTask> GetDailyTasks(DateOnly planDate)
    {
        var dateStr = WeekHelper.ToIsoDateString(planDate);
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                d.id,
                d.plan_date,
                d.weekly_task_id,
                w.title AS weekly_task_title,
                d.title,
                d.notes,
                d.progress,
                d.status,
                d.last_non_complete_progress,
                d.sort_order,
                d.carried_from_id,
                d.created_at,
                d.updated_at,
                d.completed_at
            FROM daily_tasks d
            LEFT JOIN weekly_tasks w ON w.id = d.weekly_task_id
            WHERE d.plan_date = @planDate
            ORDER BY d.sort_order ASC, d.id ASC;
            """;
        cmd.Parameters.AddWithValue("@planDate", dateStr);

        var list = new List<DailyTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadDailyTask(reader));
        }

        return list;
    }

    public IReadOnlyList<DailyTask> GetDailyTasksByWeek(DateOnly dateInWeek)
    {
        var start = WeekHelper.GetWeekStart(dateInWeek);
        var end = start.AddDays(6);

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                d.id,
                d.plan_date,
                d.weekly_task_id,
                w.title AS weekly_task_title,
                d.title,
                d.notes,
                d.progress,
                d.status,
                d.last_non_complete_progress,
                d.sort_order,
                d.carried_from_id,
                d.created_at,
                d.updated_at,
                d.completed_at
            FROM daily_tasks d
            LEFT JOIN weekly_tasks w ON w.id = d.weekly_task_id
            WHERE d.plan_date >= @start AND d.plan_date <= @end
            ORDER BY d.plan_date ASC, d.sort_order ASC, d.id ASC;
            """;
        cmd.Parameters.AddWithValue("@start", WeekHelper.ToIsoDateString(start));
        cmd.Parameters.AddWithValue("@end", WeekHelper.ToIsoDateString(end));

        var list = new List<DailyTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadDailyTask(reader));
        }

        return list;
    }

    public IReadOnlyList<DailyTask> GetDailyTasksByWeeklyTaskId(long weeklyTaskId)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                d.id,
                d.plan_date,
                d.weekly_task_id,
                w.title AS weekly_task_title,
                d.title,
                d.notes,
                d.progress,
                d.status,
                d.last_non_complete_progress,
                d.sort_order,
                d.carried_from_id,
                d.created_at,
                d.updated_at,
                d.completed_at
            FROM daily_tasks d
            LEFT JOIN weekly_tasks w ON w.id = d.weekly_task_id
            WHERE d.weekly_task_id = @weeklyTaskId
            ORDER BY d.plan_date ASC, d.sort_order ASC, d.id ASC;
            """;
        cmd.Parameters.AddWithValue("@weeklyTaskId", weeklyTaskId);

        var list = new List<DailyTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadDailyTask(reader));
        }

        return list;
    }

    public DailyTask? GetDailyTaskById(long id)
    {
        using var conn = _database.OpenConnection();
        return GetDailyTaskById(conn, transaction: null, id);
    }

    public DailyTask CreateDailyTask(
        DateOnly planDate,
        string title,
        string notes = "",
        int progress = 0,
        long? weeklyTaskId = null)
    {
        var cleanTitle = RequireTitle(title);
        var dateStr = WeekHelper.ToIsoDateString(planDate);
        var clamped = PlanTaskStatusHelper.ClampProgress(progress);
        var status = PlanTaskStatusHelper.FromProgress(clamped);
        var lastNonComplete = clamped < 100 ? clamped : 0;
        var now = UtcNowString();
        var completedAt = status == PlanTaskStatus.Completed ? now : null;

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var resolvedWeeklyId = ValidateWeeklyTaskLink(conn, tx, weeklyTaskId);
        var nextOrder = GetNextSortOrder(conn, tx, "daily_tasks", "plan_date", dateStr);

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO daily_tasks (
                    plan_date, weekly_task_id, title, notes, progress, status,
                    last_non_complete_progress, sort_order, carried_from_id,
                    created_at, updated_at, completed_at
                )
                VALUES (
                    @planDate, @weeklyTaskId, @title, @notes, @progress, @status,
                    @lastNonComplete, @sortOrder, NULL,
                    @now, @now, @completedAt
                );
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@planDate", dateStr);
            cmd.Parameters.AddWithValue("@weeklyTaskId", (object?)resolvedWeeklyId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", cleanTitle);
            cmd.Parameters.AddWithValue("@notes", notes?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("@progress", clamped);
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@lastNonComplete", lastNonComplete);
            cmd.Parameters.AddWithValue("@sortOrder", nextOrder);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }

        if (resolvedWeeklyId.HasValue)
        {
            RecalculateWeeklyAutoProgress(conn, tx, resolvedWeeklyId.Value, now);
        }

        tx.Commit();
        return GetDailyTaskById(conn, null, id)!;
    }

    public DailyTask UpdateDailyTask(
        long id,
        string title,
        string notes,
        int progress,
        long? weeklyTaskId)
    {
        var cleanTitle = RequireTitle(title);
        var clamped = PlanTaskStatusHelper.ClampProgress(progress);
        var status = PlanTaskStatusHelper.FromProgress(clamped);
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetDailyTaskById(conn, tx, id)
            ?? throw new InvalidOperationException($"Daily task {id} not found.");

        var resolvedWeeklyId = ValidateWeeklyTaskLink(conn, tx, weeklyTaskId);
        var lastNonComplete = clamped < 100
            ? clamped
            : Math.Clamp(existing.LastNonCompleteProgress, 0, 99);
        var completedAt = status == PlanTaskStatus.Completed
            ? (existing.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? now)
            : null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE daily_tasks
                SET weekly_task_id = @weeklyTaskId,
                    title = @title,
                    notes = @notes,
                    progress = @progress,
                    status = @status,
                    last_non_complete_progress = @lastNonComplete,
                    updated_at = @now,
                    completed_at = @completedAt
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@weeklyTaskId", (object?)resolvedWeeklyId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", cleanTitle);
            cmd.Parameters.AddWithValue("@notes", notes?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("@progress", clamped);
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@lastNonComplete", lastNonComplete);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        if (existing.WeeklyTaskId.HasValue)
        {
            RecalculateWeeklyAutoProgress(conn, tx, existing.WeeklyTaskId.Value, now);
        }

        if (resolvedWeeklyId.HasValue && resolvedWeeklyId != existing.WeeklyTaskId)
        {
            RecalculateWeeklyAutoProgress(conn, tx, resolvedWeeklyId.Value, now);
        }

        tx.Commit();
        return GetDailyTaskById(conn, null, id)!;
    }

    public DailyTask UpdateDailyProgress(long id, int progress)
    {
        var clamped = PlanTaskStatusHelper.ClampProgress(progress);
        var status = PlanTaskStatusHelper.FromProgress(clamped);
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetDailyTaskById(conn, tx, id)
            ?? throw new InvalidOperationException($"Daily task {id} not found.");

        var lastNonComplete = clamped < 100
            ? clamped
            : (existing.Progress < 100 ? existing.Progress : Math.Clamp(existing.LastNonCompleteProgress, 0, 99));
        var completedAt = status == PlanTaskStatus.Completed
            ? (existing.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? now)
            : null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE daily_tasks
                SET progress = @progress,
                    status = @status,
                    last_non_complete_progress = @lastNonComplete,
                    updated_at = @now,
                    completed_at = @completedAt
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@progress", clamped);
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@lastNonComplete", lastNonComplete);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        if (existing.WeeklyTaskId.HasValue)
        {
            RecalculateWeeklyAutoProgress(conn, tx, existing.WeeklyTaskId.Value, now);
        }

        tx.Commit();
        return GetDailyTaskById(conn, null, id)!;
    }

    public DailyTask ToggleDailyComplete(long id)
    {
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetDailyTaskById(conn, tx, id)
            ?? throw new InvalidOperationException($"Daily task {id} not found.");

        int newProgress;
        int newLastNonComplete;
        if (existing.Progress == 100)
        {
            newProgress = Math.Clamp(existing.LastNonCompleteProgress, 0, 99);
            newLastNonComplete = newProgress;
        }
        else
        {
            newLastNonComplete = Math.Clamp(existing.Progress, 0, 99);
            newProgress = 100;
        }

        var status = PlanTaskStatusHelper.FromProgress(newProgress);
        var completedAt = status == PlanTaskStatus.Completed ? now : null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE daily_tasks
                SET progress = @progress,
                    status = @status,
                    last_non_complete_progress = @lastNonComplete,
                    updated_at = @now,
                    completed_at = @completedAt
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@progress", newProgress);
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@lastNonComplete", newLastNonComplete);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        if (existing.WeeklyTaskId.HasValue)
        {
            RecalculateWeeklyAutoProgress(conn, tx, existing.WeeklyTaskId.Value, now);
        }

        tx.Commit();
        return GetDailyTaskById(conn, null, id)!;
    }

    public void DeleteDailyTask(long id)
    {
        var now = UtcNowString();
        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetDailyTaskById(conn, tx, id);
        if (existing is null)
        {
            return;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM daily_tasks WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        if (existing.WeeklyTaskId.HasValue)
        {
            RecalculateWeeklyAutoProgress(conn, tx, existing.WeeklyTaskId.Value, now);
        }

        tx.Commit();
    }

    public void ReorderDailyTasks(DateOnly planDate, IReadOnlyList<long> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var dateStr = WeekHelper.ToIsoDateString(planDate);

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        ReorderPeriodItems(conn, tx, "daily_tasks", "plan_date", dateStr, orderedIds);
        tx.Commit();
    }

    public CarryOverResult CarryOverDailyTasks(DateOnly fromDate, DateOnly toDate)
    {
        if (fromDate == toDate)
        {
            return new CarryOverResult(0, 0);
        }

        var fromStr = WeekHelper.ToIsoDateString(fromDate);
        var toStr = WeekHelper.ToIsoDateString(toDate);
        var fromWeek = WeekHelper.GetWeekStart(fromDate);
        var toWeek = WeekHelper.GetWeekStart(toDate);
        var sameWeek = fromWeek == toWeek;
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existingCarriedIds = new HashSet<long>();
        using (var existingCmd = conn.CreateCommand())
        {
            existingCmd.Transaction = tx;
            existingCmd.CommandText = """
                SELECT carried_from_id
                FROM daily_tasks
                WHERE plan_date = @toDate AND carried_from_id IS NOT NULL;
                """;
            existingCmd.Parameters.AddWithValue("@toDate", toStr);
            using var reader = existingCmd.ExecuteReader();
            while (reader.Read())
            {
                existingCarriedIds.Add(reader.GetInt64(0));
            }
        }

        var crossWeekMap = new Dictionary<long, long>();
        if (!sameWeek)
        {
            using var mapCmd = conn.CreateCommand();
            mapCmd.Transaction = tx;
            mapCmd.CommandText = """
                SELECT id, carried_from_id
                FROM weekly_tasks
                WHERE week_start = @toWeek AND carried_from_id IS NOT NULL;
                """;
            mapCmd.Parameters.AddWithValue("@toWeek", WeekHelper.ToIsoDateString(toWeek));
            using var reader = mapCmd.ExecuteReader();
            while (reader.Read())
            {
                var targetWeeklyId = reader.GetInt64(0);
                var sourceWeeklyId = reader.GetInt64(1);
                crossWeekMap[sourceWeeklyId] = targetWeeklyId;
            }
        }

        var sourceTasks = new List<DailyTask>();
        using (var sourceCmd = conn.CreateCommand())
        {
            sourceCmd.Transaction = tx;
            sourceCmd.CommandText = """
                SELECT
                    d.id, d.plan_date, d.weekly_task_id, NULL, d.title, d.notes, d.progress,
                    d.status, d.last_non_complete_progress, d.sort_order, d.carried_from_id,
                    d.created_at, d.updated_at, d.completed_at
                FROM daily_tasks d
                WHERE d.plan_date = @fromDate AND d.progress < 100
                ORDER BY d.sort_order ASC, d.id ASC;
                """;
            sourceCmd.Parameters.AddWithValue("@fromDate", fromStr);
            using var reader = sourceCmd.ExecuteReader();
            while (reader.Read())
            {
                sourceTasks.Add(ReadDailyTask(reader));
            }
        }

        var nextOrder = GetNextSortOrder(conn, tx, "daily_tasks", "plan_date", toStr);
        var affectedWeeklyIds = new HashSet<long>();
        var carried = 0;
        var skipped = 0;

        foreach (var src in sourceTasks)
        {
            if (existingCarriedIds.Contains(src.Id))
            {
                skipped++;
                continue;
            }

            long? targetWeeklyId = null;
            if (src.WeeklyTaskId.HasValue)
            {
                if (sameWeek)
                {
                    targetWeeklyId = ValidateWeeklyTaskLink(conn, tx, src.WeeklyTaskId);
                }
                else if (crossWeekMap.TryGetValue(src.WeeklyTaskId.Value, out var mappedId))
                {
                    targetWeeklyId = mappedId;
                }
            }

            var clamped = PlanTaskStatusHelper.ClampProgress(src.Progress);
            var status = PlanTaskStatusHelper.FromProgress(clamped);

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO daily_tasks (
                    plan_date, weekly_task_id, title, notes, progress, status,
                    last_non_complete_progress, sort_order, carried_from_id,
                    created_at, updated_at, completed_at
                )
                VALUES (
                    @toDate, @weeklyTaskId, @title, @notes, @progress, @status,
                    @lastNonComplete, @sortOrder, @carriedFromId,
                    @now, @now, NULL
                );
                """;
            insertCmd.Parameters.AddWithValue("@toDate", toStr);
            insertCmd.Parameters.AddWithValue("@weeklyTaskId", (object?)targetWeeklyId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@title", src.Title);
            insertCmd.Parameters.AddWithValue("@notes", src.Notes);
            insertCmd.Parameters.AddWithValue("@progress", clamped);
            insertCmd.Parameters.AddWithValue("@status", (int)status);
            insertCmd.Parameters.AddWithValue("@lastNonComplete", Math.Clamp(src.LastNonCompleteProgress, 0, 99));
            insertCmd.Parameters.AddWithValue("@sortOrder", nextOrder++);
            insertCmd.Parameters.AddWithValue("@carriedFromId", src.Id);
            insertCmd.Parameters.AddWithValue("@now", now);
            insertCmd.ExecuteNonQuery();

            if (targetWeeklyId.HasValue)
            {
                affectedWeeklyIds.Add(targetWeeklyId.Value);
            }

            carried++;
        }

        foreach (var weeklyId in affectedWeeklyIds)
        {
            RecalculateWeeklyAutoProgress(conn, tx, weeklyId, now);
        }

        tx.Commit();
        return new CarryOverResult(carried, skipped);
    }

    // ========================
    // History & Search (On-Demand)
    // ========================

    public IReadOnlyList<HistorySearchResult> SearchTasks(string keyword, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Array.Empty<HistorySearchResult>();
        }

        var escaped = EscapeLikePattern(keyword.Trim());
        var pattern = $"%{escaped}%";
        var effectiveLimit = Math.Clamp(limit, 1, 500);

        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT kind, task_id, period_date, title, notes, progress, status, linked_weekly_title
            FROM (
                SELECT
                    0 AS kind,
                    d.id AS task_id,
                    d.plan_date AS period_date,
                    d.title AS title,
                    d.notes AS notes,
                    d.progress AS progress,
                    d.status AS status,
                    w.title AS linked_weekly_title
                FROM daily_tasks d
                LEFT JOIN weekly_tasks w ON w.id = d.weekly_task_id
                WHERE d.title LIKE @pattern ESCAPE '\' OR d.notes LIKE @pattern ESCAPE '\'

                UNION ALL

                SELECT
                    1 AS kind,
                    w.id AS task_id,
                    w.week_start AS period_date,
                    w.title AS title,
                    w.notes AS notes,
                    w.progress AS progress,
                    w.status AS status,
                    NULL AS linked_weekly_title
                FROM weekly_tasks w
                WHERE w.title LIKE @pattern ESCAPE '\' OR w.notes LIKE @pattern ESCAPE '\'
            )
            ORDER BY period_date DESC, kind ASC, task_id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@pattern", pattern);
        cmd.Parameters.AddWithValue("@limit", effectiveLimit);

        var results = new List<HistorySearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HistorySearchResult
            {
                Kind = (HistoryTaskKind)reader.GetInt32(0),
                TaskId = reader.GetInt64(1),
                PeriodDate = WeekHelper.ParseIsoDateString(reader.GetString(2)),
                Title = reader.GetString(3),
                Notes = reader.GetString(4),
                Progress = reader.GetInt32(5),
                Status = (PlanTaskStatus)reader.GetInt32(6),
                LinkedWeeklyTitle = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return results;
    }

    public IReadOnlyList<HistoryPeriodItem> GetHistoricalDailyPeriods(int limit = 60, int offset = 0)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                plan_date,
                COUNT(1) AS total_count,
                SUM(CASE WHEN progress = 100 THEN 1 ELSE 0 END) AS completed_count,
                CAST(ROUND(AVG(progress)) AS INTEGER) AS avg_progress
            FROM daily_tasks
            GROUP BY plan_date
            ORDER BY plan_date DESC
            LIMIT @limit OFFSET @offset;
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, offset));

        var list = new List<HistoryPeriodItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HistoryPeriodItem
            {
                Kind = HistoryTaskKind.Daily,
                PeriodDate = WeekHelper.ParseIsoDateString(reader.GetString(0)),
                TotalCount = reader.GetInt32(1),
                CompletedCount = reader.GetInt32(2),
                AverageProgress = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
            });
        }

        return list;
    }

    public IReadOnlyList<HistoryPeriodItem> GetHistoricalWeeklyPeriods(int limit = 26, int offset = 0)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                week_start,
                COUNT(1) AS total_count,
                SUM(CASE WHEN progress = 100 THEN 1 ELSE 0 END) AS completed_count,
                CAST(ROUND(AVG(progress)) AS INTEGER) AS avg_progress
            FROM weekly_tasks
            GROUP BY week_start
            ORDER BY week_start DESC
            LIMIT @limit OFFSET @offset;
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, offset));

        var list = new List<HistoryPeriodItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HistoryPeriodItem
            {
                Kind = HistoryTaskKind.Weekly,
                PeriodDate = WeekHelper.ParseIsoDateString(reader.GetString(0)),
                TotalCount = reader.GetInt32(1),
                CompletedCount = reader.GetInt32(2),
                AverageProgress = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
            });
        }

        return list;
    }

    // ========================
    // Internal Helpers
    // ========================

    private static WeeklyTask? GetWeeklyTaskById(SqliteConnection conn, SqliteTransaction? transaction, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT
                w.id,
                w.week_start,
                w.title,
                w.notes,
                w.progress,
                w.status,
                w.progress_mode,
                w.sort_order,
                w.carried_from_id,
                w.created_at,
                w.updated_at,
                w.completed_at,
                (SELECT COUNT(1) FROM daily_tasks d WHERE d.weekly_task_id = w.id) AS linked_daily_count
            FROM weekly_tasks w
            WHERE w.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadWeeklyTask(reader) : null;
    }

    private static DailyTask? GetDailyTaskById(SqliteConnection conn, SqliteTransaction? transaction, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT
                d.id,
                d.plan_date,
                d.weekly_task_id,
                w.title AS weekly_task_title,
                d.title,
                d.notes,
                d.progress,
                d.status,
                d.last_non_complete_progress,
                d.sort_order,
                d.carried_from_id,
                d.created_at,
                d.updated_at,
                d.completed_at
            FROM daily_tasks d
            LEFT JOIN weekly_tasks w ON w.id = d.weekly_task_id
            WHERE d.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDailyTask(reader) : null;
    }

    private static void RecalculateWeeklyAutoProgress(
        SqliteConnection conn,
        SqliteTransaction tx,
        long weeklyTaskId,
        string nowUtc)
    {
        int mode;
        string? existingCompletedAt;
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT progress_mode, completed_at FROM weekly_tasks WHERE id = @id;";
            checkCmd.Parameters.AddWithValue("@id", weeklyTaskId);
            using var reader = checkCmd.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }

            mode = reader.GetInt32(0);
            existingCompletedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if ((WeeklyProgressMode)mode != WeeklyProgressMode.Auto)
        {
            return;
        }

        int avgProgress = 0;
        using (var avgCmd = conn.CreateCommand())
        {
            avgCmd.Transaction = tx;
            avgCmd.CommandText = "SELECT AVG(progress) FROM daily_tasks WHERE weekly_task_id = @id;";
            avgCmd.Parameters.AddWithValue("@id", weeklyTaskId);
            var scalar = avgCmd.ExecuteScalar();
            if (scalar is not null && scalar != DBNull.Value)
            {
                avgProgress = PlanTaskStatusHelper.ClampProgress(
                    (int)Math.Round(Convert.ToDouble(scalar, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero));
            }
        }

        var status = PlanTaskStatusHelper.FromProgress(avgProgress);
        var completedAt = status == PlanTaskStatus.Completed
            ? (existingCompletedAt ?? nowUtc)
            : null;

        using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = """
            UPDATE weekly_tasks
            SET progress = @progress,
                status = @status,
                updated_at = @now,
                completed_at = @completedAt
            WHERE id = @id;
            """;
        updateCmd.Parameters.AddWithValue("@progress", avgProgress);
        updateCmd.Parameters.AddWithValue("@status", (int)status);
        updateCmd.Parameters.AddWithValue("@now", nowUtc);
        updateCmd.Parameters.AddWithValue("@completedAt", (object?)completedAt ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@id", weeklyTaskId);
        updateCmd.ExecuteNonQuery();
    }

    private static long? ValidateWeeklyTaskLink(SqliteConnection conn, SqliteTransaction tx, long? weeklyTaskId)
    {
        if (!weeklyTaskId.HasValue)
        {
            return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(1) FROM weekly_tasks WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", weeklyTaskId.Value);
        var exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        return exists ? weeklyTaskId.Value : null;
    }

    private static int GetNextSortOrder(
        SqliteConnection conn,
        SqliteTransaction tx,
        string tableName,
        string periodColumn,
        string periodValue)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COALESCE(MAX(sort_order), -1) + 1 FROM {tableName} WHERE {periodColumn} = @period;";
        cmd.Parameters.AddWithValue("@period", periodValue);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ReorderPeriodItems(
        SqliteConnection conn,
        SqliteTransaction tx,
        string tableName,
        string periodColumn,
        string periodValue,
        IReadOnlyList<long> orderedIds)
    {
        var existingIds = new List<long>();
        using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = $"SELECT id FROM {tableName} WHERE {periodColumn} = @period ORDER BY sort_order ASC, id ASC;";
            selectCmd.Parameters.AddWithValue("@period", periodValue);
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                existingIds.Add(reader.GetInt64(0));
            }
        }

        var existingSet = existingIds.ToHashSet();
        var finalOrder = new List<long>(existingIds.Count);
        var seen = new HashSet<long>();

        foreach (var id in orderedIds)
        {
            if (existingSet.Contains(id) && seen.Add(id))
            {
                finalOrder.Add(id);
            }
        }

        foreach (var id in existingIds)
        {
            if (seen.Add(id))
            {
                finalOrder.Add(id);
            }
        }

        using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = $"UPDATE {tableName} SET sort_order = @sortOrder WHERE id = @id;";
        var orderParam = updateCmd.Parameters.Add("@sortOrder", SqliteType.Integer);
        var idParam = updateCmd.Parameters.Add("@id", SqliteType.Integer);

        for (var i = 0; i < finalOrder.Count; i++)
        {
            orderParam.Value = i;
            idParam.Value = finalOrder[i];
            updateCmd.ExecuteNonQuery();
        }
    }

    private static WeeklyTask ReadWeeklyTask(SqliteDataReader reader)
    {
        return new WeeklyTask
        {
            Id = reader.GetInt64(0),
            WeekStart = WeekHelper.ParseIsoDateString(reader.GetString(1)),
            Title = reader.GetString(2),
            Notes = reader.GetString(3),
            Progress = reader.GetInt32(4),
            Status = (PlanTaskStatus)reader.GetInt32(5),
            ProgressMode = (WeeklyProgressMode)reader.GetInt32(6),
            SortOrder = reader.GetInt32(7),
            CarriedFromId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            CreatedAtUtc = ParseUtc(reader.GetString(9)),
            UpdatedAtUtc = ParseUtc(reader.GetString(10)),
            CompletedAtUtc = reader.IsDBNull(11) ? null : ParseUtc(reader.GetString(11)),
            LinkedDailyCount = reader.GetInt32(12)
        };
    }

    private static DailyTask ReadDailyTask(SqliteDataReader reader)
    {
        return new DailyTask
        {
            Id = reader.GetInt64(0),
            PlanDate = WeekHelper.ParseIsoDateString(reader.GetString(1)),
            WeeklyTaskId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            WeeklyTaskTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
            Title = reader.GetString(4),
            Notes = reader.GetString(5),
            Progress = reader.GetInt32(6),
            Status = (PlanTaskStatus)reader.GetInt32(7),
            LastNonCompleteProgress = reader.GetInt32(8),
            SortOrder = reader.GetInt32(9),
            CarriedFromId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            CreatedAtUtc = ParseUtc(reader.GetString(11)),
            UpdatedAtUtc = ParseUtc(reader.GetString(12)),
            CompletedAtUtc = reader.IsDBNull(13) ? null : ParseUtc(reader.GetString(13))
        };
    }

    private static string RequireTitle(string title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("任务标题不能为空。", nameof(title));
        }

        return trimmed;
    }

    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static string UtcNowString() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
