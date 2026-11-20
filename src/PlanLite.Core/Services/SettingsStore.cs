using System.Text.Json;
using System.Text.Json.Serialization;
using PlanLite.Core.Models;

namespace PlanLite.Core.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private readonly object _sync = new();

    public SettingsStore(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new ArgumentException("Settings path cannot be empty.", nameof(settingsPath));
        }

        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                return Normalize(settings ?? new AppSettings());
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var normalized = Normalize(settings);
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.HotkeyDisplayText) && settings.HotkeyVirtualKey != 0)
        {
            settings.HotkeyDisplayText = AppSettings.DefaultHotkeyDisplay;
        }

        return settings;
    }
}
