using Microsoft.Data.Sqlite;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using PlanLite.Core.Paths;
using PlanLite.Core.Services;
using Xunit;

namespace PlanLite.Core.Tests;

public sealed class SettingsAndBackupTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsAndBackupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"planlite-sb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
            // Ignore transient locks.
        }
    }

    [Fact]
    public void AppPaths_ResolvesDefaultVsPortableFlag()
    {
        var baseDir = Path.Combine(_tempDir, "app");
        var localAppData = Path.Combine(_tempDir, "localappdata");
        Directory.CreateDirectory(baseDir);

        var defaultPaths = AppPaths.ResolveDefault(baseDir, localAppData);
        Assert.False(defaultPaths.IsPortableMode);
        Assert.Equal(Path.GetFullPath(Path.Combine(localAppData, "PlanLite")), defaultPaths.DataDirectory);

        File.WriteAllText(Path.Combine(baseDir, AppPaths.PortableFlagFileName), string.Empty);
        var portablePaths = AppPaths.ResolveDefault(baseDir, localAppData);
        Assert.True(portablePaths.IsPortableMode);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "data")), portablePaths.DataDirectory);
    }

    [Fact]
    public void SettingsStore_RoundTripsAndHandlesCorruptJson()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new SettingsStore(settingsPath);

        var initial = store.Load();
        Assert.Equal(AppThemeMode.System, initial.ThemeMode);
        Assert.True(initial.HotkeyEnabled);

        initial.ThemeMode = AppThemeMode.Dark;
        initial.IsWindowPinned = true;
        initial.IsMiniWidgetEnabled = true;
        initial.MiniWidgetLeft = 123.5;
        initial.MiniWidgetTop = 456.25;
        store.Save(initial);

        var reloaded = store.Load();
        Assert.Equal(AppThemeMode.Dark, reloaded.ThemeMode);
        Assert.True(reloaded.IsWindowPinned);
        Assert.True(reloaded.IsMiniWidgetEnabled);
        Assert.Equal(123.5, reloaded.MiniWidgetLeft);
        Assert.Equal(456.25, reloaded.MiniWidgetTop);

        File.WriteAllText(settingsPath, "{ invalid json !!!");
        var fallback = store.Load();
        Assert.Equal(AppThemeMode.System, fallback.ThemeMode);
    }

    [Fact]
    public void BackupAndRestore_RoundTripsDatabaseAndSettingsAndRejectsInvalidArchive()
    {
        var paths = new AppPaths(Path.Combine(_tempDir, "live"), isPortableMode: true);
        paths.EnsureDirectories();
        var db = new SqliteDatabase(paths.DatabasePath);
        db.Initialize();
        var repo = new PlanRepository(db);
        var settingsStore = new SettingsStore(paths.SettingsPath);
        var backupService = new BackupService(paths, db);

        var day = new DateOnly(2026, 11, 19);
        var weekly = repo.CreateWeeklyTask(day, "备份前周目标", progressMode: WeeklyProgressMode.Auto);
        repo.CreateDailyTask(day, "备份前日任务", progress: 80, weeklyTaskId: weekly.Id);
        settingsStore.Save(new AppSettings { ThemeMode = AppThemeMode.Dark, IsWindowPinned = true });

        var backupZip = Path.Combine(_tempDir, "backup.zip");
        backupService.CreateBackup(backupZip);
        Assert.True(File.Exists(backupZip));

        // Mutate live state after backup
        repo.CreateDailyTask(day, "备份后新增任务", progress: 10);
        settingsStore.Save(new AppSettings { ThemeMode = AppThemeMode.Light, IsWindowPinned = false });
        Assert.Equal(2, repo.GetDailyTasks(day).Count);

        // Restore backup
        backupService.RestoreBackup(backupZip);

        var restoredTasks = repo.GetDailyTasks(day);
        var single = Assert.Single(restoredTasks);
        Assert.Equal("备份前日任务", single.Title);
        Assert.Equal(80, single.Progress);

        var restoredSettings = settingsStore.Load();
        Assert.Equal(AppThemeMode.Dark, restoredSettings.ThemeMode);
        Assert.True(restoredSettings.IsWindowPinned);

        // Reject invalid archive without touching live DB
        var badZip = Path.Combine(_tempDir, "invalid.zip");
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, "readme.txt"), "not a db");
        System.IO.Compression.ZipFile.CreateFromDirectory(emptyDir, badZip);

        Assert.Throws<InvalidDataException>(() => backupService.RestoreBackup(badZip));
        Assert.Single(repo.GetDailyTasks(day));
    }
}
