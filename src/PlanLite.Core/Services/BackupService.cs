using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PlanLite.Core.Data;
using PlanLite.Core.Paths;

namespace PlanLite.Core.Services;

public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly SqliteDatabase _database;

    public BackupService(AppPaths paths, SqliteDatabase database)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public void CreateBackup(string destinationZipPath)
    {
        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new ArgumentException("Backup destination path cannot be empty.", nameof(destinationZipPath));
        }

        _paths.EnsureDirectories();
        var fullDestination = Path.GetFullPath(destinationZipPath);
        var destinationDir = Path.GetDirectoryName(fullDestination);
        if (!string.IsNullOrEmpty(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), $"planlite-backup-{Guid.NewGuid():N}");
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"planlite-backup-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(stagingDir);

        try
        {
            var stagedDbPath = Path.Combine(stagingDir, AppPaths.DatabaseFileName);
            using (var sourceConn = _database.OpenConnection())
            using (var backupConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = stagedDbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                backupConn.Open();
                sourceConn.BackupDatabase(backupConn);
            }

            if (File.Exists(_paths.SettingsPath))
            {
                File.Copy(_paths.SettingsPath, Path.Combine(stagingDir, AppPaths.SettingsFileName), overwrite: true);
            }

            var manifest = new BackupManifest
            {
                AppName = "PlanLite",
                SchemaVersion = SqliteDatabase.CurrentSchemaVersion,
                CreatedAtUtc = DateTime.UtcNow
            };
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), manifestJson);

            ZipFile.CreateFromDirectory(stagingDir, tempZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(tempZipPath, fullDestination, overwrite: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(tempZipPath);
            TryDeleteDirectory(stagingDir);
        }
    }

    public void RestoreBackup(string sourceZipPath)
    {
        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("Backup file does not exist.", sourceZipPath);
        }

        _paths.EnsureDirectories();
        var extractDir = Path.Combine(Path.GetTempPath(), $"planlite-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
            ExtractSafely(sourceZipPath, extractDir);

            var extractedDbPath = Path.Combine(extractDir, AppPaths.DatabaseFileName);
            if (!File.Exists(extractedDbPath))
            {
                throw new InvalidDataException("备份文件无效：缺少核心数据库文件 planlite.db。");
            }

            ValidateDatabaseFile(extractedDbPath);

            SqliteConnection.ClearAllPools();

            if (File.Exists(_paths.DatabasePath))
            {
                var preRestorePath = Path.Combine(_paths.DataDirectory, "planlite.pre-restore.db");
                File.Copy(_paths.DatabasePath, preRestorePath, overwrite: true);
            }

            TryDeleteFile($"{_paths.DatabasePath}-wal");
            TryDeleteFile($"{_paths.DatabasePath}-shm");

            File.Copy(extractedDbPath, _paths.DatabasePath, overwrite: true);

            var extractedSettingsPath = Path.Combine(extractDir, AppPaths.SettingsFileName);
            if (File.Exists(extractedSettingsPath))
            {
                File.Copy(extractedSettingsPath, _paths.SettingsPath, overwrite: true);
            }

            _database.Initialize();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(extractDir);
        }
    }

    private static void ExtractSafely(string zipPath, string destinationDir)
    {
        var fullDestRoot = Path.GetFullPath(destinationDir);
        if (!fullDestRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDestRoot += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(fullDestRoot, entry.FullName));
            if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("备份文件包含非法路径条目。");
            }

            var parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void ValidateDatabaseFile(string dbPath)
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(connString);
        conn.Open();

        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA quick_check;";
            var checkResult = checkCmd.ExecuteScalar()?.ToString();
            if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("备份数据库完整性校验失败。");
            }
        }

        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = """
                SELECT COUNT(1)
                FROM sqlite_master
                WHERE type = 'table' AND name IN ('weekly_tasks', 'daily_tasks');
                """;
            var tableCount = Convert.ToInt32(tableCmd.ExecuteScalar());
            if (tableCount < 2)
            {
                throw new InvalidDataException("备份数据库缺少 PlanLite 必需的数据表。");
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failure for transient temp/wal files.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure for temp directory.
        }
    }

    private sealed class BackupManifest
    {
        public string AppName { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
