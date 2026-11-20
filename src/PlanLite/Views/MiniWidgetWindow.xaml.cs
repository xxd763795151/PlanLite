using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PlanLite.Services;
using PlanLite.ViewModels;

namespace PlanLite.Views;

public partial class MiniWidgetWindow : Window
{
    private const double SnapThresholdDip = 16.0;
    private readonly Action _onClicked;
    private readonly Action<double, double> _onPositionSaved;

    private bool _isMouseCaptured;
    private bool _isDragging;
    private Point _dragStartScreen;
    private double _windowStartLeft;
    private double _windowStartTop;

    public MiniWidgetWindow(
        MiniWidgetViewModel viewModel,
        double? initialLeft,
        double? initialTop,
        Action onClicked,
        Action<double, double> onPositionSaved)
    {
        InitializeComponent();
        DataContext = viewModel;
        _onClicked = onClicked;
        _onPositionSaved = onPositionSaved;

        if (initialLeft.HasValue && initialTop.HasValue)
        {
            Left = initialLeft.Value;
            Top = initialTop.Value;
            ClampToWorkArea();
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 18;
            Top = workArea.Bottom - Height - 18;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.ApplyToolWindowStyle(hwnd);
        Win32Interop.ApplyRoundedCorners(hwnd);
        ClampToWorkArea();
    }

    private void OnWidgetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isMouseCaptured = true;
        _isDragging = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _windowStartLeft = Left;
        _windowStartTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnWidgetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreen = PointToScreen(e.GetPosition(this));
        var dpiScale = GetDpiScale();
        var deltaX = (currentScreen.X - _dragStartScreen.X) / dpiScale.M11;
        var deltaY = (currentScreen.Y - _dragStartScreen.Y) / dpiScale.M22;

        if (!_isDragging && (Math.Abs(deltaX) > 4 || Math.Abs(deltaY) > 4))
        {
            _isDragging = true;
        }

        if (_isDragging)
        {
            Left = _windowStartLeft + deltaX;
            Top = _windowStartTop + deltaY;
        }
    }

    private void OnWidgetMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMouseCaptured)
        {
            return;
        }

        _isMouseCaptured = false;
        ReleaseMouseCapture();

        if (_isDragging)
        {
            _isDragging = false;
            SnapAndClampToWorkArea();
            _onPositionSaved(Left, Top);
        }
        else
        {
            _onClicked();
        }

        e.Handled = true;
    }

    private void SnapAndClampToWorkArea()
    {
        var workArea = GetCurrentMonitorWorkAreaDip();

        if (Math.Abs(Left - workArea.Left) <= SnapThresholdDip)
        {
            Left = workArea.Left + 8;
        }
        else if (Math.Abs((Left + Width) - workArea.Right) <= SnapThresholdDip)
        {
            Left = workArea.Right - Width - 8;
        }

        if (Math.Abs(Top - workArea.Top) <= SnapThresholdDip)
        {
            Top = workArea.Top + 8;
        }
        else if (Math.Abs((Top + Height) - workArea.Bottom) <= SnapThresholdDip)
        {
            Top = workArea.Bottom - Height - 8;
        }

        ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        var workArea = GetCurrentMonitorWorkAreaDip();
        var minLeft = workArea.Left + 4;
        var maxLeft = Math.Max(minLeft, workArea.Right - Width - 4);
        var minTop = workArea.Top + 4;
        var maxTop = Math.Max(minTop, workArea.Bottom - Height - 4);

        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    private Rect GetCurrentMonitorWorkAreaDip()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var monitor = Win32Interop.MonitorFromWindow(hwnd, Win32Interop.MonitorDefaultToNearest);
        var info = new Win32Interop.MonitorInfo { CbSize = Marshal.SizeOf<Win32Interop.MonitorInfo>() };
        if (!Win32Interop.GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.RcWork.Left, info.RcWork.Top));
        var bottomRight = transform.Transform(new Point(info.RcWork.Right, info.RcWork.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private Matrix GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
    }
}
