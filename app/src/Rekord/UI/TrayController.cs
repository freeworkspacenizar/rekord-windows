using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Rekord.App;
using Rekord.Native;
using Rekord.SystemServices;
using RekordCore;
using DrawingColor = System.Drawing.Color;
using WinForms = System.Windows.Forms;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace Rekord.UI;

/// <summary>
/// Manages the Windows System Tray NotifyIcon, dynamically rendered state icon,
/// native balloon notifications, and the floating quick-control popover window.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppCoordinator _coordinator;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private Icon? _currentIcon;
    private TrayPopupWindow? _popup;

    public TrayController(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        _notifyIcon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = "Rekord (Ctrl+Shift+6)"
        };

        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add("Pilih Area & Rekam (Ctrl+Shift+6)", null, (_, _) => _coordinator.BeginSelection());
        contextMenu.Items.Add("Settings…", null, (_, _) => _coordinator.OpenSettings());
        contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        contextMenu.Items.Add("Quit", null, (_, _) => _coordinator.Shutdown());
        _notifyIcon.ContextMenuStrip = contextMenu;

        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                TogglePopup();
        };

        SyncStatus();
    }

    public void SyncStatus()
    {
        var (dotColor, tooltip) = _coordinator.SessionState switch
        {
            AppSessionStateKind.Recording => (
                DrawingColor.FromArgb(245, 64, 56),
                $"Rekord — Merekam {RecordingTimeline.FormatDuration(_coordinator.ElapsedSeconds)}"
            ),
            AppSessionStateKind.Paused => (
                DrawingColor.FromArgb(255, 165, 0),
                "Rekord — Dijeda"
            ),
            AppSessionStateKind.Countdown => (
                DrawingColor.FromArgb(255, 149, 0),
                $"Rekord — Countdown {_coordinator.CountdownRemaining}…"
            ),
            AppSessionStateKind.Stopping => (
                DrawingColor.FromArgb(255, 165, 0),
                "Rekord — Menyelesaikan rekaman…"
            ),
            AppSessionStateKind.QuickExporting => (
                DrawingColor.FromArgb(56, 152, 255),
                $"Rekord — Export {(int)(_coordinator.QuickExportProgress * 100)}%"
            ),
            _ => (
                DrawingColor.FromArgb(245, 64, 56),
                $"Rekord ({GlobalHotkeyManager.ShortcutDisplayLabel})"
            )
        };

        var nextIcon = CreateTrayIcon(dotColor, isRecording: _coordinator.SessionState == AppSessionStateKind.Recording);
        _notifyIcon.Icon = nextIcon;
        _currentIcon?.Dispose();
        _currentIcon = nextIcon;

        // NotifyIcon.Text is limited to 63 characters on Windows
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _popup?.RefreshUI();
    }

    public void ShowNotification(string title, string body)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(3500, title, body, WinForms.ToolTipIcon.Info);
        }
        catch { }
    }

    public void OpenPopup()
    {
        if (_popup is not null && _popup.IsVisible)
        {
            _popup.RefreshUI();
            _popup.Activate();
            return;
        }

        _popup = new TrayPopupWindow(_coordinator);
        _popup.Closed += (_, _) => _popup = null;
        PositionPopupNearTray(_popup);
        _popup.Show();
        _popup.Activate();
    }

    public void ClosePopup()
    {
        if (_popup is not null)
        {
            try { _popup.Close(); } catch { }
            _popup = null;
        }
    }

    private void TogglePopup()
    {
        if (_popup is not null && _popup.IsVisible)
            ClosePopup();
        else
            OpenPopup();
    }

    private static void PositionPopupNearTray(Window popup)
    {
        var monitor = MonitorInfo.FindMonitorAtCursor();
        if (monitor is null)
        {
            popup.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        const double width = 312;
        const double estHeight = 320;
        var work = monitor.LogicalWorkArea;

        double targetX = work.MaxX - width - 12;
        if (Win32Interop.GetCursorPos(out var pt))
        {
            var cursorLogicalX = pt.X / Math.Max(1.0, monitor.DpiScale);
            targetX = Math.Clamp(cursorLogicalX - width / 2.0, work.MinX + 12, Math.Max(work.MinX + 12, work.MaxX - width - 12));
        }

        popup.Left = Math.Round(targetX);
        popup.Top = Math.Round(Math.Max(work.MinY + 12, work.MaxY - estHeight - 12));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateTrayIcon(DrawingColor centerColor, bool isRecording)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DrawingColor.Transparent);

        // Dark charcoal squircle background
        using (var bgBrush = new SolidBrush(DrawingColor.FromArgb(255, 28, 30, 38)))
        using (var path = CreateRoundedRect(2, 2, 28, 28, 7))
        {
            g.FillPath(bgBrush, path);
            using var rimPen = new System.Drawing.Pen(DrawingColor.FromArgb(80, 255, 255, 255), 1.2f);
            g.DrawPath(rimPen, path);
        }

        // Crisp white corner viewfinder brackets
        using (var bracketPen = new System.Drawing.Pen(DrawingColor.FromArgb(235, 255, 255, 255), 2.0f))
        {
            bracketPen.StartCap = LineCap.Round;
            bracketPen.EndCap = LineCap.Round;
            const int m = 7;
            const int arm = 5;
            // Top-left
            g.DrawLines(bracketPen, new[] { new PointF(m, m + arm), new PointF(m, m), new PointF(m + arm, m) });
            // Top-right
            g.DrawLines(bracketPen, new[] { new PointF(32 - m - arm, m), new PointF(32 - m, m), new PointF(32 - m, m + arm) });
            // Bottom-left
            g.DrawLines(bracketPen, new[] { new PointF(m, 32 - m - arm), new PointF(m, 32 - m), new PointF(m + arm, 32 - m) });
            // Bottom-right
            g.DrawLines(bracketPen, new[] { new PointF(32 - m - arm, 32 - m), new PointF(32 - m, 32 - m), new PointF(32 - m, 32 - m - arm) });
        }

        // Center status dot (larger when actively recording)
        var r = isRecording ? 6.5f : 5.0f;
        using (var dotBrush = new SolidBrush(centerColor))
        {
            g.FillEllipse(dotBrush, 16 - r, 16 - r, r * 2, r * 2);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        ClosePopup();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}

internal sealed class TrayPopupWindow : Window
{
    private readonly AppCoordinator _coordinator;
    private readonly StackPanel _actionContainer = new();
    private readonly CheckBox _quickExportCheck;
    private readonly CheckBox _autoCopyCheck;
    private readonly Border _lastExportCard;
    private readonly TextBlock _lastExportName = new();
    private readonly TextBlock _lastExportMeta = new();
    private readonly TextBlock _errorLabel;

    public TrayPopupWindow(AppCoordinator coordinator)
    {
        _coordinator = coordinator;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 312;
        SizeToContent = SizeToContent.Height;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32Interop.HideFromAltTab(hwnd, noActivate: false);
            Win32Interop.SetExcludeFromCapture(hwnd, true);
        };
        Deactivated += (_, _) =>
        {
            try { Close(); } catch { }
        };

        var root = new StackPanel { Margin = new Thickness(14) };

        // Header
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = new SolidColorBrush(WpfColor.FromRgb(245, 69, 61)),
            Margin = new Thickness(0, 0, 8, 0)
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Rekord",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = WpfBrushes.White
        });
        header.Children.Add(titleStack);

        var hotkeyBadge = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(WpfColor.FromArgb(40, 255, 255, 255)),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = GlobalHotkeyManager.ShortcutDisplayLabel,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(200, 255, 255, 255))
            }
        };
        DockPanel.SetDock(hotkeyBadge, Dock.Right);
        header.Children.Add(hotkeyBadge);
        root.Children.Add(header);

        // Dynamic Action Section
        root.Children.Add(_actionContainer);
        root.Children.Add(MakeDivider());

        // Quick Toggles
        _quickExportCheck = new CheckBox
        {
            Foreground = WpfBrushes.White,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 6),
            IsChecked = _coordinator.Settings.QuickExportEnabled
        };
        _quickExportCheck.Click += (_, _) =>
        {
            _coordinator.Settings.QuickExportEnabled = _quickExportCheck.IsChecked == true;
            RefreshUI();
        };
        root.Children.Add(_quickExportCheck);

        _autoCopyCheck = new CheckBox
        {
            Content = "Auto-Copy ke Clipboard",
            Foreground = WpfBrushes.White,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 2),
            IsChecked = _coordinator.Settings.AutoCopyToClipboard
        };
        _autoCopyCheck.Click += (_, _) =>
        {
            _coordinator.Settings.AutoCopyToClipboard = _autoCopyCheck.IsChecked == true;
        };
        root.Children.Add(_autoCopyCheck);

        // Last Export Card
        _lastExportName.Foreground = WpfBrushes.White;
        _lastExportName.FontSize = 11.5;
        _lastExportName.FontWeight = FontWeights.SemiBold;
        _lastExportName.TextTrimming = TextTrimming.CharacterEllipsis;

        _lastExportMeta.Foreground = new SolidColorBrush(WpfColor.FromArgb(170, 255, 255, 255));
        _lastExportMeta.FontSize = 10.5;

        var copyBtn = MakeSmallButton("Copy");
        copyBtn.Margin = new Thickness(6, 0, 4, 0);
        copyBtn.Click += (_, _) =>
        {
            if (_coordinator.LastExport is LastExportInfo info)
                _coordinator.CopyMediaToClipboard(info.FilePath);
        };

        var folderBtn = MakeSmallButton("Folder");
        folderBtn.Click += (_, _) =>
        {
            if (_coordinator.LastExport is LastExportInfo info)
                AppCoordinator.RevealInExplorer(info.FilePath);
        };

        var cardRow = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(copyBtn);
        btns.Children.Add(folderBtn);
        DockPanel.SetDock(btns, Dock.Right);
        cardRow.Children.Add(btns);

        var metaCol = new StackPanel();
        metaCol.Children.Add(_lastExportName);
        metaCol.Children.Add(_lastExportMeta);
        cardRow.Children.Add(metaCol);

        var cardStack = new StackPanel();
        cardStack.Children.Add(new TextBlock
        {
            Text = "Ekspor Terakhir",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WpfColor.FromArgb(160, 255, 255, 255)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        cardStack.Children.Add(cardRow);

        _lastExportCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(WpfColor.FromArgb(26, 255, 255, 255)),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 10, 0, 0),
            Child = cardStack,
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(_lastExportCard);

        // Error Banner
        _errorLabel = new TextBlock
        {
            Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 110, 100)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(_errorLabel);

        root.Children.Add(MakeDivider());

        // Footer
        var footer = new DockPanel();
        var settingsBtn = MakeLinkButton("⚙ Settings…", WpfBrushes.White);
        settingsBtn.Click += (_, _) => _coordinator.OpenSettings();
        footer.Children.Add(settingsBtn);

        var quitBtn = MakeLinkButton("Quit", new SolidColorBrush(WpfColor.FromArgb(170, 255, 255, 255)));
        quitBtn.Click += (_, _) => _coordinator.Shutdown();
        DockPanel.SetDock(quitBtn, Dock.Right);
        footer.Children.Add(quitBtn);
        root.Children.Add(footer);

        Content = new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(WpfColor.FromRgb(28, 30, 36)),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(45, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, Opacity = 0.55, ShadowDepth = 4 },
            Child = root
        };

        RefreshUI();
    }

    public void RefreshUI()
    {
        var fmtName = _coordinator.Settings.DefaultFormat.GetDisplayName();
        _quickExportCheck.Content = $"Quick Export ({fmtName}) — simpan langsung";
        _quickExportCheck.IsChecked = _coordinator.Settings.QuickExportEnabled;
        _autoCopyCheck.IsChecked = _coordinator.Settings.AutoCopyToClipboard;

        _actionContainer.Children.Clear();

        switch (_coordinator.SessionState)
        {
            case AppSessionStateKind.Idle:
            case AppSessionStateKind.Previewing:
            {
                var btn = MakePrimaryButton("◎  Pilih Area & Rekam");
                btn.Click += (_, _) => _coordinator.BeginSelection();
                _actionContainer.Children.Add(btn);
                break;
            }
            case AppSessionStateKind.Selecting:
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var startBtn = MakePrimaryButton("Mulai Rekam");
                startBtn.Click += (_, _) => _coordinator.StartRecordingFromSelection();
                Grid.SetColumn(startBtn, 0);

                var cancelBtn = MakeSecondaryButton("Batal");
                cancelBtn.Click += (_, _) => _coordinator.CancelSession();
                Grid.SetColumn(cancelBtn, 2);

                row.Children.Add(startBtn);
                row.Children.Add(cancelBtn);
                _actionContainer.Children.Add(row);
                break;
            }
            case AppSessionStateKind.Countdown:
            {
                var row = new DockPanel();
                var cancelBtn = MakeSmallButton("Batal");
                cancelBtn.Click += (_, _) => _coordinator.CancelSession();
                DockPanel.SetDock(cancelBtn, Dock.Right);
                row.Children.Add(cancelBtn);
                row.Children.Add(new TextBlock
                {
                    Text = $"Memulai dalam {_coordinator.CountdownRemaining}…",
                    Foreground = WpfBrushes.White,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center
                });
                _actionContainer.Children.Add(row);
                break;
            }
            case AppSessionStateKind.Recording:
            case AppSessionStateKind.Paused:
            {
                var isPaused = _coordinator.SessionState == AppSessionStateKind.Paused;
                var statusRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var timeTxt = new TextBlock
                {
                    Text = RecordingTimeline.FormatDuration(_coordinator.ElapsedSeconds, includeTenths: true),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = WpfBrushes.White
                };
                DockPanel.SetDock(timeTxt, Dock.Right);
                statusRow.Children.Add(timeTxt);

                var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = isPaused ? WpfBrushes.Orange : new SolidColorBrush(WpfColor.FromRgb(245, 64, 56)),
                    Margin = new Thickness(0, 0, 6, 0)
                });
                left.Children.Add(new TextBlock
                {
                    Text = isPaused ? "Rekaman Dijeda" : "Sedang Merekam…",
                    Foreground = WpfBrushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                });
                statusRow.Children.Add(left);
                _actionContainer.Children.Add(statusRow);

                var btnRow = new Grid();
                btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var pauseBtn = MakeSecondaryButton(isPaused ? "▶ Resume" : "❚❚ Pause");
                pauseBtn.Click += (_, _) => _coordinator.TogglePauseResume();
                Grid.SetColumn(pauseBtn, 0);

                var stopBtn = MakePrimaryButton("■ Stop");
                stopBtn.Click += (_, _) => _coordinator.StopRecording();
                Grid.SetColumn(stopBtn, 2);

                btnRow.Children.Add(pauseBtn);
                btnRow.Children.Add(stopBtn);
                _actionContainer.Children.Add(btnRow);
                break;
            }
            case AppSessionStateKind.Stopping:
            {
                _actionContainer.Children.Add(new TextBlock
                {
                    Text = "Menyelesaikan rekaman…",
                    Foreground = WpfBrushes.White,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 4, 0, 4)
                });
                break;
            }
            case AppSessionStateKind.QuickExporting:
            {
                var pct = (int)(_coordinator.QuickExportProgress * 100);
                _actionContainer.Children.Add(new TextBlock
                {
                    Text = $"Quick Export mengonversi {fmtName} ({pct}%)…",
                    Foreground = WpfBrushes.White,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                _actionContainer.Children.Add(new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = _coordinator.QuickExportProgress,
                    Height = 6
                });
                break;
            }
        }

        if (_coordinator.LastExport is LastExportInfo info)
        {
            _lastExportCard.Visibility = Visibility.Visible;
            _lastExportName.Text = Path.GetFileName(info.FilePath);
            _lastExportMeta.Text = $"{info.Format.GetDisplayName()} • {AppCoordinator.FormatBytes(info.FileSize)}";
        }
        else
        {
            _lastExportCard.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(_coordinator.LastError))
        {
            _errorLabel.Text = _coordinator.LastError;
            _errorLabel.Visibility = Visibility.Visible;
        }
        else
        {
            _errorLabel.Visibility = Visibility.Collapsed;
        }
    }

    private static Border MakeDivider() => new()
    {
        Height = 1,
        Background = new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)),
        Margin = new Thickness(0, 10, 0, 10)
    };

    private static Button MakePrimaryButton(string text) => new()
    {
        Content = text,
        Foreground = WpfBrushes.White,
        Background = new SolidColorBrush(WpfColor.FromRgb(245, 69, 61)),
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(10, 8, 10, 8),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };

    private static Button MakeSecondaryButton(string text) => new()
    {
        Content = text,
        Foreground = WpfBrushes.White,
        Background = new SolidColorBrush(WpfColor.FromArgb(45, 255, 255, 255)),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(10, 7, 10, 7),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };

    private static Button MakeSmallButton(string text) => new()
    {
        Content = text,
        Foreground = WpfBrushes.White,
        Background = new SolidColorBrush(WpfColor.FromArgb(40, 255, 255, 255)),
        FontSize = 11,
        Padding = new Thickness(7, 3, 7, 3),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };

    private static Button MakeLinkButton(string text, System.Windows.Media.Brush fg) => new()
    {
        Content = text,
        Foreground = fg,
        Background = WpfBrushes.Transparent,
        FontSize = 12,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(2),
        Cursor = Cursors.Hand
    };
}
