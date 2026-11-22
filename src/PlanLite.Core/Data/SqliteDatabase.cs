using Microsoft.Data.Sqlite;

namespace PlanLite.Core.Data;

public sealed class SqliteDatabase
{
    public const int CurrentSchemaVersion = 2;

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

                CREATE TABLE IF NOT EXISTS note_categories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_note_categories_sort
                    ON note_categories(sort_order, id);

                CREATE TABLE IF NOT EXISTS notes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    category_id INTEGER NULL REFERENCES note_categories(id) ON DELETE SET NULL,
                    title TEXT NOT NULL,
                    content TEXT NOT NULL DEFAULT '',
                    is_pinned INTEGER NOT NULL DEFAULT 0,
                    is_masked INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_notes_browse
                    ON notes(is_pinned DESC, updated_at DESC, id DESC);

                CREATE INDEX IF NOT EXISTS idx_notes_category_browse
                    ON notes(category_id, is_pinned DESC, updated_at DESC, id DESC);

                INSERT OR REPLACE INTO schema_meta (key, value)
                VALUES ('schema_version', @version);
                """;
            cmd.Parameters.AddWithValue("@version", CurrentSchemaVersion.ToString());
            cmd.ExecuteNonQuery();
        }

        SeedDefaultNoteCategoriesIfNeeded(connection, tx);

        tx.Commit();
    }

    private static void SeedDefaultNoteCategoriesIfNeeded(SqliteConnection connection, SqliteTransaction tx)
    {
        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT value FROM schema_meta WHERE key = 'notes_default_categories_seeded';";
            var flag = checkCmd.ExecuteScalar()?.ToString();
            if (string.Equals(flag, "1", StringComparison.Ordinal))
            {
                return;
            }
        }

        var now = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var defaults = new (string Name, int SortOrder)[]
        {
            ("账号", 10),
            ("脚本", 20),
            ("链接", 30)
        };

        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT OR IGNORE INTO note_categories (name, sort_order, created_at)
                VALUES (@name, @sortOrder, @createdAt);
                """;
            var nameParam = insertCmd.Parameters.Add("@name", SqliteType.Text);
            var sortParam = insertCmd.Parameters.Add("@sortOrder", SqliteType.Integer);
            insertCmd.Parameters.AddWithValue("@createdAt", now);

            foreach (var (name, sortOrder) in defaults)
            {
                nameParam.Value = name;
                sortParam.Value = sortOrder;
                insertCmd.ExecuteNonQuery();
            }
        }

        using (var markCmd = connection.CreateCommand())
        {
            markCmd.Transaction = tx;
            markCmd.CommandText = """
                INSERT OR REPLACE INTO schema_meta (key, value)
                VALUES ('notes_default_categories_seeded', '1');
                """;
            markCmd.ExecuteNonQuery();
        }
    }
}
