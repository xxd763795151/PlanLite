using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PlanLite.Core.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PlanLite.Services;

public sealed class ThemeService : IDisposable
{
    private Window? _watchedWindow;
    private AppThemeMode _currentMode = AppThemeMode.System;
    private bool _isSubscribed;
    private bool _isWatchingWindow;
    private bool _pendingWatchOnLoad;

    public ApplicationTheme CurrentEffectiveTheme { get; private set; } = ApplicationTheme.Light;

    public event Action<ApplicationTheme>? EffectiveThemeChanged;

    public void Initialize(Window mainWindow, AppThemeMode mode)
    {
        _watchedWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));

        if (!_isSubscribed)
        {
            ApplicationThemeManager.Changed += OnWpfUiThemeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _isSubscribed = true;
        }

        ApplyTheme(mode);
    }

    public void ApplyTheme(AppThemeMode mode)
    {
        _currentMode = mode;

        if (_watchedWindow is not null && _pendingWatchOnLoad)
        {
            _watchedWindow.Loaded -= OnWatchedWindowLoaded;
            _pendingWatchOnLoad = false;
        }

        if (_watchedWindow is not null && _isWatchingWindow)
        {
            if (_watchedWindow.IsLoaded)
            {
                try
                {
                    SystemThemeWatcher.UnWatch(_watchedWindow);
                }
                catch
                {
                    // Ignore if watcher state was already detached.
                }
            }

            _isWatchingWindow = false;
        }

        ApplicationTheme targetTheme;
        switch (mode)
        {
            case AppThemeMode.Light:
                targetTheme = ApplicationTheme.Light;
                break;
            case AppThemeMode.Dark:
                targetTheme = ApplicationTheme.Dark;
                break;
            default:
                targetTheme = ResolveSystemApplicationTheme();
                if (_watchedWindow is not null)
                {
                    if (_watchedWindow.IsLoaded)
                    {
                        SystemThemeWatcher.Watch(_watchedWindow, WindowBackdropType.None, updateAccents: true);
                        _isWatchingWindow = true;
                    }
                    else
                    {
                        _watchedWindow.Loaded += OnWatchedWindowLoaded;
                        _pendingWatchOnLoad = true;
                    }
                }
                break;
        }

        ApplicationThemeManager.Apply(targetTheme, WindowBackdropType.None, updateAccent: true);
        UpdateSemanticBrushes(targetTheme);
    }

    public void SyncIfSystemMode()
    {
        if (_currentMode != AppThemeMode.System)
        {
            return;
        }

        var systemTheme = ResolveSystemApplicationTheme();
        if (systemTheme != CurrentEffectiveTheme)
        {
            ApplicationThemeManager.Apply(systemTheme, WindowBackdropType.None, updateAccent: true);
            UpdateSemanticBrushes(systemTheme);
        }
    }

    private void OnWatchedWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_watchedWindow is null)
        {
            return;
        }

        _watchedWindow.Loaded -= OnWatchedWindowLoaded;
        _pendingWatchOnLoad = false;

        if (_currentMode == AppThemeMode.System && !_isWatchingWindow)
        {
            SystemThemeWatcher.Watch(_watchedWindow, WindowBackdropType.None, updateAccents: true);
            _isWatchingWindow = true;
            SyncIfSystemMode();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _currentMode != AppThemeMode.System)
        {
            return;
        }

        _watchedWindow?.Dispatcher.BeginInvoke(SyncIfSystemMode);
    }

    private void OnWpfUiThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        UpdateSemanticBrushes(currentApplicationTheme);
    }

    private static ApplicationTheme ResolveSystemApplicationTheme()
    {
        var sys = ApplicationThemeManager.GetSystemTheme();
        return sys switch
        {
            SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
            SystemTheme.HCBlack or SystemTheme.HC1 or SystemTheme.HC2 => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light
        };
    }

    private void UpdateSemanticBrushes(ApplicationTheme theme)
    {
        CurrentEffectiveTheme = theme;
        var isDark = theme == ApplicationTheme.Dark;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        if (isDark)
        {
            SetBrush(resources, "PlanPopupBackgroundBrush", "#202226");
            SetBrush(resources, "PlanHeaderBackgroundBrush", "#272A30");
            SetBrush(resources, "PlanCardBackgroundBrush", "#2B2E35");
            SetBrush(resources, "PlanCardHoverBrush", "#333740");
            SetBrush(resources, "PlanSubtleBorderBrush", "#3D424C");
            SetBrush(resources, "PlanInputBackgroundBrush", "#24272D");
            SetBrush(resources, "PlanPrimaryTextBrush", "#F2F4F8");
            SetBrush(resources, "PlanSecondaryTextBrush", "#B4BCC8");
            SetBrush(resources, "PlanMutedTextBrush", "#7F8794");
            SetBrush(resources, "PlanProgressTrackBrush", "#3C414B");
            SetBrush(resources, "PlanAccentBrush", "#4CC2FF");
            SetBrush(resources, "PlanPrimaryButtonBrush", "#0078D4");
            SetBrush(resources, "PlanAccentSoftBrush", "#1D3448");
            SetBrush(resources, "PlanSuccessBrush", "#54C77B");
            SetBrush(resources, "PlanSuccessSoftBrush", "#1D3828");
            SetBrush(resources, "PlanDangerBrush", "#FF7A70");
            SetBrush(resources, "PlanDangerSoftBrush", "#412322");
            SetBrush(resources, "PlanOverlayBackdropBrush", "#88000000");
            SetBrush(resources, "PlanMiniBackgroundBrush", "#262930");
        }
        else
        {
            SetBrush(resources, "PlanPopupBackgroundBrush", "#F9FAFC");
            SetBrush(resources, "PlanHeaderBackgroundBrush", "#F1F4F8");
            SetBrush(resources, "PlanCardBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "PlanCardHoverBrush", "#F3F7FC");
            SetBrush(resources, "PlanSubtleBorderBrush", "#E1E6EE");
            SetBrush(resources, "PlanInputBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "PlanPrimaryTextBrush", "#181B22");
            SetBrush(resources, "PlanSecondaryTextBrush", "#5A6270");
            SetBrush(resources, "PlanMutedTextBrush", "#8B93A1");
            SetBrush(resources, "PlanProgressTrackBrush", "#E5EAF1");
            SetBrush(resources, "PlanAccentBrush", "#0067C0");
            SetBrush(resources, "PlanPrimaryButtonBrush", "#0067C0");
            SetBrush(resources, "PlanAccentSoftBrush", "#E8F2FC");
            SetBrush(resources, "PlanSuccessBrush", "#107C41");
            SetBrush(resources, "PlanSuccessSoftBrush", "#E6F4EC");
            SetBrush(resources, "PlanDangerBrush", "#C42B1C");
            SetBrush(resources, "PlanDangerSoftBrush", "#FDECEC");
            SetBrush(resources, "PlanOverlayBackdropBrush", "#66000000");
            SetBrush(resources, "PlanMiniBackgroundBrush", "#FFFFFF");
        }

        EffectiveThemeChanged?.Invoke(theme);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    public void Dispose()
    {
        if (_watchedWindow is not null)
        {
            if (_pendingWatchOnLoad)
            {
                _watchedWindow.Loaded -= OnWatchedWindowLoaded;
                _pendingWatchOnLoad = false;
            }

            if (_isWatchingWindow && _watchedWindow.IsLoaded)
            {
                try
                {
                    SystemThemeWatcher.UnWatch(_watchedWindow);
                }
                catch
                {
                    // Ignore cleanup errors during shutdown.
                }
            }

            _isWatchingWindow = false;
            _watchedWindow = null;
        }

        if (_isSubscribed)
        {
            ApplicationThemeManager.Changed -= OnWpfUiThemeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _isSubscribed = false;
        }
    }
}
