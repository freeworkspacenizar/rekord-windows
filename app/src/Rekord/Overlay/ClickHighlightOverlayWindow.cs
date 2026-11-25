using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Rekord.Native;
using Rekord.SystemServices;

namespace Rekord.Overlay;

/// <summary>
/// Full-screen click-through transparent window that renders expanding ripple rings on mouse clicks
/// using a Win32 WH_MOUSE_LL hook. Intentionally NOT excluded from capture so click ripples appear in recordings.
/// </summary>
public sealed class ClickHighlightOverlayWindow : IDisposable
{
    private Window? _window;
    private Canvas? _canvas;
    private DisplayMonitor? _monitor;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Win32Interop.LowLevelMouseProc? _hookDelegate;

    public void Start(DisplayMonitor monitor)
    {
        Stop();
        _monitor = monitor;

        var canvas = new Canvas
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Left = monitor.LogicalBounds.X,
            Top = monitor.LogicalBounds.Y,
            Width = monitor.LogicalBounds.Width,
            Height = monitor.LogicalBounds.Height,
            Content = canvas
        };

        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            Win32Interop.HideFromAltTab(hwnd, noActivate: true);
            Win32Interop.SetClickThrough(hwnd, true);
            // Intentionally NOT calling SetExcludeFromCapture so ripples appear in recordings!
        };

        _window = win;
        _canvas = canvas;
        win.Show();

        _hookDelegate = MouseHookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = Win32Interop.GetModuleHandle(curModule?.ModuleName);
        _hookHandle = Win32Interop.SetWindowsHookEx(Win32Interop.WH_MOUSE_LL, _hookDelegate, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Win32Interop.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _hookDelegate = null;

        if (_window is not null)
        {
            try { _window.Close(); } catch { }
            _window = null;
            _canvas = null;
        }
        _monitor = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _monitor is not null && _window is not null)
        {
            var msg = wParam.ToInt32();
            if (msg == Win32Interop.WM_LBUTTONDOWN || msg == Win32Interop.WM_RBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<Win32Interop.MSLLHOOKSTRUCT>(lParam);
                var mon = _monitor;
                var isSecondary = msg == Win32Interop.WM_RBUTTONDOWN;

                if (info.pt.X >= mon.PhysicalX && info.pt.X < mon.PhysicalX + mon.PhysicalWidth &&
                    info.pt.Y >= mon.PhysicalY && info.pt.Y < mon.PhysicalY + mon.PhysicalHeight)
                {
                    var localX = (info.pt.X - mon.PhysicalX) / mon.DpiScale;
                    var localY = (info.pt.Y - mon.PhysicalY) / mon.DpiScale;

                    _window.Dispatcher.BeginInvoke(() => SpawnRipple(localX, localY, isSecondary));
                }
            }
        }

        return Win32Interop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void SpawnRipple(double x, double y, bool isSecondary)
    {
        if (_canvas is null) return;

        const double initialRadius = 6;
        const double finalRadius = 28;
        var baseColor = isSecondary
            ? Color.FromRgb(56, 168, 255)
            : Color.FromRgb(255, 87, 71);

        var ring = new Ellipse
        {
            Width = initialRadius * 2,
            Height = initialRadius * 2,
            Fill = new SolidColorBrush(Color.FromArgb(62, baseColor.R, baseColor.G, baseColor.B)),
            Stroke = new SolidColorBrush(Color.FromArgb(242, baseColor.R, baseColor.G, baseColor.B)),
            StrokeThickness = 3.0,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(ring, x - initialRadius);
        Canvas.SetTop(ring, y - initialRadius);
        _canvas.Children.Add(ring);

        var duration = TimeSpan.FromMilliseconds(420);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var sizeAnim = new DoubleAnimation(initialRadius * 2, finalRadius * 2, duration) { EasingFunction = ease };
        var leftAnim = new DoubleAnimation(x - initialRadius, x - finalRadius, duration) { EasingFunction = ease };
        var topAnim = new DoubleAnimation(y - initialRadius, y - finalRadius, duration) { EasingFunction = ease };
        var opacityAnim = new DoubleAnimation(0.95, 0.0, duration) { EasingFunction = ease };
        var strokeAnim = new DoubleAnimation(3.5, 1.0, duration) { EasingFunction = ease };

        opacityAnim.Completed += (_, _) => _canvas?.Children.Remove(ring);

        ring.BeginAnimation(FrameworkElement.WidthProperty, sizeAnim);
        ring.BeginAnimation(FrameworkElement.HeightProperty, sizeAnim);
        ring.BeginAnimation(Canvas.LeftProperty, leftAnim);
        ring.BeginAnimation(Canvas.TopProperty, topAnim);
        ring.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        ring.BeginAnimation(Shape.StrokeThicknessProperty, strokeAnim);
    }

    public void Dispose() => Stop();
}
