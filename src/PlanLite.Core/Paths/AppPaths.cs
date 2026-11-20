namespace PlanLite.Core.Paths;

/// <summary>
/// Resolves application data storage paths, supporting separated AppData storage by default
/// and portable side-by-side storage when a <c>portable.flag</c> file exists next to the executable.
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagFileName = "portable.flag";
    public const string DatabaseFileName = "planlite.db";
    public const string SettingsFileName = "settings.json";

    public AppPaths(string dataDirectory, bool isPortableMode)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Data directory cannot be empty.", nameof(dataDirectory));
        }

        DataDirectory = Path.GetFullPath(dataDirectory);
        IsPortableMode = isPortableMode;
        DatabasePath = Path.Combine(DataDirectory, DatabaseFileName);
        SettingsPath = Path.Combine(DataDirectory, SettingsFileName);
        LogsDirectory = Path.Combine(DataDirectory, "logs");
    }

    public string DataDirectory { get; }
    public bool IsPortableMode { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }
    public string LogsDirectory { get; }

    public static AppPaths ResolveDefault(string? baseDirectory = null, string? localAppDataDirectory = null)
    {
        var resolvedBaseDir = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        var portableFlagPath = Path.Combine(resolvedBaseDir, PortableFlagFileName);
        if (File.Exists(portableFlagPath))
        {
            var portableDataDir = Path.Combine(resolvedBaseDir, "data");
            return new AppPaths(portableDataDir, isPortableMode: true);
        }

        var appDataRoot = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataDirectory;

        if (string.IsNullOrWhiteSpace(appDataRoot))
        {
            appDataRoot = resolvedBaseDir;
        }

        return new AppPaths(Path.Combine(appDataRoot, "PlanLite"), isPortableMode: false);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
