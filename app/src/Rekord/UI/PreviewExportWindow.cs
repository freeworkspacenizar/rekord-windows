using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rekord.App;
using Rekord.Capture;
using RekordCore;

namespace Rekord.UI;

/// <summary>
/// Studio Preview & Export window with looping video playback, interactive trim scrubber,
/// and WebP (default) / 2-pass GIF / MP4 export inspector.
/// </summary>
public sealed class PreviewExportWindow : Window
{
    private readonly RecordingResult _recording;
    private readonly AppSettings _settings;
    private readonly ExportEngine _exportEngine;
    private readonly Action<string, long, ExportFormat> _onExportCompleted;
    private readonly Action<string> _onCopyMedia;

    private readonly MediaElement _player;
    private readonly DispatcherTimer _playbackTimer;
    private readonly TrimTimelineScrubber _scrubber;

    private readonly TextBlock _badgeText = new();
    private readonly TextBlock _trimTimeText = new();
    private readonly Button _playPauseBtn = new();

    private readonly TextBlock _webpSavingsBadge;
    private readonly TextBlock _dimensionsText = new();
    private readonly TextBlock _qualityHeader = new();
    private readonly StackPanel _customWidthRow;
    private readonly TextBox _customWidthBox;
    private readonly CheckBox _loopCheck;

    private readonly StackPanel _progressPanel;
    private readonly TextBlock _progressPctText = new();
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _errorText;
    private readonly Border _statusBannerCard;
    private readonly TextBlock _statusBannerText = new();
    private readonly StackPanel _statusBannerActions;

    private readonly Button _exportPrimaryBtn;
    private readonly Button _copyBtn;
    private readonly Button _saveMp4Btn;
    private readonly Button _cancelDiscardBtn;

    private readonly Dictionary<ExportFormat, Button> _formatBtns = new();
    private readonly Dictionary<int, Button> _fpsBtns = new();
    private readonly Dictionary<ExportScaleMode, Button> _scaleBtns = new();
    private readonly Dictionary<ExportQualityPreset, (Button Btn, TextBlock Subtitle)> _qualityCards = new();

    private double _trimStart;
    private double _trimEnd;
    private double _currentTime;
    private bool _isPlaying = true;

    private ExportFormat _format;
    private int _fps;
    private ExportScaleMode _scaleMode;
    private ExportQualityPreset _quality;
    private bool _loopForever;

    private bool _isExporting;
    private string? _exportedPath;
    private ExportFormat? _exportedFormat;

    public PreviewExportWindow(
        RecordingResult recording,
        AppSettings settings,
        ExportEngine exportEngine,
        Action<string, long, ExportFormat> onExportCompleted,
        Action<string> onCopyMedia)
    {
        _recording = recording;
        _settings = settings;
        _exportEngine = exportEngine;
        _onExportCompleted = onExportCompleted;
        _onCopyMedia = onCopyMedia;

        _trimStart = 0;
        _trimEnd = recording.Duration;
        _format = settings.DefaultFormat;
        _fps = settings.DefaultFps;
        _scaleMode = settings.DefaultScaleMode;
        _quality = settings.DefaultQuality;
        _loopForever = settings.DefaultLoopForever;

        Title = "Rekord — Preview & Export";
        Width = 900;
        Height = 650;
        MinWidth = 780;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(23, 24, 28));
        Foreground = Brushes.White;

        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });

        // === LEFT: Preview Player + Trim Scrubber ===
        var leftGrid = new Grid { Margin = new Thickness(18) };
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _player = new MediaElement
        {
            Source = new Uri(recording.VideoPath, UriKind.Absolute),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform,
            ScrubbingEnabled = true
        };
        _player.MediaEnded += (_, _) =>
        {
            SeekTo(_trimStart);
            if (_isPlaying) _player.Play();
        };

        var playerContainer = new Grid();
        playerContainer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(18, 19, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Child = _player
        });

        _badgeText.FontFamily = new FontFamily("Consolas");
        _badgeText.FontSize = 11;
        _badgeText.FontWeight = FontWeights.SemiBold;
        _badgeText.Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));

        playerContainer.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0)),
            Child = _badgeText
        });
        Grid.SetRow(playerContainer, 0);
        leftGrid.Children.Add(playerContainer);

        // Trim Timeline Box
        var trimBox = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
            Padding = new Thickness(12)
        };
        var trimStack = new StackPanel();
        var trimHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        _playPauseBtn.Content = "❚❚";
        _playPauseBtn.Width = 28;
        _playPauseBtn.Height = 28;
        _playPauseBtn.Foreground = Brushes.White;
        _playPauseBtn.Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
        _playPauseBtn.BorderThickness = new Thickness(0);
        _playPauseBtn.Cursor = Cursors.Hand;
        _playPauseBtn.Click += (_, _) => TogglePlayPause();
        trimHeader.Children.Add(_playPauseBtn);

        trimHeader.Children.Add(new TextBlock
        {
            Text = "Trim",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });

        _trimTimeText.FontFamily = new FontFamily("Consolas");
        _trimTimeText.FontSize = 12;
        _trimTimeText.Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        _trimTimeText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_trimTimeText, Dock.Right);
        trimHeader.Children.Add(_trimTimeText);
        trimStack.Children.Add(trimHeader);

        _scrubber = new TrimTimelineScrubber(recording.Duration)
        {
            Height = 34
        };
        _scrubber.TrimChanged += (start, end, seekTarget) =>
        {
            _trimStart = start;
            _trimEnd = end;
            SeekTo(seekTarget);
            UpdateInspectorUI();
        };
        _scrubber.SeekRequested += t => SeekTo(t);
        trimStack.Children.Add(_scrubber);
        trimBox.Child = trimStack;
        Grid.SetRow(trimBox, 2);
        leftGrid.Children.Add(trimBox);

        Grid.SetColumn(leftGrid, 0);
        mainGrid.Children.Add(leftGrid);

        // Vertical Divider
        var vDiv = new Border { Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)) };
        Grid.SetColumn(vDiv, 1);
        mainGrid.Children.Add(vDiv);

        // === RIGHT: Export Inspector ===
        var rightHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)),
            Padding = new Thickness(18)
        };
        var rightDock = new DockPanel();

        // Bottom Actions
        var actionStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        _progressPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        var progHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        _progressPctText.FontFamily = new FontFamily("Consolas");
        _progressPctText.FontSize = 11;
        _progressPctText.Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        DockPanel.SetDock(_progressPctText, Dock.Right);
        progHeader.Children.Add(_progressPctText);
        progHeader.Children.Add(new TextBlock { Text = "Mengonversi dengan FFmpeg…", FontSize = 11.5, Foreground = Brushes.White });
        _progressPanel.Children.Add(progHeader);
        _progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
        _progressPanel.Children.Add(_progressBar);
        actionStack.Children.Add(_progressPanel);

        _errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 110, 100)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };
        actionStack.Children.Add(_errorText);

        var bannerStack = new StackPanel();
        _statusBannerText.Foreground = new SolidColorBrush(Color.FromRgb(115, 235, 158));
        _statusBannerText.FontSize = 11.5;
        _statusBannerText.TextWrapping = TextWrapping.Wrap;
        bannerStack.Children.Add(_statusBannerText);

        _statusBannerActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var revealBtn = MakeSmallBtn("Reveal in Explorer");
        revealBtn.Margin = new Thickness(0, 0, 6, 0);
        revealBtn.Click += (_, _) =>
        {
            if (_exportedPath is not null)
                AppCoordinator.RevealInExplorer(_exportedPath);
        };
        var copyAgainBtn = MakeSmallBtn("Copy Lagi");
        copyAgainBtn.Click += (_, _) =>
        {
            if (_exportedPath is not null)
            {
                _onCopyMedia(_exportedPath);
                var fmt = _exportedFormat ?? _format;
                ShowStatusBanner($"{fmt.GetDisplayName()} disalin ulang ke clipboard!", showActions: true);
            }
        };
        _statusBannerActions.Children.Add(revealBtn);
        _statusBannerActions.Children.Add(copyAgainBtn);
        bannerStack.Children.Add(_statusBannerActions);

        _statusBannerCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(34, 72, 220, 130)),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = bannerStack,
            Visibility = Visibility.Collapsed
        };
        actionStack.Children.Add(_statusBannerCard);

        _exportPrimaryBtn = new Button
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(245, 69, 61)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(10, 9, 10, 9),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _exportPrimaryBtn.Click += async (_, _) => await ExportAnimationAsync(copyToClipboard: false);
        actionStack.Children.Add(_exportPrimaryBtn);

        var secondaryRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _copyBtn = MakeSecondaryBtn("Copy WebP");
        _copyBtn.Click += async (_, _) => await ExportAnimationAsync(copyToClipboard: true);
        Grid.SetColumn(_copyBtn, 0);

        _saveMp4Btn = MakeSecondaryBtn("Save MP4");
        _saveMp4Btn.Click += async (_, _) => await ExportMp4Async();
        Grid.SetColumn(_saveMp4Btn, 2);

        secondaryRow.Children.Add(_copyBtn);
        secondaryRow.Children.Add(_saveMp4Btn);
        actionStack.Children.Add(secondaryRow);

        _cancelDiscardBtn = new Button
        {
            Content = "Discard & Close",
            Foreground = new SolidColorBrush(Color.FromArgb(165, 255, 255, 255)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _cancelDiscardBtn.Click += (_, _) =>
        {
            if (_isExporting)
            {
                _exportEngine.Cancel();
                _isExporting = false;
                ShowStatusBanner("Ekspor dibatalkan.", showActions: false);
                UpdateInspectorUI();
            }
            else
            {
                Close();
            }
        };
        actionStack.Children.Add(_cancelDiscardBtn);

        DockPanel.SetDock(actionStack, Dock.Bottom);
        rightDock.Children.Add(actionStack);

        // Top Inspector Controls
        var inspector = new StackPanel();
        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _webpSavingsBadge = new TextBlock
        {
            Text = "5–10× smaller size",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(115, 235, 158)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badgeBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(42, 72, 220, 130)),
            Padding = new Thickness(7, 2, 7, 2),
            Child = _webpSavingsBadge
        };
        DockPanel.SetDock(badgeBorder, Dock.Right);
        titleRow.Children.Add(badgeBorder);
        titleRow.Children.Add(new TextBlock { Text = "Export", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        inspector.Children.Add(titleRow);

        // Format Switcher
        inspector.Children.Add(MakeSectionLabel("Format Output"));
        var fmtGrid = MakeSegmentGrid(2);
        var fmtIdx = 0;
        foreach (var fmt in Enum.GetValues<ExportFormat>())
        {
            var captured = fmt;
            var btn = MakeSegmentBtn(fmt.GetDisplayName());
            btn.Click += (_, _) => { _format = captured; UpdateInspectorUI(); };
            Grid.SetColumn(btn, fmtIdx++);
            fmtGrid.Children.Add(btn);
            _formatBtns[fmt] = btn;
        }
        inspector.Children.Add(fmtGrid);

        // FPS Switcher
        inspector.Children.Add(MakeSectionLabel("Frame Rate (FPS)"));
        int[] fpsChoices = { 10, 15, 24, 30 };
        var fpsGrid = MakeSegmentGrid(fpsChoices.Length);
        for (var i = 0; i < fpsChoices.Length; i++)
        {
            var val = fpsChoices[i];
            var btn = MakeSegmentBtn(val.ToString());
            btn.Click += (_, _) => { _fps = val; UpdateInspectorUI(); };
            Grid.SetColumn(btn, i);
            fpsGrid.Children.Add(btn);
            _fpsBtns[val] = btn;
        }
        inspector.Children.Add(fpsGrid);

        // Scale Mode
        var scaleHeader = new DockPanel { Margin = new Thickness(0, 10, 0, 5) };
        _dimensionsText.FontFamily = new FontFamily("Consolas");
        _dimensionsText.FontSize = 11;
        _dimensionsText.Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
        DockPanel.SetDock(_dimensionsText, Dock.Right);
        scaleHeader.Children.Add(_dimensionsText);
        scaleHeader.Children.Add(new TextBlock
        {
            Text = "Resolusi Output",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255))
        });
        inspector.Children.Add(scaleHeader);

        var scaleGrid = MakeSegmentGrid(3);
        var scaleIdx = 0;
        foreach (var mode in Enum.GetValues<ExportScaleMode>())
        {
            var captured = mode;
            var btn = MakeSegmentBtn(mode.GetShortLabel());
            btn.Click += (_, _) => { _scaleMode = captured; UpdateInspectorUI(); };
            Grid.SetColumn(btn, scaleIdx++);
            scaleGrid.Children.Add(btn);
            _scaleBtns[mode] = btn;
        }
        inspector.Children.Add(scaleGrid);

        _customWidthBox = new TextBox
        {
            Text = settings.DefaultCustomWidth.ToString(),
            Width = 84,
            Background = new SolidColorBrush(Color.FromRgb(40, 42, 50)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Padding = new Thickness(4, 2, 4, 2)
        };
        _customWidthBox.TextChanged += (_, _) => UpdateInspectorUI();

        _customWidthRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _customWidthRow.Children.Add(new TextBlock
        {
            Text = "Lebar (px):",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _customWidthRow.Children.Add(_customWidthBox);
        inspector.Children.Add(_customWidthRow);

        // Quality Cards
        _qualityHeader.FontSize = 11.5;
        _qualityHeader.FontWeight = FontWeights.SemiBold;
        _qualityHeader.Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        _qualityHeader.Margin = new Thickness(0, 12, 0, 6);
        inspector.Children.Add(_qualityHeader);

        foreach (var preset in Enum.GetValues<ExportQualityPreset>())
        {
            var captured = preset;
            var sub = new TextBlock
            {
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(165, 255, 255, 255))
            };
            var cardContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            cardContent.Children.Add(new TextBlock
            {
                Text = preset.GetDisplayName(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            cardContent.Children.Add(sub);

            var cardBtn = new Button
            {
                Content = cardContent,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            cardBtn.Click += (_, _) => { _quality = captured; UpdateInspectorUI(); };
            inspector.Children.Add(cardBtn);
            _qualityCards[preset] = (cardBtn, sub);
        }

        _loopCheck = new CheckBox
        {
            Content = "Loop terus menerus (Infinite)",
            Foreground = Brushes.White,
            FontSize = 12,
            IsChecked = _loopForever,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _loopCheck.Click += (_, _) => _loopForever = _loopCheck.IsChecked == true;
        inspector.Children.Add(_loopCheck);

        rightDock.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = inspector
        });

        rightHost.Child = rightDock;
        Grid.SetColumn(rightHost, 2);
        mainGrid.Children.Add(rightHost);

        Content = mainGrid;

        // Playback Loop Timer
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playbackTimer.Tick += (_, _) =>
        {
            var pos = _player.Position.TotalSeconds;
            if (!double.IsFinite(pos)) return;
            _currentTime = pos;
            _scrubber.SetPlayhead(pos);
            if (_isPlaying && pos >= _trimEnd - 0.03)
            {
                SeekTo(_trimStart);
                _player.Play();
            }
        };

        Loaded += (_, _) =>
        {
            _player.Play();
            _playbackTimer.Start();
            UpdateInspectorUI();
        };

        Closing += (_, _) =>
        {
            _playbackTimer.Stop();
            _exportEngine.Cancel();
            try
            {
                _player.Stop();
                _player.Close();
                _player.Source = null;
            }
            catch { }
        };

        UpdateInspectorUI();
    }

    private ExportOptions BuildCurrentOptions()
    {
        var parsedCustom = int.TryParse(_customWidthBox.Text.Trim(), out var cw) ? cw : _settings.DefaultCustomWidth;
        var range = new TrimRange(_trimStart, _trimEnd, _recording.Duration);
        return new ExportOptions
        {
            Format = _format,
            Fps = _fps,
            ScaleMode = _scaleMode,
            CustomWidth = parsedCustom,
            LogicalWidth = (int)Math.Round(_recording.LogicalSize.Width),
            NativePixelWidth = (int)Math.Round(_recording.PixelSize.Width),
            Quality = _quality,
            LoopForever = _loopForever,
            TrimRange = range,
            TotalDuration = _recording.Duration
        };
    }

    private void TogglePlayPause()
    {
        if (_isPlaying)
        {
            _player.Pause();
            _isPlaying = false;
            _playPauseBtn.Content = "▶";
        }
        else
        {
            if (_currentTime < _trimStart || _currentTime >= _trimEnd - 0.05)
                SeekTo(_trimStart);
            _player.Play();
            _isPlaying = true;
            _playPauseBtn.Content = "❚❚";
        }
    }

    private void SeekTo(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, _recording.Duration);
        _currentTime = clamped;
        _player.Position = TimeSpan.FromSeconds(clamped);
        _scrubber.SetPlayhead(clamped);
    }

    private async Task ExportAnimationAsync(bool copyToClipboard)
    {
        if (_isExporting) return;
        _isExporting = true;
        _errorText.Visibility = Visibility.Collapsed;
        _statusBannerCard.Visibility = Visibility.Collapsed;
        _progressPanel.Visibility = Visibility.Visible;
        _progressBar.Value = 0.02;
        _progressPctText.Text = "2%";
        UpdateInspectorUI();

        var options = BuildCurrentOptions();
        var selectedFormat = options.Format;

        try
        {
            var outputPath = _settings.MakeOutputFilePath(selectedFormat.GetFileExtension());
            var resultPath = await _exportEngine.ExportAnimationAsync(
                _recording.VideoPath,
                outputPath,
                options,
                p => Dispatcher.BeginInvoke(() =>
                {
                    _progressBar.Value = p;
                    _progressPctText.Text = $"{(int)(p * 100)}%";
                })
            );

            var fileSize = File.Exists(resultPath) ? new FileInfo(resultPath).Length : 0L;
            _exportedPath = resultPath;
            _exportedFormat = selectedFormat;
            _isExporting = false;
            _progressPanel.Visibility = Visibility.Collapsed;

            var shouldCopy = copyToClipboard || _settings.AutoCopyToClipboard;
            if (shouldCopy)
            {
                _onCopyMedia(resultPath);
                ShowStatusBanner($"{selectedFormat.GetDisplayName()} tersimpan & disalin ke clipboard ({AppCoordinator.FormatBytes(fileSize)})", showActions: true);
            }
            else
            {
                ShowStatusBanner($"{selectedFormat.GetDisplayName()} tersimpan: {Path.GetFileName(resultPath)} ({AppCoordinator.FormatBytes(fileSize)})", showActions: true);
            }

            _onExportCompleted(resultPath, fileSize, selectedFormat);
        }
        catch (Exception ex)
        {
            _isExporting = false;
            _progressPanel.Visibility = Visibility.Collapsed;
            _errorText.Text = ex.Message;
            _errorText.Visibility = Visibility.Visible;
        }
        finally
        {
            UpdateInspectorUI();
        }
    }

    private async Task ExportMp4Async()
    {
        if (_isExporting) return;
        _isExporting = true;
        _errorText.Visibility = Visibility.Collapsed;
        _statusBannerCard.Visibility = Visibility.Collapsed;
        _progressPanel.Visibility = Visibility.Visible;
        _progressBar.Value = 0.05;
        _progressPctText.Text = "5%";
        UpdateInspectorUI();

        var range = new TrimRange(_trimStart, _trimEnd, _recording.Duration);
        try
        {
            var outputPath = _settings.MakeOutputFilePath("mp4");
            var resultPath = await _exportEngine.ExportMp4Async(
                _recording.VideoPath,
                outputPath,
                range,
                _recording.Duration,
                p => Dispatcher.BeginInvoke(() =>
                {
                    _progressBar.Value = p;
                    _progressPctText.Text = $"{(int)(p * 100)}%";
                })
            );

            var fileSize = File.Exists(resultPath) ? new FileInfo(resultPath).Length : 0L;
            _isExporting = false;
            _progressPanel.Visibility = Visibility.Collapsed;
            ShowStatusBanner($"MP4 tersimpan: {Path.GetFileName(resultPath)} ({AppCoordinator.FormatBytes(fileSize)})", showActions: false);
            AppCoordinator.RevealInExplorer(resultPath);
        }
        catch (Exception ex)
        {
            _isExporting = false;
            _progressPanel.Visibility = Visibility.Collapsed;
            _errorText.Text = ex.Message;
            _errorText.Visibility = Visibility.Visible;
        }
        finally
        {
            UpdateInspectorUI();
        }
    }

    private void ShowStatusBanner(string text, bool showActions)
    {
        _statusBannerText.Text = text;
        _statusBannerActions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        _statusBannerCard.Visibility = Visibility.Visible;
    }

    private void UpdateInspectorUI()
    {
        var opts = BuildCurrentOptions();
        var w = opts.ResolvedTargetWidth;
        var aspect = _recording.LogicalSize.Height / Math.Max(1.0, _recording.LogicalSize.Width);
        var h = CaptureGeometry.EvenPixelDimension(w * aspect);
        var dimText = $"{w} × {h} px";

        _dimensionsText.Text = dimText;
        _badgeText.Text = $"{dimText} • {RecordingTimeline.FormatDuration(_trimEnd - _trimStart, includeTenths: true)}";
        _trimTimeText.Text = $"{RecordingTimeline.FormatDuration(_trimStart, includeTenths: true)}  →  {RecordingTimeline.FormatDuration(_trimEnd, includeTenths: true)}";

        _webpSavingsBadge.Visibility = _format == ExportFormat.WebP ? Visibility.Visible : Visibility.Collapsed;
        _customWidthRow.Visibility = _scaleMode == ExportScaleMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        _qualityHeader.Text = _format == ExportFormat.WebP ? "Kualitas WebP (Animated)" : "Kualitas Palet (2-Pass GIF)";

        foreach (var (fmt, btn) in _formatBtns)
            StyleSegment(btn, fmt == _format);
        foreach (var (fps, btn) in _fpsBtns)
            StyleSegment(btn, fps == _fps);
        foreach (var (mode, btn) in _scaleBtns)
            StyleSegment(btn, mode == _scaleMode);

        foreach (var (preset, (btn, sub)) in _qualityCards)
        {
            sub.Text = preset.GetSubtitle(_format);
            var selected = preset == _quality;
            btn.Background = new SolidColorBrush(selected ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(12, 255, 255, 255));
            btn.BorderBrush = new SolidColorBrush(selected ? Color.FromArgb(190, 245, 76, 66) : Color.FromArgb(18, 255, 255, 255));
        }

        var fmtLabel = _format.GetDisplayName();
        _exportPrimaryBtn.Content = _exportedPath is null ? $"✦  Export & Save {fmtLabel}" : $"✦  Re-Export {fmtLabel}";
        _copyBtn.Content = $"Copy {fmtLabel}";
        _cancelDiscardBtn.Content = _isExporting ? "Cancel Export" : "Discard & Close";

        _exportPrimaryBtn.IsEnabled = !_isExporting;
        _copyBtn.IsEnabled = !_isExporting;
        _saveMp4Btn.IsEnabled = !_isExporting;
    }

    private static TextBlock MakeSectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
        Margin = new Thickness(0, 10, 0, 5)
    };

    private static Grid MakeSegmentGrid(int cols)
    {
        var g = new Grid();
        for (var i = 0; i < cols; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private static Button MakeSegmentBtn(string text) => new()
    {
        Content = text,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(4, 5, 4, 5),
        Margin = new Thickness(2, 0, 2, 0),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };

    private static void StyleSegment(Button btn, bool active)
    {
        btn.Background = new SolidColorBrush(active ? Color.FromRgb(245, 69, 61) : Color.FromArgb(24, 255, 255, 255));
        btn.Foreground = new SolidColorBrush(active ? Colors.White : Color.FromArgb(200, 255, 255, 255));
    }

    private static Button MakeSecondaryBtn(string text) => new()
    {
        Content = text,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
        FontSize = 12,
        FontWeight = FontWeights.Medium,
        Padding = new Thickness(6, 7, 6, 7),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };

    private static Button MakeSmallBtn(string text) => new()
    {
        Content = text,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        FontSize = 11,
        Padding = new Thickness(8, 3, 8, 3),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };
}

internal sealed class TrimTimelineScrubber : FrameworkElement
{
    private const double ThumbWidth = 12.0;
    private const double MinGap = 0.2;

    private enum DragTarget { None, Start, End }
    private DragTarget _dragTarget = DragTarget.None;

    private readonly double _totalDuration;
    private double _trimStart;
    private double _trimEnd;
    private double _playhead;

    public event Action<double, double, double>? TrimChanged;
    public event Action<double>? SeekRequested;

    public TrimTimelineScrubber(double totalDuration)
    {
        _totalDuration = Math.Max(MinGap, totalDuration);
        _trimStart = 0;
        _trimEnd = _totalDuration;
        Cursor = Cursors.Hand;
    }

    public void SetPlayhead(double seconds)
    {
        _playhead = Math.Clamp(seconds, 0, _totalDuration);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = Math.Max(1, ActualWidth);
        var h = Math.Max(1, ActualHeight);
        var trackW = Math.Max(1, w - ThumbWidth * 2);

        var startX = (_trimStart / _totalDuration) * trackW;
        var endX = (_trimEnd / _totalDuration) * trackW;
        var playX = (_playhead / _totalDuration) * trackW + ThumbWidth;

        // Track base
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            null,
            new Rect(ThumbWidth / 2.0, (h - 22) / 2.0, w - ThumbWidth, 22),
            6, 6
        );

        // Active trim range highlight
        var activeRect = new Rect(startX, (h - 28) / 2.0, Math.Max(ThumbWidth * 2, (endX - startX) + ThumbWidth * 2), 28);
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(66, 245, 69, 61)),
            new Pen(new SolidColorBrush(Color.FromRgb(245, 69, 61)), 2),
            activeRect,
            4, 4
        );

        // Playhead
        dc.DrawRectangle(Brushes.White, null, new Rect(playX - 1, 1, 2, h - 2));

        // Start & End thumbs
        var accent = new SolidColorBrush(Color.FromRgb(245, 69, 61));
        var startThumb = new Rect(startX, (h - 28) / 2.0, ThumbWidth, 28);
        var endThumb = new Rect(endX + ThumbWidth, (h - 28) / 2.0, ThumbWidth, 28);
        dc.DrawRoundedRectangle(accent, null, startThumb, 4, 4);
        dc.DrawRoundedRectangle(accent, null, endThumb, 4, 4);

        var gripBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        dc.DrawRectangle(gripBrush, null, new Rect(startThumb.X + ThumbWidth / 2.0 - 1, (h - 12) / 2.0, 2, 12));
        dc.DrawRectangle(gripBrush, null, new Rect(endThumb.X + ThumbWidth / 2.0 - 1, (h - 12) / 2.0, 2, 12));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var pt = e.GetPosition(this);
        var trackW = Math.Max(1, ActualWidth - ThumbWidth * 2);
        var startX = (_trimStart / _totalDuration) * trackW;
        var endX = (_trimEnd / _totalDuration) * trackW + ThumbWidth;

        if (Math.Abs(pt.X - (startX + ThumbWidth / 2.0)) <= 14)
        {
            _dragTarget = DragTarget.Start;
            CaptureMouse();
        }
        else if (Math.Abs(pt.X - (endX + ThumbWidth / 2.0)) <= 14)
        {
            _dragTarget = DragTarget.End;
            CaptureMouse();
        }
        else
        {
            var fraction = Math.Clamp((pt.X - ThumbWidth) / trackW, 0, 1);
            var t = Math.Clamp(fraction * _totalDuration, _trimStart, _trimEnd);
            SeekRequested?.Invoke(t);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragTarget == DragTarget.None) return;

        var pt = e.GetPosition(this);
        var trackW = Math.Max(1, ActualWidth - ThumbWidth * 2);

        if (_dragTarget == DragTarget.Start)
        {
            var fraction = Math.Clamp(pt.X / trackW, 0, 1);
            _trimStart = Math.Min(fraction * _totalDuration, Math.Max(0, _trimEnd - MinGap));
            InvalidateVisual();
            TrimChanged?.Invoke(_trimStart, _trimEnd, _trimStart);
        }
        else if (_dragTarget == DragTarget.End)
        {
            var fraction = Math.Clamp((pt.X - ThumbWidth) / trackW, 0, 1);
            _trimEnd = Math.Max(fraction * _totalDuration, Math.Min(_totalDuration, _trimStart + MinGap));
            InvalidateVisual();
            TrimChanged?.Invoke(_trimStart, _trimEnd, _trimEnd);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragTarget = DragTarget.None;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }
}
