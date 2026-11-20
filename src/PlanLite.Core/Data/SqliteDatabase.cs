using Microsoft.Data.Sqlite;

namespace PlanLite.Core.Data;

public sealed class SqliteDatabase
{
    public const int CurrentSchemaVersion = 1;

    public SqliteDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }
    public string ConnectionString { get; }

    public SqliteConnection OpenConnection()
    {
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 3000;
            """;
        pragmaCmd.ExecuteNonQuery();

        return connection;
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS weekly_tasks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    week_start TEXT NOT NULL,
                    title TEXT NOT NULL,
                    notes TEXT NOT NULL DEFAULT '',
                    progress INTEGER NOT NULL DEFAULT 0,
                    status INTEGER NOT NULL DEFAULT 0,
                    progress_mode INTEGER NOT NULL DEFAULT 0,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    carried_from_id INTEGER NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    completed_at TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_weekly_tasks_week_start
                    ON weekly_tasks(week_start, sort_order, id);

                CREATE TABLE IF NOT EXISTS daily_tasks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    plan_date TEXT NOT NULL,
                    weekly_task_id INTEGER NULL REFERENCES weekly_tasks(id) ON DELETE SET NULL,
                    title TEXT NOT NULL,
                    notes TEXT NOT NULL DEFAULT '',
                    progress INTEGER NOT NULL DEFAULT 0,
                    status INTEGER NOT NULL DEFAULT 0,
                    last_non_complete_progress INTEGER NOT NULL DEFAULT 0,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    carried_from_id INTEGER NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    completed_at TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_daily_tasks_plan_date
                    ON daily_tasks(plan_date, sort_order, id);

                CREATE INDEX IF NOT EXISTS idx_daily_tasks_weekly_task_id
                    ON daily_tasks(weekly_task_id);

                INSERT OR REPLACE INTO schema_meta (key, value)
                VALUES ('schema_version', @version);
                """;
            cmd.Parameters.AddWithValue("@version", CurrentSchemaVersion.ToString());
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
