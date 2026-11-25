using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RekordCore;

/// <summary>
/// Deep module persisting Rekord preferences as JSON in %APPDATA%\Rekord\settings.json
/// and generating deterministic, collision-free timestamped output file paths.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsFilePath;
    private bool _isLoading;

    private bool _quickExportEnabled;
    private string _outputDirectoryPath = DefaultOutputDirectoryPath;
    private bool _autoCopyToClipboard = true;
    private ExportFormat _defaultFormat = ExportFormat.WebP;
    private int _defaultFps = 15;
    private ExportScaleMode _defaultScaleMode = ExportScaleMode.Logical1x;
    private int _defaultCustomWidth = 800;
    private ExportQualityPreset _defaultQuality = ExportQualityPreset.Balanced;
    private bool _defaultLoopForever = true;
    private int _countdownSeconds;
    private bool _showCursor = true;
    private bool _highlightClicks = true;
    private bool _allowPanDuringRecording = true;
    private bool _startWithWindows;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string DefaultSettingsFilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(appData, "Rekord", "settings.json");
        }
    }

    public static string DefaultOutputDirectoryPath
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            return Path.Combine(videos, "Rekord");
        }
    }

    public AppSettings(string? settingsFilePath = null)
    {
        _settingsFilePath = string.IsNullOrWhiteSpace(settingsFilePath)
            ? DefaultSettingsFilePath
            : settingsFilePath;
        Load();
    }

    public bool QuickExportEnabled
    {
        get => _quickExportEnabled;
        set => SetField(ref _quickExportEnabled, value);
    }

    public string OutputDirectoryPath
    {
        get => _outputDirectoryPath;
        set => SetField(ref _outputDirectoryPath, string.IsNullOrWhiteSpace(value) ? DefaultOutputDirectoryPath : value);
    }

    public bool AutoCopyToClipboard
    {
        get => _autoCopyToClipboard;
        set => SetField(ref _autoCopyToClipboard, value);
    }

    public ExportFormat DefaultFormat
    {
        get => _defaultFormat;
        set => SetField(ref _defaultFormat, value);
    }

    public int DefaultFps
    {
        get => _defaultFps;
        set => SetField(ref _defaultFps, Math.Clamp(value, 1, 60));
    }

    public ExportScaleMode DefaultScaleMode
    {
        get => _defaultScaleMode;
        set => SetField(ref _defaultScaleMode, value);
    }

    public int DefaultCustomWidth
    {
        get => _defaultCustomWidth;
        set => SetField(ref _defaultCustomWidth, Math.Clamp(value, 120, 4096));
    }

    public ExportQualityPreset DefaultQuality
    {
        get => _defaultQuality;
        set => SetField(ref _defaultQuality, value);
    }

    public bool DefaultLoopForever
    {
        get => _defaultLoopForever;
        set => SetField(ref _defaultLoopForever, value);
    }

    /// <summary>0 = Off, 3 = 3-second countdown</summary>
    public int CountdownSeconds
    {
        get => _countdownSeconds;
        set => SetField(ref _countdownSeconds, Math.Max(0, value));
    }

    public bool ShowCursor
    {
        get => _showCursor;
        set => SetField(ref _showCursor, value);
    }

    public bool HighlightClicks
    {
        get => _highlightClicks;
        set => SetField(ref _highlightClicks, value);
    }

    public bool AllowPanDuringRecording
    {
        get => _allowPanDuringRecording;
        set => SetField(ref _allowPanDuringRecording, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetField(ref _startWithWindows, value);
    }

    /// <summary>
    /// Ensures the output directory exists and returns a unique timestamped file path.
    /// </summary>
    public string MakeOutputFilePath(string fileExtension, DateTime? timestamp = null)
    {
        var ext = fileExtension.TrimStart('.');
        var dir = OutputDirectoryPath;
        Directory.CreateDirectory(dir);

        var dt = timestamp ?? DateTime.Now;
        var baseName = $"Rekord_{dt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}";
        var candidate = Path.Combine(dir, $"{baseName}.{ext}");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{baseName}_{suffix}.{ext}");
            suffix++;
        }
        return candidate;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (!_isLoading)
            Save();
    }

    private void Load()
    {
        _isLoading = true;
        try
        {
            if (!File.Exists(_settingsFilePath))
                return;

            var json = File.ReadAllText(_settingsFilePath);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions);
            if (dto is null) return;

            _quickExportEnabled = dto.QuickExportEnabled;
            _outputDirectoryPath = string.IsNullOrWhiteSpace(dto.OutputDirectoryPath)
                ? DefaultOutputDirectoryPath
                : dto.OutputDirectoryPath;
            _autoCopyToClipboard = dto.AutoCopyToClipboard;
            _defaultFormat = dto.DefaultFormat;
            _defaultFps = Math.Clamp(dto.DefaultFps, 1, 60);
            _defaultScaleMode = dto.DefaultScaleMode;
            _defaultCustomWidth = Math.Clamp(dto.DefaultCustomWidth, 120, 4096);
            _defaultQuality = dto.DefaultQuality;
            _defaultLoopForever = dto.DefaultLoopForever;
            _countdownSeconds = Math.Max(0, dto.CountdownSeconds);
            _showCursor = dto.ShowCursor;
            _highlightClicks = dto.HighlightClicks;
            _allowPanDuringRecording = dto.AllowPanDuringRecording;
            _startWithWindows = dto.StartWithWindows;
        }
        catch
        {
            // Keep defaults if settings file is corrupted or unreadable.
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var dto = new SettingsDto
            {
                QuickExportEnabled = _quickExportEnabled,
                OutputDirectoryPath = _outputDirectoryPath,
                AutoCopyToClipboard = _autoCopyToClipboard,
                DefaultFormat = _defaultFormat,
                DefaultFps = _defaultFps,
                DefaultScaleMode = _defaultScaleMode,
                DefaultCustomWidth = _defaultCustomWidth,
                DefaultQuality = _defaultQuality,
                DefaultLoopForever = _defaultLoopForever,
                CountdownSeconds = _countdownSeconds,
                ShowCursor = _showCursor,
                HighlightClicks = _highlightClicks,
                AllowPanDuringRecording = _allowPanDuringRecording,
                StartWithWindows = _startWithWindows
            };

            var json = JsonSerializer.Serialize(dto, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    private sealed class SettingsDto
    {
        public bool QuickExportEnabled { get; set; }
        public string OutputDirectoryPath { get; set; } = DefaultOutputDirectoryPath;
        public bool AutoCopyToClipboard { get; set; } = true;
        public ExportFormat DefaultFormat { get; set; } = ExportFormat.WebP;
        public int DefaultFps { get; set; } = 15;
        public ExportScaleMode DefaultScaleMode { get; set; } = ExportScaleMode.Logical1x;
        public int DefaultCustomWidth { get; set; } = 800;
        public ExportQualityPreset DefaultQuality { get; set; } = ExportQualityPreset.Balanced;
        public bool DefaultLoopForever { get; set; } = true;
        public int CountdownSeconds { get; set; }
        public bool ShowCursor { get; set; } = true;
        public bool HighlightClicks { get; set; } = true;
        public bool AllowPanDuringRecording { get; set; } = true;
        public bool StartWithWindows { get; set; }
    }
}
