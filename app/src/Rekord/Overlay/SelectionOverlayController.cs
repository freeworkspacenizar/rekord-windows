using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Rekord.Capture;
using Rekord.Native;
using Rekord.SystemServices;
using RekordCore;

namespace Rekord.Overlay;

public enum OverlayMode
{
    Selecting,
    Countdown,
    Recording,
    Paused
}

/// <summary>
/// Manages the full-screen transparent selection overlay window + floating frosted-dark control pill.
/// Both windows set WDA_EXCLUDEFROMCAPTURE so neither borders nor controls appear in recordings.
/// </summary>
public sealed class SelectionOverlayController
{
    private readonly AppSettings _settings;
    private Window? _overlayWindow;
    private SelectionCanvasControl? _canvas;
    private Window? _toolbarWindow;
    private StackPanel? _toolbarRoot;
    private readonly Window?[] _borderStrips = new Window?[4]; // Top, Bottom, Left, Right exterior strips during recording

    // Toolbar dynamic references
    private TextBlock? _durationLabel;
    private Ellipse? _statusDot;
    private Button? _pauseResumeButton;
    private readonly Dictionary<AspectRatioPreset, Button> _ratioButtons = new();
    private Button? _countdownButton;
    private Button? _cursorButton;
    private Button? _clickHighlightButton;

    public DisplayMonitor? TargetMonitor { get; private set; }
    public RectD SelectionRect { get; private set; }
    public OverlayMode Mode { get; private set; } = OverlayMode.Selecting;
    public AspectRatioPreset AspectRatio { get; private set; } = AspectRatioPreset.Free;

    public event Action? StartRecordingRequested;
    public event Action? StopRecordingRequested;
    public event Action? TogglePauseRequested;
    public event Action? CancelRequested;
    public event Action<RectD>? PanDuringRecording;

    public SelectionOverlayController(AppSettings settings)
    {
        _settings = settings;
    }

    public RectD SelectionGlobalLogicalRect
    {
        get
        {
            if (TargetMonitor is null)
                return SelectionRect;
            return new RectD(
                TargetMonitor.LogicalBounds.X + SelectionRect.X,
                TargetMonitor.LogicalBounds.Y + SelectionRect.Y,
                SelectionRect.Width,
                SelectionRect.Height
            );
        }
    }

    public bool ShowForSelection()
    {
        var monitor = MonitorInfo.FindMonitorAtCursor();
        if (monitor is null)
            return false;

        TargetMonitor = monitor;
        var localBounds = new RectD(0, 0, monitor.LogicalBounds.Width, monitor.LogicalBounds.Height);

        if (SelectionRect.Width < CaptureGeometry.MinimumDimension ||
            SelectionRect.Height < CaptureGeometry.MinimumDimension ||
            !localBounds.Intersects(SelectionRect))
        {
            SelectionRect = CaptureGeometry.DefaultSelectionRect(new SizeD(localBounds.Width, localBounds.Height));
        }
        else
        {
            SelectionRect = CaptureGeometry.ClampRect(SelectionRect, localBounds);
        }

        Mode = OverlayMode.Selecting;
        BuildWindows(monitor);
        UpdateVisuals();
        RepositionToolbar();

        _overlayWindow?.Show();
        _overlayWindow?.Activate();
        if (_toolbarWindow is not null)
        {
            if (_overlayWindow is not null)
                _toolbarWindow.Owner = _overlayWindow;
            _toolbarWindow.Show();
            RepositionToolbar();
            Win32Interop.BringTopmostNoActivate(new WindowInteropHelper(_toolbarWindow).Handle);
        }
        return true;
    }

    public void TransitionTo(OverlayMode newMode)
    {
        Mode = newMode;
        if (_canvas is not null)
        {
            _canvas.OverlayMode = newMode;
            _canvas.InvalidateVisual();
        }

        if (newMode is OverlayMode.Recording or OverlayMode.Paused)
        {
            // Detach Owner BEFORE hiding _overlayWindow so WPF does not hide _toolbarWindow too.
            if (_toolbarWindow is not null)
                _toolbarWindow.Owner = null;

            // Hide full-screen layered overlay completely during recording so DWM never blacks out
            // DXGI / BitBlt capture. Render the frame border via 4 thin opaque strips strictly OUTSIDE the capture rect.
            _overlayWindow?.Hide();
            ShowOrUpdateBorderStrips(newMode);
        }
        else
        {
            CloseBorderStrips();
            if (_overlayWindow is not null)
            {
                var hwnd = new WindowInteropHelper(_overlayWindow).Handle;
                Win32Interop.SetClickThrough(hwnd, newMode != OverlayMode.Selecting);
                _overlayWindow.Show();
            }
        }

        if (newMode == OverlayMode.Countdown)
        {
            if (_toolbarWindow is not null)
            {
                _toolbarWindow.Owner = null;
                _toolbarWindow.Hide();
            }
        }
        else
        {
            RebuildToolbarContent();
            if (_toolbarWindow is not null)
            {
                if (newMode == OverlayMode.Selecting && _overlayWindow is not null && _overlayWindow.IsVisible)
                    _toolbarWindow.Owner = _overlayWindow;
                _toolbarWindow.Show();
                RepositionToolbar();
                Win32Interop.BringTopmostNoActivate(new WindowInteropHelper(_toolbarWindow).Handle);
            }
        }
    }

    public void UpdateElapsed(double seconds)
    {
        if (_durationLabel is null) return;
        _durationLabel.Text = Mode == OverlayMode.Paused
            ? "PAUSED"
            : RecordingTimeline.FormatDuration(seconds, includeTenths: true);
    }

    public void Close()
    {
        CloseBorderStrips();
        if (_toolbarWindow is not null)
        {
            try { _toolbarWindow.Owner = null; } catch { }
            try { _toolbarWindow.Close(); } catch { }
            _toolbarWindow = null;
            _toolbarRoot = null;
        }
        if (_overlayWindow is not null)
        {
            try { _overlayWindow.Close(); } catch { }
            _overlayWindow = null;
            _canvas = null;
        }
        _ratioButtons.Clear();
        _durationLabel = null;
        _statusDot = null;
        _pauseResumeButton = null;
    }

    private void ShowOrUpdateBorderStrips(OverlayMode mode)
    {
        const double thickness = 3.0;
        const double pad = 1.0; // 1px outside capture rect so interior pixels are 100% untouched
        var g = SelectionGlobalLogicalRect;
        var brush = new SolidColorBrush(mode == OverlayMode.Paused
            ? Color.FromRgb(255, 184, 41)
            : Color.FromRgb(255, 68, 58));

        var stripRects = new[]
        {
            // Top
            new Rect(g.MinX - pad - thickness, g.MinY - pad - thickness, g.Width + (pad + thickness) * 2, thickness),
            // Bottom
            new Rect(g.MinX - pad - thickness, g.MaxY + pad, g.Width + (pad + thickness) * 2, thickness),
            // Left
            new Rect(g.MinX - pad - thickness, g.MinY - pad, thickness, g.Height + pad * 2),
            // Right
            new Rect(g.MaxX + pad, g.MinY - pad, thickness, g.Height + pad * 2)
        };

        for (var i = 0; i < 4; i++)
        {
            var r = stripRects[i];
            if (_borderStrips[i] is null)
            {
                var strip = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = false,
                    Background = brush,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize,
                    Left = r.X,
                    Top = r.Y,
                    Width = Math.Max(1, r.Width),
                    Height = Math.Max(1, r.Height)
                };
                strip.SourceInitialized += (_, _) =>
                {
                    var hwnd = new WindowInteropHelper(strip).Handle;
                    Win32Interop.HideFromAltTab(hwnd, noActivate: true);
                    Win32Interop.SetClickThrough(hwnd, true);
                };
                _borderStrips[i] = strip;
                strip.Show();
            }
            else
            {
                var strip = _borderStrips[i]!;
                strip.Background = brush;
                strip.Left = r.X;
                strip.Top = r.Y;
                strip.Width = Math.Max(1, r.Width);
                strip.Height = Math.Max(1, r.Height);
                if (!strip.IsVisible) strip.Show();
            }
        }
    }

    private void CloseBorderStrips()
    {
        for (var i = 0; i < _borderStrips.Length; i++)
        {
            if (_borderStrips[i] is not null)
            {
                try { _borderStrips[i]!.Close(); } catch { }
                _borderStrips[i] = null;
            }
        }
    }

    private void ApplyAspectRatio(AspectRatioPreset preset)
    {
        if (TargetMonitor is null) return;
        AspectRatio = preset;
        var fullBounds = new RectD(0, 0, TargetMonitor.LogicalBounds.Width, TargetMonitor.LogicalBounds.Height);
        var safeBounds = fullBounds.Height > 260 ? fullBounds.InsetBy(24, 72) : fullBounds;
        SelectionRect = CaptureGeometry.ApplyAspectRatio(preset, SelectionRect, safeBounds);
        UpdateVisuals();
        RepositionToolbar();
    }

    private void SnapToWindow(SnapWindowCandidate candidate)
    {
        AspectRatio = AspectRatioPreset.Free;
        SelectionRect = candidate.LogicalLocalRect;
        UpdateVisuals();
        RepositionToolbar();
    }

    private void PanBy(double dx, double dy)
    {
        if (!_settings.AllowPanDuringRecording || TargetMonitor is null) return;
        var bounds = new RectD(0, 0, TargetMonitor.LogicalBounds.Width, TargetMonitor.LogicalBounds.Height);
        var nextRect = CaptureGeometry.Pan(SelectionRect, new SizeD(dx, dy), bounds);
        if (nextRect == SelectionRect) return;

        SelectionRect = nextRect;
        if (Mode is OverlayMode.Recording or OverlayMode.Paused)
        {
            ShowOrUpdateBorderStrips(Mode);
        }
        else if (_canvas is not null)
        {
            _canvas.SelectionRect = nextRect;
            _canvas.InvalidateVisual();
        }
        RepositionToolbar();
        if (Mode is OverlayMode.Recording or OverlayMode.Paused)
            PanDuringRecording?.Invoke(nextRect);
    }

    private void BuildWindows(DisplayMonitor monitor)
    {
        Close();

        var canvas = new SelectionCanvasControl
        {
            SelectionRect = SelectionRect,
            OverlayMode = Mode,
            AspectRatioProvider = () => AspectRatio
        };
        canvas.SelectionChanged += rect =>
        {
            SelectionRect = rect;
            RepositionToolbar();
        };

        var overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Left = monitor.LogicalBounds.X,
            Top = monitor.LogicalBounds.Y,
            Width = monitor.LogicalBounds.Width,
            Height = monitor.LogicalBounds.Height,
            Content = canvas
        };

        overlay.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(overlay).Handle;
            Win32Interop.HideFromAltTab(hwnd, noActivate: false);
        };

        overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelRequested?.Invoke();
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Space) && Mode == OverlayMode.Selecting)
            {
                e.Handled = true;
                StartRecordingRequested?.Invoke();
            }
        };

        var pillRoot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var pillBorder = new Border
        {
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(14, 7, 14, 7),
            Background = new SolidColorBrush(Color.FromArgb(232, 22, 24, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                Opacity = 0.5,
                ShadowDepth = 4
            },
            Child = pillRoot
        };

        var toolbar = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = pillBorder
        };

        toolbar.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(toolbar).Handle;
            Win32Interop.HideFromAltTab(hwnd, noActivate: true);
        };

        _overlayWindow = overlay;
        _canvas = canvas;
        _toolbarWindow = toolbar;
        _toolbarRoot = pillRoot;

        RebuildToolbarContent();
    }

    private void RebuildToolbarContent()
    {
        if (_toolbarRoot is null) return;
        _toolbarRoot.Children.Clear();
        _ratioButtons.Clear();
        _durationLabel = null;
        _statusDot = null;
        _pauseResumeButton = null;

        if (Mode == OverlayMode.Selecting)
            BuildSelectionControls(_toolbarRoot);
        else
            BuildRecordingControls(_toolbarRoot);

        _toolbarWindow?.UpdateLayout();
    }

    private void BuildSelectionControls(StackPanel root)
    {
        foreach (var preset in Enum.GetValues<AspectRatioPreset>())
        {
            var captured = preset;
            var btn = CreatePillButton(preset.GetDisplayName(), isActive: AspectRatio == preset);
            btn.Margin = new Thickness(2, 0, 2, 0);
            btn.Click += (_, _) => ApplyAspectRatio(captured);
            _ratioButtons[preset] = btn;
            root.Children.Add(btn);
        }

        root.Children.Add(CreateSeparator());

        // Snap to Window button + popup menu
        var snapBtn = CreatePillButton("⊞ Snap", isActive: false, subtleBg: true);
        snapBtn.Click += (_, _) => OpenSnapMenu(snapBtn);
        root.Children.Add(snapBtn);

        root.Children.Add(CreateSeparator());

        // Countdown toggle
        _countdownButton = CreatePillButton(_settings.CountdownSeconds > 0 ? "⏱ 3s" : "⏱ Off", isActive: _settings.CountdownSeconds > 0, activeColor: Color.FromArgb(100, 255, 149, 0));
        _countdownButton.ToolTip = "Countdown sebelum mulai merekam (Off / 3 detik)";
        _countdownButton.Margin = new Thickness(2, 0, 2, 0);
        _countdownButton.Click += (_, _) =>
        {
            _settings.CountdownSeconds = _settings.CountdownSeconds == 0 ? 3 : 0;
            UpdateVisuals();
        };
        root.Children.Add(_countdownButton);

        // Cursor toggle
        _cursorButton = CreatePillButton("⌖", isActive: _settings.ShowCursor, activeColor: Color.FromArgb(110, 40, 130, 255));
        _cursorButton.ToolTip = "Tampilkan / sembunyikan kursor mouse";
        _cursorButton.Margin = new Thickness(2, 0, 2, 0);
        _cursorButton.Click += (_, _) =>
        {
            _settings.ShowCursor = !_settings.ShowCursor;
            UpdateVisuals();
        };
        root.Children.Add(_cursorButton);

        // Click highlight toggle
        _clickHighlightButton = CreatePillButton("◎", isActive: _settings.HighlightClicks, activeColor: Color.FromArgb(110, 235, 65, 130));
        _clickHighlightButton.ToolTip = "Highlight efek klik mouse";
        _clickHighlightButton.Margin = new Thickness(2, 0, 2, 0);
        _clickHighlightButton.Click += (_, _) =>
        {
            _settings.HighlightClicks = !_settings.HighlightClicks;
            UpdateVisuals();
        };
        root.Children.Add(_clickHighlightButton);

        root.Children.Add(CreateSeparator());

        // Record button
        var recBtn = CreateAccentButton("● Record", Color.FromRgb(245, 64, 56));
        recBtn.Margin = new Thickness(2, 0, 6, 0);
        recBtn.Click += (_, _) => StartRecordingRequested?.Invoke();
        root.Children.Add(recBtn);

        // Cancel button
        var cancelBtn = CreatePillButton("✕", isActive: false, subtleBg: true);
        cancelBtn.ToolTip = "Batal (Esc)";
        cancelBtn.Click += (_, _) => CancelRequested?.Invoke();
        root.Children.Add(cancelBtn);
    }

    private void BuildRecordingControls(StackPanel root)
    {
        if (_settings.AllowPanDuringRecording)
        {
            var grip = CreatePanGrip();
            grip.Margin = new Thickness(0, 0, 8, 0);
            root.Children.Add(grip);
        }

        _statusDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Mode == OverlayMode.Paused ? Colors.Orange : Color.FromRgb(255, 66, 56)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        root.Children.Add(_statusDot);

        _durationLabel = new TextBlock
        {
            Text = Mode == OverlayMode.Paused ? "PAUSED" : "00:00.0",
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 62,
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(_durationLabel);

        root.Children.Add(CreateSeparator());

        _pauseResumeButton = CreatePillButton(Mode == OverlayMode.Paused ? "▶ Resume" : "❚❚ Pause", isActive: false, subtleBg: true);
        _pauseResumeButton.Margin = new Thickness(0, 0, 6, 0);
        _pauseResumeButton.Click += (_, _) => TogglePauseRequested?.Invoke();
        root.Children.Add(_pauseResumeButton);

        var stopBtn = CreateAccentButton("■ Stop", Color.FromRgb(245, 64, 56));
        stopBtn.Click += (_, _) => StopRecordingRequested?.Invoke();
        root.Children.Add(stopBtn);
    }

    private void OpenSnapMenu(Button anchor)
    {
        if (TargetMonitor is null) return;
        var candidates = WindowSnapper.FetchCandidates(TargetMonitor);
        var menu = new ContextMenu();

        if (candidates.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Tidak ada jendela terbuka yang terdeteksi", IsEnabled = false });
        }
        else
        {
            var limit = Math.Min(candidates.Count, 12);
            for (var i = 0; i < limit; i++)
            {
                var c = candidates[i];
                var shortTitle = c.Title.Length > 42 ? c.Title[..42] + "…" : c.Title;
                var item = new MenuItem
                {
                    Header = $"{c.AppName} — {shortTitle} ({(int)c.LogicalLocalRect.Width}×{(int)c.LogicalLocalRect.Height})"
                };
                item.Click += (_, _) => SnapToWindow(c);
                menu.Items.Add(item);
            }
        }

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private Border CreatePanGrip()
    {
        var grip = new Border
        {
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ToolTip = "Seret untuk menggeser posisi frame saat merekam",
            Child = new TextBlock
            {
                Text = "⋮⋮",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        Win32Interop.POINT? lastPhys = null;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (Win32Interop.GetCursorPos(out var pt))
                lastPhys = pt;
            grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (_, _) =>
        {
            if (!grip.IsMouseCaptured || !lastPhys.HasValue || TargetMonitor is null) return;
            if (Win32Interop.GetCursorPos(out var cur))
            {
                var scale = Math.Max(1.0, TargetMonitor.DpiScale);
                var dx = (cur.X - lastPhys.Value.X) / scale;
                var dy = (cur.Y - lastPhys.Value.Y) / scale;
                lastPhys = cur;
                PanBy(dx, dy);
            }
        };
        grip.MouseLeftButtonUp += (_, _) =>
        {
            lastPhys = null;
            if (grip.IsMouseCaptured)
                grip.ReleaseMouseCapture();
        };

        return grip;
    }

    private void UpdateVisuals()
    {
        if (_canvas is not null)
        {
            _canvas.SelectionRect = SelectionRect;
            _canvas.OverlayMode = Mode;
            _canvas.InvalidateVisual();
        }

        foreach (var (preset, btn) in _ratioButtons)
        {
            var active = AspectRatio == preset;
            btn.Background = new SolidColorBrush(active ? Color.FromArgb(70, 255, 255, 255) : Colors.Transparent);
            btn.Foreground = new SolidColorBrush(active ? Colors.White : Color.FromArgb(190, 255, 255, 255));
        }

        if (_countdownButton is not null)
        {
            var on = _settings.CountdownSeconds > 0;
            _countdownButton.Content = on ? "⏱ 3s" : "⏱ Off";
            _countdownButton.Background = new SolidColorBrush(on ? Color.FromArgb(100, 255, 149, 0) : Color.FromArgb(24, 255, 255, 255));
            _countdownButton.Foreground = new SolidColorBrush(on ? Color.FromRgb(255, 180, 60) : Color.FromArgb(200, 255, 255, 255));
        }

        if (_cursorButton is not null)
        {
            _cursorButton.Background = new SolidColorBrush(_settings.ShowCursor ? Color.FromArgb(110, 40, 130, 255) : Color.FromArgb(24, 255, 255, 255));
            _cursorButton.Foreground = new SolidColorBrush(_settings.ShowCursor ? Colors.White : Color.FromArgb(130, 255, 255, 255));
        }

        if (_clickHighlightButton is not null)
        {
            _clickHighlightButton.Background = new SolidColorBrush(_settings.HighlightClicks ? Color.FromArgb(110, 235, 65, 130) : Color.FromArgb(24, 255, 255, 255));
            _clickHighlightButton.Foreground = new SolidColorBrush(_settings.HighlightClicks ? Colors.White : Color.FromArgb(130, 255, 255, 255));
        }
    }

    private void RepositionToolbar()
    {
        if (_toolbarWindow is null || TargetMonitor is null) return;
        _toolbarWindow.UpdateLayout();

        var pillW = _toolbarWindow.ActualWidth > 10 ? _toolbarWindow.ActualWidth : 520;
        var pillH = _toolbarWindow.ActualHeight > 10 ? _toolbarWindow.ActualHeight : 44;

        var globalSel = SelectionGlobalLogicalRect;
        var work = TargetMonitor.LogicalWorkArea;
        const double gap = 12;

        var x = globalSel.MidX - pillW / 2.0;
        x = Math.Clamp(x, work.MinX + 12, Math.Max(work.MinX + 12, work.MaxX - pillW - 12));

        // Prefer placing pill cleanly below capture box; flip above if near bottom edge.
        var y = globalSel.MaxY + gap;
        if (y + pillH > work.MaxY - 8)
            y = globalSel.MinY - pillH - gap;
        if (y < work.MinY + 8)
            y = work.MaxY - pillH - 12;

        _toolbarWindow.Left = Math.Round(x);
        _toolbarWindow.Top = Math.Round(y);
        if (_toolbarWindow.IsVisible)
            Win32Interop.BringTopmostNoActivate(new WindowInteropHelper(_toolbarWindow).Handle);
    }

    private static UIElement CreateSeparator() => new Border
    {
        Width = 1,
        Height = 18,
        Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
        Margin = new Thickness(7, 0, 7, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button CreatePillButton(string text, bool isActive, bool subtleBg = false, Color? activeColor = null)
    {
        var bg = isActive
            ? (activeColor ?? Color.FromArgb(70, 255, 255, 255))
            : (subtleBg ? Color.FromArgb(30, 255, 255, 255) : Colors.Transparent);

        return new Button
        {
            Content = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(isActive || subtleBg ? Colors.White : Color.FromArgb(190, 255, 255, 255)),
            Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 4, 9, 4),
            Cursor = Cursors.Hand,
            Style = CreateFlatButtonStyle()
        };
    }

    private static Button CreateAccentButton(string text, Color bg) => new()
    {
        Content = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(bg),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(13, 5, 13, 5),
        Cursor = Cursors.Hand,
        Style = CreateFlatButtonStyle()
    };

    private static Style CreateFlatButtonStyle()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);

        template.VisualTree = borderFactory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}

/// <summary>
/// Interactive full-screen WPF drawing canvas for the selection frame:
/// outer dimmed scrim, exterior border, 8 resize handles, rule-of-thirds guides, and live W×H dimension pill.
/// </summary>
internal sealed class SelectionCanvasControl : FrameworkElement
{
    private const double HandleHitRadius = 11.0;
    private const double CreateDragThreshold = 8.0;

    private enum DragKind { None, Move, Resize, Create }
    private DragKind _dragKind = DragKind.None;
    private ResizeHandle _activeHandle;
    private Point _dragStartPoint;
    private RectD _dragOriginalRect;
    private bool _hasExceededCreateThreshold;

    public RectD SelectionRect { get; set; }
    public OverlayMode OverlayMode { get; set; } = OverlayMode.Selecting;
    public Func<AspectRatioPreset>? AspectRatioProvider { get; set; }
    public event Action<RectD>? SelectionChanged;

    public SelectionCanvasControl()
    {
        Focusable = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var fullRect = new Rect(0, 0, ActualWidth, ActualHeight);
        var selRect = new Rect(SelectionRect.X, SelectionRect.Y, SelectionRect.Width, SelectionRect.Height);

        // 1. Dimmed outer scrim + nearly-invisible (alpha=1) hit-testable interior during Selection
        // so Windows WS_EX_LAYERED hit-testing captures drag-to-move clicks inside selRect instead of passing through.
        if (OverlayMode is OverlayMode.Selecting or OverlayMode.Countdown)
        {
            var combined = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(fullRect),
                new RectangleGeometry(selRect)
            );
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(98, 0, 0, 0)), null, combined);

            if (OverlayMode == OverlayMode.Selecting)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, selRect);
            }
        }

        // 2. Exterior border drawn outside selectionRect so interior pixels stay exact
        var strokeW = OverlayMode == OverlayMode.Selecting ? 2.0 : 2.5;
        var offset = strokeW / 2.0 + 1.0;
        var borderRect = new Rect(
            selRect.X - offset,
            selRect.Y - offset,
            selRect.Width + offset * 2,
            selRect.Height + offset * 2
        );

        var (strokeColor, dash) = OverlayMode switch
        {
            OverlayMode.Selecting => (Color.FromArgb(242, 255, 255, 255), new DashStyle(new[] { 6.0, 4.0 }, 0)),
            OverlayMode.Countdown => (Colors.Orange, new DashStyle(new[] { 5.0, 4.0 }, 0)),
            OverlayMode.Recording => (Color.FromArgb(245, 255, 68, 58), null),
            OverlayMode.Paused => (Color.FromArgb(245, 255, 184, 41), new DashStyle(new[] { 7.0, 4.0 }, 0)),
            _ => (Colors.White, null)
        };

        var pen = new Pen(new SolidColorBrush(strokeColor), strokeW) { DashStyle = dash };
        dc.DrawRectangle(null, pen, borderRect);

        // 3. Resize handles, rule-of-thirds guides, and W×H dimension pill during Selection
        if (OverlayMode == OverlayMode.Selecting)
        {
            if (_dragKind != DragKind.None)
                DrawRuleOfThirds(dc, selRect);

            DrawHandles(dc);
            DrawDimensionPill(dc, selRect);
        }
    }

    private static void DrawRuleOfThirds(DrawingContext dc, Rect sel)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 0.75);
        for (var i = 1; i <= 2; i++)
        {
            var x = sel.Left + sel.Width * i / 3.0;
            var y = sel.Top + sel.Height * i / 3.0;
            dc.DrawLine(pen, new Point(x, sel.Top), new Point(x, sel.Bottom));
            dc.DrawLine(pen, new Point(sel.Left, y), new Point(sel.Right, y));
        }
    }

    private void DrawHandles(DrawingContext dc)
    {
        var strokePen = new Pen(new SolidColorBrush(Color.FromRgb(36, 143, 255)), 1.6);
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var pt = GetHandlePoint(handle, SelectionRect);
            var isCorner = handle is ResizeHandle.TopLeft or ResizeHandle.TopRight or ResizeHandle.BottomLeft or ResizeHandle.BottomRight;
            var r = isCorner ? 5.5 : 4.2;
            dc.DrawEllipse(Brushes.White, strokePen, pt, r, r);
        }
    }

    private void DrawDimensionPill(DrawingContext dc, Rect sel)
    {
        var text = $"{(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)}";
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12,
            Brushes.White,
            dpi
        );

        const double padH = 10;
        const double padV = 4;
        var pillW = formatted.Width + padH * 2;
        var pillH = formatted.Height + padV * 2;

        var x = sel.Left;
        var y = sel.Top - pillH - 8;
        if (y < 6)
        {
            y = sel.Top + 8;
            x = sel.Left + 8;
        }

        var pillRect = new Rect(x, y, pillW, pillH);
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(196, 0, 0, 0)),
            new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 1),
            pillRect,
            pillH / 2.0,
            pillH / 2.0
        );
        dc.DrawText(formatted, new Point(pillRect.X + padH, pillRect.Y + padV));
    }

    private static Point GetHandlePoint(ResizeHandle handle, RectD r) => handle switch
    {
        ResizeHandle.TopLeft => new Point(r.MinX, r.MinY),
        ResizeHandle.Top => new Point(r.MidX, r.MinY),
        ResizeHandle.TopRight => new Point(r.MaxX, r.MinY),
        ResizeHandle.Right => new Point(r.MaxX, r.MidY),
        ResizeHandle.BottomRight => new Point(r.MaxX, r.MaxY),
        ResizeHandle.Bottom => new Point(r.MidX, r.MaxY),
        ResizeHandle.BottomLeft => new Point(r.MinX, r.MaxY),
        ResizeHandle.Left => new Point(r.MinX, r.MidY),
        _ => new Point(r.MinX, r.MinY)
    };

    private ResizeHandle? HitTestHandle(Point pt)
    {
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var hp = GetHandlePoint(handle, SelectionRect);
            var dx = pt.X - hp.X;
            var dy = pt.Y - hp.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= HandleHitRadius)
                return handle;
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (OverlayMode != OverlayMode.Selecting) return;

        var pt = e.GetPosition(this);
        if (HitTestHandle(pt) is ResizeHandle h)
        {
            _dragKind = DragKind.Resize;
            _activeHandle = h;
            _dragStartPoint = pt;
            _dragOriginalRect = SelectionRect;
            CaptureMouse();
        }
        else if (pt.X >= SelectionRect.MinX && pt.X <= SelectionRect.MaxX &&
                 pt.Y >= SelectionRect.MinY && pt.Y <= SelectionRect.MaxY)
        {
            _dragKind = DragKind.Move;
            _dragStartPoint = pt;
            _dragOriginalRect = SelectionRect;
            CaptureMouse();
        }
        else
        {
            _dragKind = DragKind.Create;
            _hasExceededCreateThreshold = false;
            _dragStartPoint = pt;
            _dragOriginalRect = SelectionRect;
            CaptureMouse();
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (OverlayMode != OverlayMode.Selecting) return;

        var pt = e.GetPosition(this);
        if (_dragKind == DragKind.None)
        {
            Cursor = HitTestHandle(pt) switch
            {
                ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
                ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
                ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
                ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
                null when pt.X >= SelectionRect.MinX && pt.X <= SelectionRect.MaxX &&
                          pt.Y >= SelectionRect.MinY && pt.Y <= SelectionRect.MaxY => Cursors.SizeAll,
                _ => Cursors.Cross
            };
            return;
        }

        var bounds = new RectD(0, 0, ActualWidth, ActualHeight);
        var delta = new SizeD(pt.X - _dragStartPoint.X, pt.Y - _dragStartPoint.Y);

        if (_dragKind == DragKind.Create)
        {
            if (!_hasExceededCreateThreshold)
            {
                if (Math.Abs(delta.Width) < CreateDragThreshold && Math.Abs(delta.Height) < CreateDragThreshold)
                    return;
                _hasExceededCreateThreshold = true;
            }
            SelectionRect = CaptureGeometry.CreateFromDrag(
                _dragStartPoint.X,
                _dragStartPoint.Y,
                pt.X,
                pt.Y,
                AspectRatioProvider?.Invoke() ?? AspectRatioPreset.Free,
                bounds
            );
        }
        else if (_dragKind == DragKind.Move)
        {
            SelectionRect = CaptureGeometry.Pan(_dragOriginalRect, delta, bounds);
        }
        else
        {
            SelectionRect = CaptureGeometry.Resize(_dragOriginalRect, _activeHandle, delta, AspectRatioProvider?.Invoke() ?? AspectRatioPreset.Free, bounds);
        }

        InvalidateVisual();
        SelectionChanged?.Invoke(SelectionRect);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragKind = DragKind.None;
        _hasExceededCreateThreshold = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
    }
}
