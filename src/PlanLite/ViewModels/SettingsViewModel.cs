using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Models;
using PlanLite.Core.Paths;
using PlanLite.Core.Services;
using PlanLite.Services;

namespace PlanLite.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly StartupService _startupService;
    private readonly ThemeService _themeService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly BackupService _backupService;
    private readonly Func<string, string, string, string?> _pickSaveFilePath;
    private readonly Func<string, string, string?> _pickOpenFilePath;
    private readonly Action<bool> _onPinnedChanged;
    private readonly Action<bool> _onMiniWidgetChanged;
    private readonly Action _onRestoredFromBackup;
    private readonly Action<string> _showToast;

    private AppSettings _settings;
    private bool _suppressCallbacks;

    public SettingsViewModel(
        AppPaths paths,
        AppSettings initialSettings,
        SettingsStore settingsStore,
        StartupService startupService,
        ThemeService themeService,
        GlobalHotkeyService hotkeyService,
        BackupService backupService,
        Func<string, string, string, string?> pickSaveFilePath,
        Func<string, string, string?> pickOpenFilePath,
        Action<bool> onPinnedChanged,
        Action<bool> onMiniWidgetChanged,
        Action onRestoredFromBackup,
        Action<string> showToast)
    {
        _paths = paths;
        _settings = initialSettings;
        _settingsStore = settingsStore;
        _startupService = startupService;
        _themeService = themeService;
        _hotkeyService = hotkeyService;
        _backupService = backupService;
        _pickSaveFilePath = pickSaveFilePath;
        _pickOpenFilePath = pickOpenFilePath;
        _onPinnedChanged = onPinnedChanged;
        _onMiniWidgetChanged = onMiniWidgetChanged;
        _onRestoredFromBackup = onRestoredFromBackup;
        _showToast = showToast;

        LoadFromSettings(initialSettings);
    }

    public string DataDirectoryPath => _paths.DataDirectory;
    public string StorageModeText => _paths.IsPortableMode ? "便携模式 (portable.flag)" : "独立数据目录 (%LocalAppData%)";

    [ObservableProperty]
    private AppThemeMode _themeMode;

    [ObservableProperty]
    private bool _launchAtStartup;

    [ObservableProperty]
    private bool _isWindowPinned;

    [ObservableProperty]
    private bool _isMiniWidgetEnabled;

    [ObservableProperty]
    private bool _hotkeyEnabled;

    [ObservableProperty]
    private string _hotkeyDisplayText = AppSettings.DefaultHotkeyDisplay;

    [ObservableProperty]
    private string? _hotkeyStatusMessage;

    [ObservableProperty]
    private bool _hasHotkeyError;

    [ObservableProperty]
    private string? _startupErrorMessage;

    [ObservableProperty]
    private string? _pendingRestorePath;

    [ObservableProperty]
    private string? _pendingRestoreFileName;

    public void LoadFromSettings(AppSettings settings)
    {
        _settings = settings;
        _suppressCallbacks = true;
        ThemeMode = settings.ThemeMode;
        LaunchAtStartup = settings.LaunchAtStartup;
        IsWindowPinned = settings.IsWindowPinned;
        IsMiniWidgetEnabled = settings.IsMiniWidgetEnabled;
        HotkeyEnabled = settings.HotkeyEnabled;
        HotkeyDisplayText = string.IsNullOrWhiteSpace(settings.HotkeyDisplayText)
            ? AppSettings.DefaultHotkeyDisplay
            : settings.HotkeyDisplayText;
        StartupErrorMessage = null;
        PendingRestorePath = null;
        PendingRestoreFileName = null;
        _suppressCallbacks = false;
    }

    public void SyncPinnedFromExternal(bool isPinned)
    {
        _suppressCallbacks = true;
        IsWindowPinned = isPinned;
        _settings.IsWindowPinned = isPinned;
        _suppressCallbacks = false;
    }

    public void SyncMiniWidgetFromExternal(bool isEnabled)
    {
        _suppressCallbacks = true;
        IsMiniWidgetEnabled = isEnabled;
        _settings.IsMiniWidgetEnabled = isEnabled;
        _suppressCallbacks = false;
    }

    partial void OnThemeModeChanged(AppThemeMode value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        _settings.ThemeMode = value;
        _themeService.ApplyTheme(value);
        _settingsStore.Save(_settings);
        _showToast("已切换界面主题");
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        StartupErrorMessage = null;
        if (!_startupService.TrySetEnabled(value, out var error))
        {
            StartupErrorMessage = error;
            _suppressCallbacks = true;
            LaunchAtStartup = !value;
            _suppressCallbacks = false;
            return;
        }

        _settings.LaunchAtStartup = value;
        _settingsStore.Save(_settings);
        _showToast(value ? "已开启开机自启动" : "已关闭开机自启动");
    }

    partial void OnIsWindowPinnedChanged(bool value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        _settings.IsWindowPinned = value;
        _settingsStore.Save(_settings);
        _onPinnedChanged(value);
    }

    partial void OnIsMiniWidgetEnabledChanged(bool value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        _settings.IsMiniWidgetEnabled = value;
        _settingsStore.Save(_settings);
        _onMiniWidgetChanged(value);
    }

    partial void OnHotkeyEnabledChanged(bool value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        _settings.HotkeyEnabled = value;
        ApplyAndPersistHotkey();
    }

    [RelayCommand]
    private void SelectTheme(string modeName)
    {
        if (Enum.TryParse<AppThemeMode>(modeName, ignoreCase: true, out var mode))
        {
            ThemeMode = mode;
        }
    }

    public void ApplyCapturedHotkey(uint modifiers, uint virtualKey, string displayText)
    {
        if (modifiers == 0 || virtualKey == 0)
        {
            HasHotkeyError = true;
            HotkeyStatusMessage = "请使用修饰键（Ctrl / Alt / Shift / Win）加普通按键组合。";
            return;
        }

        _settings.HotkeyEnabled = true;
        _settings.HotkeyModifiers = modifiers;
        _settings.HotkeyVirtualKey = virtualKey;
        _settings.HotkeyDisplayText = displayText;

        _suppressCallbacks = true;
        HotkeyEnabled = true;
        HotkeyDisplayText = displayText;
        _suppressCallbacks = false;

        ApplyAndPersistHotkey();
    }

    [RelayCommand]
    private void ResetDefaultHotkey()
    {
        ApplyCapturedHotkey(
            AppSettings.DefaultHotkeyModifiers,
            AppSettings.DefaultHotkeyVirtualKey,
            AppSettings.DefaultHotkeyDisplay);
    }

    public void RefreshHotkeyRegistrationStatus(bool success, string? error)
    {
        HasHotkeyError = !success;
        HotkeyStatusMessage = success
            ? (_settings.HotkeyEnabled ? $"已启用全局快捷键: {HotkeyDisplayText}" : "全局快捷键已禁用")
            : error;
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            _paths.EnsureDirectories();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.DataDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _showToast($"无法打开目录: {ex.Message}");
        }
    }

    [RelayCommand]
    private void BackupData()
    {
        var defaultName = $"PlanLite-Backup-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        var destPath = _pickSaveFilePath(defaultName, "PlanLite 备份文件 (*.zip)|*.zip", "备份本地数据");
        if (string.IsNullOrWhiteSpace(destPath))
        {
            return;
        }

        try
        {
            _backupService.CreateBackup(destPath);
            _showToast($"已备份至 {Path.GetFileName(destPath)}");
        }
        catch (Exception ex)
        {
            _showToast($"备份失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RequestRestoreBackup()
    {
        var sourcePath = _pickOpenFilePath("PlanLite 备份文件 (*.zip)|*.zip", "选择要恢复的备份文件");
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        PendingRestorePath = sourcePath;
        PendingRestoreFileName = Path.GetFileName(sourcePath);
    }

    [RelayCommand]
    private void CancelRestoreBackup()
    {
        PendingRestorePath = null;
        PendingRestoreFileName = null;
    }

    [RelayCommand]
    private void ConfirmRestoreBackup()
    {
        if (string.IsNullOrWhiteSpace(PendingRestorePath))
        {
            return;
        }

        var path = PendingRestorePath;
        PendingRestorePath = null;
        PendingRestoreFileName = null;

        try
        {
            _backupService.RestoreBackup(path);
            _onRestoredFromBackup();
            _showToast("数据恢复成功");
        }
        catch (Exception ex)
        {
            _showToast($"恢复失败: {ex.Message}");
        }
    }

    private void ApplyAndPersistHotkey()
    {
        var (success, error) = _hotkeyService.Apply(
            _settings.HotkeyEnabled,
            _settings.HotkeyModifiers,
            _settings.HotkeyVirtualKey);

        RefreshHotkeyRegistrationStatus(success, error);
        _settingsStore.Save(_settings);
        if (success)
        {
            _showToast(_settings.HotkeyEnabled ? $"快捷键已更新为 {HotkeyDisplayText}" : "已关闭全局快捷键");
        }
    }
}
