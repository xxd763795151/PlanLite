using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PlanLite.Services;
using PlanLite.ViewModels;
using Wpf.Ui.Controls;

namespace PlanLite;

public partial class MainWindow : FluentWindow
{
    private const double ScreenEdgeMarginDip = 12.0;
    private const int TrayBounceDebounceMs = 250;

    private readonly MainViewModel _viewModel;
    private readonly ThemeService _themeService;
    private DateTime _lastAutoHideUtc = DateTime.MinValue;
    private bool _suppressAutoHide;
    private bool _allowExitClose;

    public MainWindow(MainViewModel viewModel, ThemeService themeService)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));

        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.ApplyToolWindowStyle(hwnd);
        Win32Interop.ApplyRoundedCorners(hwnd);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExitClose)
        {
            e.Cancel = true;
            HidePopup();
            return;
        }

        base.OnClosing(e);
    }

    public void PrepareForExit()
    {
        _allowExitClose = true;
        _viewModel.FlushPendingSaves();
    }

    public void ToggleFromTray()
    {
        if (IsVisible)
        {
            HidePopup();
            return;
        }

        if ((DateTime.UtcNow - _lastAutoHideUtc).TotalMilliseconds < TrayBounceDebounceMs)
        {
            return;
        }

        ShowAndActivate();
    }

    public void ToggleFromHotkey()
    {
        if (IsVisible && IsActive)
        {
            HidePopup();
        }
        else
        {
            ShowAndActivate();
        }
    }

    public void ShowAndActivate()
    {
        _themeService.SyncIfSystemMode();
        _viewModel.RefreshAllData();

        PositionBottomRight();
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        UpdateLayout();
        PositionBottomRight();

        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.SetForegroundWindow(hwnd);
        }
    }

    public void HidePopup()
    {
        _viewModel.FlushPendingSaves();
        if (IsVisible)
        {
            Hide();
            Win32Interop.TrimWorkingSet();
        }
    }

    public T RunWithAutoHideSuppressed<T>(Func<T> action)
    {
        _suppressAutoHide = true;
        try
        {
            return action();
        }
        finally
        {
            _suppressAutoHide = false;
            if (IsVisible)
            {
                Activate();
            }
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_suppressAutoHide || _viewModel.IsPinned || !IsVisible)
        {
            return;
        }

        _lastAutoHideUtc = DateTime.UtcNow;
        HidePopup();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible)
        {
            PositionBottomRight();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.NoteEditor.IsOpen)
        {
            _viewModel.NoteEditor.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_viewModel.TaskEditor.IsOpen)
        {
            _viewModel.TaskEditor.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!_viewModel.IsPinned)
        {
            HidePopup();
            e.Handled = true;
        }
    }

    private void PositionBottomRight()
    {
        var workArea = GetActiveMonitorWorkAreaDip();
        MaxHeight = Math.Max(320.0, workArea.Height - ScreenEdgeMarginDip * 2);

        var actualW = ActualWidth > 0 ? ActualWidth : Width;
        var actualH = ActualHeight > 0 ? ActualHeight : MinHeight;

        Left = Math.Max(workArea.Left + ScreenEdgeMarginDip, workArea.Right - actualW - ScreenEdgeMarginDip);
        Top = Math.Max(workArea.Top + ScreenEdgeMarginDip, workArea.Bottom - actualH - ScreenEdgeMarginDip);
    }

    private Rect GetActiveMonitorWorkAreaDip()
    {
        IntPtr monitor = IntPtr.Zero;
        if (Win32Interop.GetCursorPos(out var pt))
        {
            monitor = Win32Interop.MonitorFromPoint(pt, Win32Interop.MonitorDefaultToNearest);
        }

        if (monitor == IntPtr.Zero)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                monitor = Win32Interop.MonitorFromWindow(hwnd, Win32Interop.MonitorDefaultToNearest);
            }
        }

        if (monitor != IntPtr.Zero)
        {
            var info = new Win32Interop.MonitorInfo { CbSize = Marshal.SizeOf<Win32Interop.MonitorInfo>() };
            if (Win32Interop.GetMonitorInfo(monitor, ref info))
            {
                var source = PresentationSource.FromVisual(this);
                var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var topLeft = transform.Transform(new Point(info.RcWork.Left, info.RcWork.Top));
                var bottomRight = transform.Transform(new Point(info.RcWork.Right, info.RcWork.Bottom));
                return new Rect(topLeft, bottomRight);
            }
        }

        return SystemParameters.WorkArea;
    }
}
