using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rekord.SystemServices;
using RekordCore;
using WinForms = System.Windows.Forms;

namespace Rekord.UI;

/// <summary>
/// Settings window covering export defaults, storage path, recording controls,
/// Start with Windows, and global hotkey display.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TextBlock _outputPathLabel;
    private readonly DockPanel _customWidthRow;
    private readonly ComboBox _qualityCombo;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Rekord Settings";
        Width = 520;
        Height = 610;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 32));
        Foreground = Brushes.White;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Margin = new Thickness(18) };

        // === Section 1: Alur Export & Penyimpanan ===
        var exportSec = MakeSectionCard("Alur Export & Penyimpanan", out var exportBody);

        var quickCheck = MakeCheckBox("Quick Export (otomatis simpan begitu Stop tanpa Preview)", _settings.QuickExportEnabled);
        quickCheck.Click += (_, _) => _settings.QuickExportEnabled = quickCheck.IsChecked == true;
        exportBody.Children.Add(quickCheck);

        var autoCopyCheck = MakeCheckBox("Auto-Copy hasil ke Clipboard setelah selesai", _settings.AutoCopyToClipboard);
        autoCopyCheck.Click += (_, _) => _settings.AutoCopyToClipboard = autoCopyCheck.IsChecked == true;
        exportBody.Children.Add(autoCopyCheck);

        _outputPathLabel = new TextBlock
        {
            Text = _settings.OutputDirectoryPath,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(165, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var folderRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var folderBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var pickBtn = MakeButton("Pilih…");
        pickBtn.Margin = new Thickness(8, 0, 6, 0);
        pickBtn.Click += (_, _) => PickOutputFolder();

        var openBtn = MakeButton("Buka");
        openBtn.ToolTip = "Buka folder di File Explorer";
        openBtn.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(_settings.OutputDirectoryPath);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputDirectoryPath}\"") { UseShellExecute = true });
            }
            catch { }
        };
        folderBtns.Children.Add(pickBtn);
        folderBtns.Children.Add(openBtn);
        DockPanel.SetDock(folderBtns, Dock.Right);
        folderRow.Children.Add(folderBtns);

        var folderInfo = new StackPanel();
        folderInfo.Children.Add(new TextBlock { Text = "Folder Penyimpanan", FontSize = 12, Foreground = Brushes.White });
        folderInfo.Children.Add(_outputPathLabel);
        folderRow.Children.Add(folderInfo);
        exportBody.Children.Add(folderRow);
        root.Children.Add(exportSec);

        // === Section 2: Pengaturan Default Export ===
        var defaultsSec = MakeSectionCard("Pengaturan Default Export", out var defaultsBody);

        var formatCombo = MakeComboBox(new[] { "WebP (5–10× smaller size)", "GIF (Universal)" }, _settings.DefaultFormat == ExportFormat.WebP ? 0 : 1);
        _qualityCombo = MakeComboBox(Array.Empty<string>(), 0);

        formatCombo.SelectionChanged += (_, _) =>
        {
            _settings.DefaultFormat = formatCombo.SelectedIndex == 0 ? ExportFormat.WebP : ExportFormat.Gif;
            PopulateQualityCombo();
        };
        defaultsBody.Children.Add(MakeLabeledRow("Default Format", formatCombo));

        int[] fpsValues = { 10, 15, 24, 30 };
        var fpsIdx = Array.IndexOf(fpsValues, _settings.DefaultFps);
        var fpsCombo = MakeComboBox(new[] { "10 FPS", "15 FPS (Recommended)", "24 FPS", "30 FPS" }, fpsIdx >= 0 ? fpsIdx : 1);
        fpsCombo.SelectionChanged += (_, _) =>
        {
            if (fpsCombo.SelectedIndex >= 0 && fpsCombo.SelectedIndex < fpsValues.Length)
                _settings.DefaultFps = fpsValues[fpsCombo.SelectedIndex];
        };
        defaultsBody.Children.Add(MakeLabeledRow("Default FPS", fpsCombo));

        var scaleModes = Enum.GetValues<ExportScaleMode>();
        var scaleLabels = new string[scaleModes.Length];
        for (var i = 0; i < scaleModes.Length; i++)
            scaleLabels[i] = scaleModes[i].GetDisplayLabel();
        var scaleCombo = MakeComboBox(scaleLabels, Array.IndexOf(scaleModes, _settings.DefaultScaleMode));

        var customBox = new TextBox
        {
            Text = _settings.DefaultCustomWidth.ToString(),
            Width = 100,
            Background = new SolidColorBrush(Color.FromRgb(40, 42, 50)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Padding = new Thickness(6, 3, 6, 3)
        };
        customBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(customBox.Text.Trim(), out var w) && w >= 120)
                _settings.DefaultCustomWidth = w;
        };
        _customWidthRow = MakeLabeledRow("Lebar Custom (px)", customBox);
        _customWidthRow.Visibility = _settings.DefaultScaleMode == ExportScaleMode.Custom ? Visibility.Visible : Visibility.Collapsed;

        scaleCombo.SelectionChanged += (_, _) =>
        {
            if (scaleCombo.SelectedIndex >= 0 && scaleCombo.SelectedIndex < scaleModes.Length)
            {
                _settings.DefaultScaleMode = scaleModes[scaleCombo.SelectedIndex];
                _customWidthRow.Visibility = _settings.DefaultScaleMode == ExportScaleMode.Custom ? Visibility.Visible : Visibility.Collapsed;
            }
        };
        defaultsBody.Children.Add(MakeLabeledRow("Skala Resolusi", scaleCombo));
        defaultsBody.Children.Add(_customWidthRow);

        PopulateQualityCombo();
        _qualityCombo.SelectionChanged += (_, _) =>
        {
            var presets = Enum.GetValues<ExportQualityPreset>();
            if (_qualityCombo.SelectedIndex >= 0 && _qualityCombo.SelectedIndex < presets.Length)
                _settings.DefaultQuality = presets[_qualityCombo.SelectedIndex];
        };
        defaultsBody.Children.Add(MakeLabeledRow("Kualitas", _qualityCombo));

        var loopCheck = MakeCheckBox("Loop terus menerus (Infinite)", _settings.DefaultLoopForever);
        loopCheck.Click += (_, _) => _settings.DefaultLoopForever = loopCheck.IsChecked == true;
        defaultsBody.Children.Add(loopCheck);
        root.Children.Add(defaultsSec);

        // === Section 3: Perekaman, Sistem & Kontrol ===
        var recSec = MakeSectionCard("Perekaman, Sistem & Kontrol", out var recBody);

        var countdownCombo = MakeComboBox(new[] { "Off", "3 detik" }, _settings.CountdownSeconds > 0 ? 1 : 0);
        countdownCombo.SelectionChanged += (_, _) => _settings.CountdownSeconds = countdownCombo.SelectedIndex == 1 ? 3 : 0;
        recBody.Children.Add(MakeLabeledRow("Countdown Sebelum Rekam", countdownCombo));

        var cursorCheck = MakeCheckBox("Tampilkan Kursor Mouse", _settings.ShowCursor);
        cursorCheck.Click += (_, _) => _settings.ShowCursor = cursorCheck.IsChecked == true;
        recBody.Children.Add(cursorCheck);

        var clickCheck = MakeCheckBox("Highlight Efek Klik Mouse", _settings.HighlightClicks);
        clickCheck.Click += (_, _) => _settings.HighlightClicks = clickCheck.IsChecked == true;
        recBody.Children.Add(clickCheck);

        var panCheck = MakeCheckBox("Izinkan Geser Posisi Frame Saat Merekam (Live Pan)", _settings.AllowPanDuringRecording);
        panCheck.Click += (_, _) => _settings.AllowPanDuringRecording = panCheck.IsChecked == true;
        recBody.Children.Add(panCheck);

        var startupCheck = MakeCheckBox("Jalankan otomatis saat Windows menyala (Start with Windows)", _settings.StartWithWindows);
        startupCheck.Click += (_, _) => _settings.StartWithWindows = startupCheck.IsChecked == true;
        recBody.Children.Add(startupCheck);

        var hotkeyBadge = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = GlobalHotkeyManager.ShortcutDisplayLabel,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brushes.White
            }
        };
        recBody.Children.Add(MakeLabeledRow("Global Hotkey", hotkeyBadge));
        root.Children.Add(recSec);

        scroll.Content = root;
        Content = scroll;
    }

    private void PopulateQualityCombo()
    {
        var presets = Enum.GetValues<ExportQualityPreset>();
        var currentIndex = Array.IndexOf(presets, _settings.DefaultQuality);
        _qualityCombo.Items.Clear();
        foreach (var preset in presets)
            _qualityCombo.Items.Add($"{preset.GetDisplayName()} — {preset.GetSubtitle(_settings.DefaultFormat)}");
        _qualityCombo.SelectedIndex = currentIndex >= 0 ? currentIndex : 1;
    }

    private void PickOutputFolder()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Pilih Folder Penyimpanan Rekord",
            SelectedPath = _settings.OutputDirectoryPath,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            _settings.OutputDirectoryPath = dialog.SelectedPath;
            _outputPathLabel.Text = dialog.SelectedPath;
        }
    }

    private static Border MakeSectionCard(string title, out StackPanel body)
    {
        body = new StackPanel();
        var outer = new StackPanel();
        outer.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        outer.Children.Add(body);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 14),
            Child = outer
        };
    }

    private static CheckBox MakeCheckBox(string label, bool isChecked) => new()
    {
        Content = label,
        IsChecked = isChecked,
        Foreground = Brushes.White,
        FontSize = 12,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static DockPanel MakeLabeledRow(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private static ComboBox MakeComboBox(string[] items, int selectedIndex)
    {
        var cb = new ComboBox { Width = 230, FontSize = 11.5 };
        foreach (var item in items)
            cb.Items.Add(item);
        if (selectedIndex >= 0 && selectedIndex < items.Length)
            cb.SelectedIndex = selectedIndex;
        return cb;
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        FontSize = 11.5,
        Padding = new Thickness(10, 4, 10, 4),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand
    };
}
