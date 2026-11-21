using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace PlanLite.Services;

public sealed class TrayService : IDisposable
{
    private const uint TrayIconId = 1u;
    private const int TrayCallbackMessage = Win32Interop.WmApp + 1;
    private const int MaxInitialRegisterRetries = 5;
    private const string DefaultTooltipText = "PlanLite · 工作计划";
    private const string TrayIconPackUri = "pack://application:,,,/Assets/PlanLite.ico";

    private HwndSource? _hwndSource;
    private ContextMenu? _contextMenu;
    private MenuItem? _pinMenuItem;
    private MenuItem? _miniWidgetMenuItem;
    private DispatcherTimer? _registerRetryTimer;
    private IntPtr _iconHandle;
    private bool _ownsIconHandle;
    private uint _taskbarCreatedMessage;
    private int _registerRetryCount;
    private bool _isRegistered;
    private bool _isThemeSubscribed;
    private string _tooltipText = DefaultTooltipText;
    private bool _disposed;

    public bool IsRegistered => _isRegistered;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hwndSource is not null)
        {
            return;
        }

        _contextMenu = BuildContextMenu(isPinned, isMiniWidgetEnabled);
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        _isThemeSubscribed = true;

        _taskbarCreatedMessage = Win32Interop.RegisterWindowMessage("TaskbarCreated");

        var parameters = new HwndSourceParameters("PlanLiteTraySink")
        {
            // Must be top-level (not HWND_MESSAGE) so TaskbarCreated broadcasts reach WndProc.
            ParentWindow = IntPtr.Zero,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = Win32Interop.WsExToolWindow
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        (_iconHandle, _ownsIconHandle) = LoadTrayIconHandle();

        if (!TryRegisterIcon())
        {
            StartRegisterRetryTimer();
        }
    }

    public void UpdateState(bool isPinned, bool isMiniWidgetEnabled, string tooltipText)
    {
        if (_disposed)
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

        _tooltipText = NormalizeTooltip(tooltipText);

        if (_hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            return;
        }

        if (!_isRegistered)
        {
            _ = TryRegisterIcon();
            return;
        }

        var data = CreateNotifyIconData(Win32Interop.NifTip);
        if (!Win32Interop.ShellNotifyIcon(Win32Interop.NimModify, ref data))
        {
            _isRegistered = false;
            _ = TryRegisterIcon();
        }
    }

    private bool TryRegisterIcon()
    {
        if (_disposed || _hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            return false;
        }

        var data = CreateNotifyIconData(Win32Interop.NifMessage | Win32Interop.NifIcon | Win32Interop.NifTip);
        var registered = Win32Interop.ShellNotifyIcon(Win32Interop.NimAdd, ref data)
            || Win32Interop.ShellNotifyIcon(Win32Interop.NimModify, ref data);

        if (registered)
        {
            _isRegistered = true;
            StopRegisterRetryTimer();
        }

        return registered;
    }

    private Win32Interop.NotifyIconData CreateNotifyIconData(uint flags)
    {
        return new Win32Interop.NotifyIconData
        {
            CbSize = (uint)Marshal.SizeOf<Win32Interop.NotifyIconData>(),
            HWnd = _hwndSource?.Handle ?? IntPtr.Zero,
            UId = TrayIconId,
            UFlags = flags,
            UCallbackMessage = TrayCallbackMessage,
            HIcon = _iconHandle,
            SzTip = _tooltipText,
            SzInfo = string.Empty,
            SzInfoTitle = string.Empty
        };
    }

    private void StartRegisterRetryTimer()
    {
        if (_disposed || _isRegistered || _registerRetryTimer is not null)
        {
            return;
        }

        _registerRetryCount = 0;
        _registerRetryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _registerRetryTimer.Tick += OnRegisterRetryTick;
        _registerRetryTimer.Start();
    }

    private void OnRegisterRetryTick(object? sender, EventArgs e)
    {
        if (_disposed || _isRegistered)
        {
            StopRegisterRetryTimer();
            return;
        }

        _registerRetryCount++;
        if (TryRegisterIcon() || _registerRetryCount >= MaxInitialRegisterRetries)
        {
            StopRegisterRetryTimer();
        }
    }

    private void StopRegisterRetryTimer()
    {
        if (_registerRetryTimer is null)
        {
            return;
        }

        _registerRetryTimer.Stop();
        _registerRetryTimer.Tick -= OnRegisterRetryTick;
        _registerRetryTimer = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && unchecked((uint)msg) == _taskbarCreatedMessage)
        {
            _isRegistered = false;
            if (!TryRegisterIcon())
            {
                StartRegisterRetryTimer();
            }

            return IntPtr.Zero;
        }

        if (msg == Win32Interop.WmDestroy)
        {
            UnregisterIcon();
            return IntPtr.Zero;
        }

        if (msg == TrayCallbackMessage)
        {
            var mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMsg)
            {
                case Win32Interop.WmLButtonUp:
                    if (_contextMenu?.IsOpen == true)
                    {
                        _contextMenu.IsOpen = false;
                    }

                    handled = true;
                    ToggleMainWindowRequested?.Invoke();
                    break;

                case Win32Interop.WmRButtonUp:
                case Win32Interop.WmContextMenu:
                    handled = true;
                    ShowContextMenu();
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_disposed || _contextMenu is null || _hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            return;
        }

        // Foregrounding the message window ensures clicking outside dismisses the WPF ContextMenu.
        Win32Interop.SetForegroundWindow(_hwndSource.Handle);
        _contextMenu.Placement = PlacementMode.MousePoint;
        _contextMenu.PlacementTarget = null;
        _contextMenu.IsOpen = true;
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        var menu = _contextMenu;
        if (_disposed || menu is null)
        {
            return;
        }

        if (menu.Dispatcher.CheckAccess())
        {
            menu.UpdateDefaultStyle();
            menu.UpdateLayout();
        }
        else
        {
            menu.Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && _contextMenu is not null)
                {
                    _contextMenu.UpdateDefaultStyle();
                    _contextMenu.UpdateLayout();
                }
            });
        }
    }

    private void UnregisterIcon()
    {
        if (!_isRegistered || _hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            _isRegistered = false;
            return;
        }

        var data = CreateNotifyIconData(0);
        _ = Win32Interop.ShellNotifyIcon(Win32Interop.NimDelete, ref data);
        _isRegistered = false;
    }

    private static (IntPtr Handle, bool OwnsHandle) LoadTrayIconHandle()
    {
        try
        {
            var resourceInfo = Application.GetResourceStream(new Uri(TrayIconPackUri, UriKind.Absolute));
            if (resourceInfo?.Stream is not null)
            {
                using var stream = resourceInfo.Stream;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);

                var targetWidth = Math.Max(16, Win32Interop.GetSystemMetrics(Win32Interop.SmCxSmIcon));
                var targetHeight = Math.Max(16, Win32Interop.GetSystemMetrics(Win32Interop.SmCySmIcon));
                var frame = SelectBestIcoFrame(memory.ToArray(), targetWidth, targetHeight);
                if (frame is not null)
                {
                    var handle = Win32Interop.CreateIconFromResourceEx(
                        frame,
                        (uint)frame.Length,
                        fIcon: true,
                        dwVer: 0x00030000,
                        cxDesired: targetWidth,
                        cyDesired: targetHeight,
                        flags: Win32Interop.LrDefaultColor);
                    if (handle != IntPtr.Zero)
                    {
                        return (handle, true);
                    }
                }
            }
        }
        catch
        {
            // Fall back to default application icon handle below.
        }

        return (Win32Interop.LoadIcon(IntPtr.Zero, Win32Interop.IdiApplication), false);
    }

    internal static byte[]? SelectBestIcoFrame(byte[] icoBytes, int targetWidth, int targetHeight)
    {
        if (icoBytes is null || icoBytes.Length < 6)
        {
            return null;
        }

        var reserved = BitConverter.ToUInt16(icoBytes, 0);
        var imageType = BitConverter.ToUInt16(icoBytes, 2);
        var count = BitConverter.ToUInt16(icoBytes, 4);
        if (reserved != 0 || imageType != 1 || count == 0)
        {
            return null;
        }

        var headerEnd = 6L + count * 16L;
        if (icoBytes.Length < headerEnd)
        {
            return null;
        }

        var bestTier = int.MaxValue;
        var bestMetric = long.MaxValue;
        var bestBpp = -1;
        var bestOffset = -1;
        var bestSize = -1;

        for (var i = 0; i < count; i++)
        {
            var entryOffset = 6 + i * 16;
            var width = icoBytes[entryOffset] == 0 ? 256 : icoBytes[entryOffset];
            var height = icoBytes[entryOffset + 1] == 0 ? 256 : icoBytes[entryOffset + 1];
            var bitCount = BitConverter.ToUInt16(icoBytes, entryOffset + 6);
            var dataSize = BitConverter.ToInt32(icoBytes, entryOffset + 8);
            var dataOffset = BitConverter.ToInt32(icoBytes, entryOffset + 12);

            if (dataSize <= 0 || dataOffset < headerEnd || (long)dataOffset + dataSize > icoBytes.Length)
            {
                continue;
            }

            int tier;
            long metric;
            if (width == targetWidth && height == targetHeight)
            {
                tier = 0;
                metric = 0;
            }
            else if (width >= targetWidth && height >= targetHeight)
            {
                tier = 1;
                metric = (long)(width - targetWidth) + (height - targetHeight);
            }
            else
            {
                tier = 2;
                metric = -((long)width * height);
            }

            var isBetter = tier < bestTier
                || (tier == bestTier && metric < bestMetric)
                || (tier == bestTier && metric == bestMetric && bitCount > bestBpp);

            if (isBetter)
            {
                bestTier = tier;
                bestMetric = metric;
                bestBpp = bitCount;
                bestOffset = dataOffset;
                bestSize = dataSize;
            }
        }

        if (bestOffset < 0 || bestSize <= 0)
        {
            return null;
        }

        var frame = new byte[bestSize];
        Buffer.BlockCopy(icoBytes, bestOffset, frame, 0, bestSize);
        return frame;
    }

    private static string NormalizeTooltip(string? tooltipText)
    {
        var safeTooltip = string.IsNullOrWhiteSpace(tooltipText) ? DefaultTooltipText : tooltipText;
        return safeTooltip.Length > 120 ? safeTooltip[..117] + "..." : safeTooltip;
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
        StopRegisterRetryTimer();

        if (_isThemeSubscribed)
        {
            ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
            _isThemeSubscribed = false;
        }

        if (_contextMenu is not null)
        {
            _contextMenu.IsOpen = false;
            _contextMenu = null;
        }

        UnregisterIcon();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            if (_ownsIconHandle)
            {
                _ = Win32Interop.DestroyIcon(_iconHandle);
            }

            _iconHandle = IntPtr.Zero;
            _ownsIconHandle = false;
        }
    }
}
