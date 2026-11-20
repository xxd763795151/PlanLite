using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Wpf.Ui.Tray;
using Wpf.Ui.Tray.Controls;

namespace PlanLite.Services;

public sealed class TrayService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private RoutedNotifyIconEvent? _leftClickHandler;
    private MenuItem? _pinMenuItem;
    private MenuItem? _miniWidgetMenuItem;
    private bool _disposed;

    public event Action? ToggleMainWindowRequested;
    public event Action? OpenMainWindowRequested;
    public event Action? OpenReportsRequested;
    public event Action? OpenSettingsRequested;
    public event Action? TogglePinRequested;
    public event Action? ToggleMiniWidgetRequested;
    public event Action? ExitRequested;

    public void Initialize(Window parentWindow, bool isPinned, bool isMiniWidgetEnabled)
    {
        ArgumentNullException.ThrowIfNull(parentWindow);

        var iconFrame = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/PlanLite.ico", UriKind.Absolute));
        var menu = BuildContextMenu(isPinned, isMiniWidgetEnabled);

        _notifyIcon = new NotifyIcon
        {
            Icon = iconFrame,
            TooltipText = "PlanLite · 工作计划",
            FocusOnLeftClick = false,
            MenuOnRightClick = true,
            Menu = menu
        };

#pragma warning disable CS8622
        _leftClickHandler = (_, e) =>
        {
            e.Handled = true;
            ToggleMainWindowRequested?.Invoke();
        };
#pragma warning restore CS8622
        _notifyIcon.LeftClick += _leftClickHandler;
        _notifyIcon.Register();
    }

    public void UpdateState(bool isPinned, bool isMiniWidgetEnabled, string tooltipText)
    {
        if (_disposed || _notifyIcon is null)
        {
            return;
        }

        if (_pinMenuItem is not null)
        {
            _pinMenuItem.IsChecked = isPinned;
        }

        if (_miniWidgetMenuItem is not null)
        {
            _miniWidgetMenuItem.IsChecked = isMiniWidgetEnabled;
        }

        var safeTooltip = string.IsNullOrWhiteSpace(tooltipText) ? "PlanLite · 工作计划" : tooltipText;
        if (safeTooltip.Length > 120)
        {
            safeTooltip = safeTooltip[..117] + "...";
        }

        _notifyIcon.TooltipText = safeTooltip;
    }

    private ContextMenu BuildContextMenu(bool isPinned, bool isMiniWidgetEnabled)
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "打开主窗口" };
        openItem.Click += (_, _) => OpenMainWindowRequested?.Invoke();

        _pinMenuItem = new MenuItem
        {
            Header = "固定窗口",
            IsCheckable = true,
            IsChecked = isPinned
        };
        _pinMenuItem.Click += (_, _) => TogglePinRequested?.Invoke();

        _miniWidgetMenuItem = new MenuItem
        {
            Header = "迷你悬浮窗",
            IsCheckable = true,
            IsChecked = isMiniWidgetEnabled
        };
        _miniWidgetMenuItem.Click += (_, _) => ToggleMiniWidgetRequested?.Invoke();

        var reportsItem = new MenuItem { Header = "日报 / 周报" };
        reportsItem.Click += (_, _) => OpenReportsRequested?.Invoke();

        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke();

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_pinMenuItem);
        menu.Items.Add(_miniWidgetMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(reportsItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        return menu;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_notifyIcon is not null)
        {
            if (_leftClickHandler is not null)
            {
                _notifyIcon.LeftClick -= _leftClickHandler;
                _leftClickHandler = null;
            }
            _notifyIcon.Unregister();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
