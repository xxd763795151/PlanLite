using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PlanLite.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x504C; // 'PL'

    private HwndSource? _hwndSource;
    private bool _isRegistered;

    public event Action? HotkeyPressed;

    public (bool Success, string? ErrorMessage) Apply(bool enabled, uint modifiers, uint virtualKey)
    {
        EnsureMessageWindow();
        Unregister();

        if (!enabled || virtualKey == 0)
        {
            return (true, null);
        }

        if (_hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            return (false, "无法初始化快捷键监听窗口。");
        }

        var flags = modifiers | (uint)Win32Interop.HotkeyModifiers.NoRepeat;
        if (Win32Interop.RegisterHotKey(_hwndSource.Handle, HotkeyId, flags, virtualKey))
        {
            _isRegistered = true;
            return (true, null);
        }

        var win32Error = Marshal.GetLastWin32Error();
        var message = win32Error == 1409
            ? "快捷键已被其他程序占用，请更换组合键。"
            : $"快捷键注册失败 (错误码: {win32Error})。";
        return (false, message);
    }

    public void Unregister()
    {
        if (_isRegistered && _hwndSource is not null && _hwndSource.Handle != IntPtr.Zero)
        {
            Win32Interop.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            _isRegistered = false;
        }
    }

    private void EnsureMessageWindow()
    {
        if (_hwndSource is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("PlanLiteHotkeySink")
        {
            ParentWindow = Win32Interop.HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
