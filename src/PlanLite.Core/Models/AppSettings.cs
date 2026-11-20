namespace PlanLite.Core.Models;

public enum AppThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public sealed class AppSettings
{
    // Win32 MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008
    public const uint DefaultHotkeyModifiers = 0x0001 | 0x0004; // Alt + Shift
    public const uint DefaultHotkeyVirtualKey = 0x50; // 'P'
    public const string DefaultHotkeyDisplay = "Alt + Shift + P";

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
    public bool IsWindowPinned { get; set; }
    public bool IsMiniWidgetEnabled { get; set; }
    public double? MiniWidgetLeft { get; set; }
    public double? MiniWidgetTop { get; set; }
    public bool LaunchAtStartup { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = DefaultHotkeyModifiers;
    public uint HotkeyVirtualKey { get; set; } = DefaultHotkeyVirtualKey;
    public string HotkeyDisplayText { get; set; } = DefaultHotkeyDisplay;
    public bool ShowCompletedWeeklyExpanded { get; set; }
    public bool ShowCompletedDailyExpanded { get; set; }
}
