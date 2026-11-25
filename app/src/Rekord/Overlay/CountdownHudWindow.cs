using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Rekord.Native;
using RekordCore;

namespace Rekord.Overlay;

/// <summary>
/// Translucent floating 3-2-1 countdown badge centered in the capture region.
/// Excluded from capture and closed completely before recording starts.
/// </summary>
public sealed class CountdownHudWindow
{
    private Window? _window;
    private TextBlock? _label;
    private CancellationTokenSource? _cts;

    public async Task RunAsync(int seconds, RectD globalLogicalRect, Action onComplete)
    {
        Cancel();
        if (seconds <= 0)
        {
            onComplete();
            return;
        }

        const double size = 124;
        var label = new TextBlock
        {
            Text = seconds.ToString(),
            FontSize = 54,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.45,
                BlurRadius = 8,
                ShadowDepth = 2
            }
        };

        var circle = new Ellipse
        {
            Width = 112,
            Height = 112,
            Fill = new SolidColorBrush(Color.FromArgb(204, 24, 26, 32)),
            Stroke = new LinearGradientBrush(
                Color.FromArgb(140, 255, 255, 255),
                Color.FromArgb(190, 245, 72, 62),
                45
            ),
            StrokeThickness = 2.5
        };

        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(circle);
        grid.Children.Add(label);

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = size,
            Height = size,
            Left = globalLogicalRect.MidX - size / 2.0,
            Top = globalLogicalRect.MidY - size / 2.0,
            Content = grid
        };

        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            Win32Interop.HideFromAltTab(hwnd, noActivate: true);
            Win32Interop.SetClickThrough(hwnd, true);
            Win32Interop.SetExcludeFromCapture(hwnd, true);
        };

        _window = win;
        _label = label;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        win.Show();

        try
        {
            for (var tick = seconds; tick >= 1; tick--)
            {
                if (token.IsCancellationRequested) return;
                _label.Text = tick.ToString();
                await Task.Delay(900, token);
            }

            if (token.IsCancellationRequested) return;
            Dismiss();
            await Task.Delay(80, token);
            if (!token.IsCancellationRequested)
                onComplete();
        }
        catch (OperationCanceledException)
        {
            Dismiss();
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        Dismiss();
    }

    private void Dismiss()
    {
        if (_window is not null)
        {
            try { _window.Close(); } catch { }
            _window = null;
            _label = null;
        }
    }
}
