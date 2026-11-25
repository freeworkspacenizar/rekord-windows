using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Rekord.Capture;
using Rekord.Overlay;
using Rekord.SystemServices;
using Rekord.UI;
using RekordCore;

namespace Rekord.App;

public enum AppSessionStateKind
{
    Idle,
    Selecting,
    Countdown,
    Recording,
    Paused,
    Stopping,
    Previewing,
    QuickExporting
}

public sealed record LastExportInfo(
    string FilePath,
    long FileSize,
    ExportFormat Format,
    DateTime Timestamp
);

/// <summary>
/// Central application coordinator managing the full Windows lifecycle:
/// System Tray / Global Hotkey (Ctrl+Shift+6) -> Dynamic Frame Selection -> Countdown ->
/// DXGI/GDI Capture (+Click Ripple) -> Preview/Quick Export -> Bundled FFmpeg WebP/GIF/MP4.
/// </summary>
public sealed class AppCoordinator : IDisposable
{
    public AppSettings Settings { get; } = new();
    public ExportEngine ExportEngine { get; } = new();

    public AppSessionStateKind SessionState { get; private set; } = AppSessionStateKind.Idle;
    public int CountdownRemaining { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public double QuickExportProgress { get; private set; }
    public LastExportInfo? LastExport { get; private set; }
    public string? LastError { get; private set; }

    public event Action? StateChanged;

    private readonly SelectionOverlayController _overlayController;
    private readonly CountdownHudWindow _countdownHud = new();
    private readonly ClickHighlightOverlayWindow _clickHighlightOverlay = new();
    private readonly ScreenCaptureEngine _captureEngine;
    private TrayController? _trayController;
    private GlobalHotkeyManager? _hotkeyManager;
    private PreviewExportWindow? _previewWindow;
    private SettingsWindow? _settingsWindow;

    public AppCoordinator()
    {
        _overlayController = new SelectionOverlayController(Settings);
        _captureEngine = new ScreenCaptureEngine(ExportEngine);
        SetupCallbacks();
    }

    public void Start()
    {
        StartupManager.SetEnabled(Settings.StartWithWindows);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.StartWithWindows))
                StartupManager.SetEnabled(Settings.StartWithWindows);
        };

        _trayController = new TrayController(this);
        _hotkeyManager = new GlobalHotkeyManager(HandleGlobalHotkey);
        if (!_hotkeyManager.Register())
        {
            LastError = $"Global hotkey {GlobalHotkeyManager.ShortcutDisplayLabel} sedang digunakan aplikasi lain.";
            NotifyStateChanged();
        }
    }

    public void HandleGlobalHotkey()
    {
        switch (SessionState)
        {
            case AppSessionStateKind.Idle:
            case AppSessionStateKind.Previewing:
                BeginSelection();
                break;
            case AppSessionStateKind.Selecting:
                StartRecordingFromSelection();
                break;
            case AppSessionStateKind.Countdown:
                CancelSession();
                break;
            case AppSessionStateKind.Recording:
            case AppSessionStateKind.Paused:
                StopRecording();
                break;
            case AppSessionStateKind.Stopping:
            case AppSessionStateKind.QuickExporting:
                break;
        }
    }

    public void BeginSelection()
    {
        LastError = null;
        _trayController?.ClosePopup();

        if (!_overlayController.ShowForSelection())
        {
            LastError = "Tidak ada layar aktif yang terdeteksi untuk perekaman.";
            SessionState = AppSessionStateKind.Idle;
            NotifyStateChanged();
            _trayController?.OpenPopup();
            return;
        }

        SessionState = AppSessionStateKind.Selecting;
        ElapsedSeconds = 0;
        NotifyStateChanged();
    }

    public void StartRecordingFromSelection()
    {
        if (SessionState != AppSessionStateKind.Selecting) return;
        LastError = null;
        _trayController?.ClosePopup();

        var countdownSec = Settings.CountdownSeconds;
        if (countdownSec > 0)
        {
            SessionState = AppSessionStateKind.Countdown;
            CountdownRemaining = countdownSec;
            _overlayController.TransitionTo(OverlayMode.Countdown);
            NotifyStateChanged();

            _ = _countdownHud.RunAsync(countdownSec, _overlayController.SelectionGlobalLogicalRect, LaunchActualCapture);
        }
        else
        {
            LaunchActualCapture();
        }
    }

    public void TogglePauseResume()
    {
        if (SessionState == AppSessionStateKind.Recording)
        {
            _captureEngine.Pause();
            SessionState = AppSessionStateKind.Paused;
            _overlayController.TransitionTo(OverlayMode.Paused);
            NotifyStateChanged();
        }
        else if (SessionState == AppSessionStateKind.Paused)
        {
            _captureEngine.Resume();
            SessionState = AppSessionStateKind.Recording;
            _overlayController.TransitionTo(OverlayMode.Recording);
            NotifyStateChanged();
        }
    }

    public void StopRecording()
    {
        if (SessionState is not (AppSessionStateKind.Recording or AppSessionStateKind.Paused))
            return;

        SessionState = AppSessionStateKind.Stopping;
        NotifyStateChanged();

        _trayController?.ClosePopup();
        _clickHighlightOverlay.Stop();
        _overlayController.Close();

        _ = FinishStopRecordingAsync();
    }

    private async Task FinishStopRecordingAsync()
    {
        try
        {
            var result = await _captureEngine.StopAsync();
            if (Settings.QuickExportEnabled)
            {
                await PerformQuickExportAsync(result);
            }
            else
            {
                SessionState = AppSessionStateKind.Previewing;
                NotifyStateChanged();
                ShowPreviewWindow(result);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SessionState = AppSessionStateKind.Idle;
            NotifyStateChanged();
            _trayController?.OpenPopup();
        }
    }

    public void CancelSession()
    {
        _countdownHud.Cancel();
        _captureEngine.Abort();
        _clickHighlightOverlay.Stop();
        _overlayController.Close();
        SessionState = AppSessionStateKind.Idle;
        ElapsedSeconds = 0;
        CountdownRemaining = 0;
        NotifyStateChanged();
    }

    public void OpenSettings()
    {
        _trayController?.ClosePopup();
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void CopyMediaToClipboard(string filePath)
    {
        ClipboardHelper.CopyFileToClipboard(filePath);
    }

    public static void RevealInExplorer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public void Shutdown()
    {
        CancelSession();
        _previewWindow?.Close();
        _settingsWindow?.Close();
        Dispose();
        Application.Current?.Shutdown();
    }

    private void SetupCallbacks()
    {
        _overlayController.StartRecordingRequested += StartRecordingFromSelection;
        _overlayController.StopRecordingRequested += StopRecording;
        _overlayController.TogglePauseRequested += TogglePauseResume;
        _overlayController.CancelRequested += CancelSession;
        _overlayController.PanDuringRecording += newRect => _captureEngine.UpdateRegion(newRect);

        _captureEngine.DurationUpdated += secs =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (SessionState != AppSessionStateKind.Recording) return;
                ElapsedSeconds = secs;
                _overlayController.UpdateElapsed(secs);
                NotifyStateChanged();
            });
        };

        _captureEngine.StreamFailed += ex =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                LastError = ex.Message;
                CancelSession();
                _trayController?.OpenPopup();
            });
        };
    }

    private async void LaunchActualCapture()
    {
        var monitor = _overlayController.TargetMonitor;
        if (monitor is null)
        {
            LastError = "Display target tidak ditemukan untuk perekaman.";
            CancelSession();
            _trayController?.OpenPopup();
            return;
        }

        var rect = _overlayController.SelectionRect;
        var showCursor = Settings.ShowCursor;

        if (Settings.HighlightClicks)
            _clickHighlightOverlay.Start(monitor);

        _overlayController.TransitionTo(OverlayMode.Recording);
        SessionState = AppSessionStateKind.Recording;
        ElapsedSeconds = 0;
        NotifyStateChanged();

        // Give DWM a brief frame to flush the hidden selection scrim before grabbing Frame #1
        await Task.Delay(40);
        if (SessionState != AppSessionStateKind.Recording) return;

        try
        {
            await _captureEngine.StartAsync(
                monitor: monitor,
                logicalLocalRect: rect,
                captureFps: Math.Max(30, Settings.DefaultFps),
                showCursor: showCursor
            );
        }
        catch (Exception ex)
        {
            LastError = $"Gagal memulai perekaman: {ex.Message}";
            CancelSession();
            _trayController?.OpenPopup();
        }
    }

    private void ShowPreviewWindow(RecordingResult recording)
    {
        if (_previewWindow is not null)
        {
            try { _previewWindow.Close(); } catch { }
        }

        var win = new PreviewExportWindow(
            recording: recording,
            settings: Settings,
            exportEngine: ExportEngine,
            onExportCompleted: (path, size, format) => RecordExportSuccess(path, size, format),
            onCopyMedia: CopyMediaToClipboard
        );

        win.Closed += (_, _) =>
        {
            try
            {
                if (File.Exists(recording.VideoPath))
                    File.Delete(recording.VideoPath);
            }
            catch { }

            _previewWindow = null;
            if (SessionState == AppSessionStateKind.Previewing)
            {
                SessionState = AppSessionStateKind.Idle;
                NotifyStateChanged();
            }
        };

        _previewWindow = win;
        win.Show();
        win.Activate();
    }

    private async Task PerformQuickExportAsync(RecordingResult recording)
    {
        try
        {
            SessionState = AppSessionStateKind.QuickExporting;
            QuickExportProgress = 0.05;
            NotifyStateChanged();

            var format = Settings.DefaultFormat;
            var options = new ExportOptions
            {
                Format = format,
                Fps = Settings.DefaultFps,
                ScaleMode = Settings.DefaultScaleMode,
                CustomWidth = Settings.DefaultCustomWidth,
                LogicalWidth = (int)Math.Round(recording.LogicalSize.Width),
                NativePixelWidth = (int)Math.Round(recording.PixelSize.Width),
                Quality = Settings.DefaultQuality,
                LoopForever = Settings.DefaultLoopForever,
                TrimRange = null,
                TotalDuration = recording.Duration
            };

            var outputPath = Settings.MakeOutputFilePath(format.GetFileExtension());
            var exportedPath = await ExportEngine.ExportAnimationAsync(
                recording.VideoPath,
                outputPath,
                options,
                progress =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        QuickExportProgress = progress;
                        NotifyStateChanged();
                    });
                }
            );

            var fileSize = File.Exists(exportedPath) ? new FileInfo(exportedPath).Length : 0L;
            if (Settings.AutoCopyToClipboard)
                CopyMediaToClipboard(exportedPath);

            RecordExportSuccess(exportedPath, fileSize, format);

            var fileName = Path.GetFileName(exportedPath);
            var folderName = Path.GetFileName(Path.GetDirectoryName(exportedPath) ?? "");
            var body = Settings.AutoCopyToClipboard
                ? $"{fileName} tersimpan & disalin ke clipboard."
                : $"{fileName} tersimpan di {folderName}.";
            _trayController?.ShowNotification(
                $"Rekord — {format.GetDisplayName()} Siap ({FormatBytes(fileSize)})",
                body
            );

            SessionState = AppSessionStateKind.Idle;
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            LastError = $"Quick Export gagal: {ex.Message}";
            SessionState = AppSessionStateKind.Idle;
            NotifyStateChanged();
            _trayController?.OpenPopup();
        }
        finally
        {
            try
            {
                if (File.Exists(recording.VideoPath))
                    File.Delete(recording.VideoPath);
            }
            catch { }
        }
    }

    private void RecordExportSuccess(string filePath, long fileSize, ExportFormat format)
    {
        LastExport = new LastExportInfo(filePath, fileSize, format, DateTime.Now);
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        _trayController?.SyncStatus();
        StateChanged?.Invoke();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double val = Math.Max(0, bytes);
        var idx = 0;
        while (val >= 1024 && idx < units.Length - 1)
        {
            val /= 1024.0;
            idx++;
        }
        return idx == 0 ? $"{val:F0} {units[idx]}" : $"{val:F1} {units[idx]}";
    }

    public void Dispose()
    {
        _hotkeyManager?.Dispose();
        _trayController?.Dispose();
        _clickHighlightOverlay.Dispose();
        _captureEngine.Dispose();
    }
}
