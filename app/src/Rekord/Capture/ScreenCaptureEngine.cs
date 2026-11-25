using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Rekord.Native;
using Rekord.SystemServices;
using RekordCore;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Rekord.Capture;

public sealed record RecordingResult(
    string VideoPath,
    double Duration,
    SizeD LogicalSize,
    SizeD PixelSize,
    double DpiScale
);

/// <summary>
/// Deep module driving Windows screen capture via DXGI Desktop Duplication (GPU-accelerated, zero Win10 yellow border)
/// with automatic GDI BitBlt fallback, piped into bundled FFmpeg (h264_mf / LGPL fallback) with Pause/Resume PTS retiming
/// and live pan (<see cref="UpdateRegion"/>).
/// </summary>
public sealed class ScreenCaptureEngine : IDisposable
{
    private readonly ExportEngine _exportEngine;
    private readonly object _stateLock = new();

    private CancellationTokenSource? _captureCts;
    private Task? _captureLoopTask;
    private Process? _ffmpegProcess;
    private RecordingTimeline _timeline;

    private DisplayMonitor? _activeMonitor;
    private SizeD _logicalSize;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _cropX;
    private int _cropY;
    private int _captureFps = 30;
    private bool _showCursor = true;
    private string? _outputPath;
    private int _writtenFrames;

    public event Action<double>? DurationUpdated;
    public event Action<Exception>? StreamFailed;

    public ScreenCaptureEngine(ExportEngine exportEngine)
    {
        _exportEngine = exportEngine;
    }

    public Task StartAsync(
        DisplayMonitor monitor,
        RectD logicalLocalRect,
        int captureFps = 30,
        bool showCursor = true)
    {
        Abort();

        var ffmpegPath = _exportEngine.ResolveFfmpegPath();
        var (cropX, cropY, pixelW, pixelH) = CaptureGeometry.ToPhysicalPixelRect(
            logicalLocalRect,
            monitor.PhysicalWidth,
            monitor.PhysicalHeight,
            monitor.DpiScale
        );

        var clampedFps = Math.Clamp(captureFps, 15, 60);
        var tempPath = Path.Combine(Path.GetTempPath(), $"Rekord_Raw_{Guid.NewGuid():N}.mp4");
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        var process = StartFfmpegPipe(ffmpegPath, tempPath, pixelW, pixelH, clampedFps);
        var cts = new CancellationTokenSource();

        lock (_stateLock)
        {
            _activeMonitor = monitor;
            _logicalSize = new SizeD(logicalLocalRect.Width, logicalLocalRect.Height);
            _pixelWidth = pixelW;
            _pixelHeight = pixelH;
            _cropX = cropX;
            _cropY = cropY;
            _captureFps = clampedFps;
            _showCursor = showCursor;
            _outputPath = tempPath;
            _writtenFrames = 0;
            _timeline.Reset();
            _ffmpegProcess = process;
            _captureCts = cts;
            _captureLoopTask = Task.Run(() => CaptureLoop(process, monitor, pixelW, pixelH, clampedFps, showCursor, cts.Token));
        }

        return Task.CompletedTask;
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            _timeline.Pause();
        }
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            _timeline.Resume();
        }
    }

    /// <summary>
    /// Updates capture origin live during recording while keeping width &amp; height strictly locked.
    /// Thread-safe and race-free: the capture loop reads the latest atomic crop origin each frame.
    /// </summary>
    public void UpdateRegion(RectD logicalLocalRect)
    {
        lock (_stateLock)
        {
            if (_activeMonitor is null) return;
            var (newX, newY, _, _) = CaptureGeometry.ToPhysicalPixelRect(
                new RectD(logicalLocalRect.X, logicalLocalRect.Y, _logicalSize.Width, _logicalSize.Height),
                _activeMonitor.PhysicalWidth,
                _activeMonitor.PhysicalHeight,
                _activeMonitor.DpiScale
            );
            _cropX = newX;
            _cropY = newY;
        }
    }

    /// <summary>
    /// Immediately cancels active capture, kills FFmpeg, and deletes the temporary raw MP4 file.
    /// Safe to call even when idle.
    /// </summary>
    public void Abort()
    {
        CancellationTokenSource? cts;
        Process? proc;
        string? tempPath;

        lock (_stateLock)
        {
            cts = _captureCts;
            proc = _ffmpegProcess;
            tempPath = _outputPath;
            _captureCts = null;
            _captureLoopTask = null;
            _ffmpegProcess = null;
            _outputPath = null;
            _activeMonitor = null;
            _writtenFrames = 0;
            _timeline.Reset();
        }

        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
            try { proc.Dispose(); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(tempPath))
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }
    }

    public async Task<RecordingResult> StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loopTask;
        Process? proc;
        string? tempPath;
        SizeD logicalSize;
        int pixelW;
        int pixelH;
        double dpiScale;

        lock (_stateLock)
        {
            cts = _captureCts;
            loopTask = _captureLoopTask;
            proc = _ffmpegProcess;
            tempPath = _outputPath;
            logicalSize = _logicalSize;
            pixelW = _pixelWidth;
            pixelH = _pixelHeight;
            dpiScale = _activeMonitor?.DpiScale ?? 1.0;

            _captureCts = null;
            _captureLoopTask = null;
            _ffmpegProcess = null;
            _outputPath = null;
            _activeMonitor = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        if (loopTask is not null)
        {
            try { await loopTask.ConfigureAwait(false); } catch { }
        }

        int frames;
        double duration;
        lock (_stateLock)
        {
            frames = _writtenFrames;
            duration = _timeline.RecordedDurationSeconds;
        }

        if (proc is not null)
        {
            try
            {
                proc.StandardInput.BaseStream.Flush();
                proc.StandardInput.Close();
            }
            catch { }

            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await proc.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
            proc.Dispose();
        }

        if (string.IsNullOrWhiteSpace(tempPath) || frames <= 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw new InvalidOperationException("Tidak ada frame yang berhasil direkam.");
        }

        return new RecordingResult(
            VideoPath: tempPath,
            Duration: Math.Max(0.25, duration),
            LogicalSize: logicalSize,
            PixelSize: new SizeD(pixelW, pixelH),
            DpiScale: dpiScale
        );
    }

    private static Process StartFfmpegPipe(string ffmpegPath, string outputPath, int pixelW, int pixelH, int fps)
    {
        var proc = TrySpawnFfmpeg(ffmpegPath, ExportEngine.BuildRawCaptureArguments(outputPath, pixelW, pixelH, fps, useHardwareMediaFoundation: true));
        if (proc is not null)
            return proc;

        // Automatic fallback to LGPL software encoder if MediaFoundation h264_mf fails to start
        var fallback = TrySpawnFfmpeg(ffmpegPath, ExportEngine.BuildRawCaptureArguments(outputPath, pixelW, pixelH, fps, useHardwareMediaFoundation: false));
        return fallback ?? throw new InvalidOperationException("Gagal memulai proses encoder FFmpeg.");
    }

    private static Process? TrySpawnFfmpeg(string ffmpegPath, System.Collections.Generic.IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            proc.Dispose();
            return null;
        }

        // Drain stderr asynchronously to prevent pipe buffer deadlocks
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { }
            }
            catch { }
        });

        // Quick probe: if encoder immediately rejected codec options, it exits within ~150ms
        if (proc.WaitForExit(150))
        {
            proc.Dispose();
            return null;
        }

        return proc;
    }

    private void CaptureLoop(
        Process ffmpegProcess,
        DisplayMonitor monitor,
        int width,
        int height,
        int fps,
        bool showCursor,
        CancellationToken ct)
    {
        var frameBuffer = new byte[width * height * 4];
        var hasLastValidFrame = false;
        var sw = Stopwatch.StartNew();
        var frameIntervalTicks = TimeSpan.TicksPerSecond / fps;
        var nextFrameTicks = 0L;

        DxgiMonitorDuplicator? dxgi = DxgiMonitorDuplicator.TryCreate(monitor, width, height);
        GdiRegionGrabber? gdi = null;

        try
        {
            var stdin = ffmpegProcess.StandardInput.BaseStream;

            while (!ct.IsCancellationRequested)
            {
                if (ffmpegProcess.HasExited)
                    throw new IOException("Proses encoder FFmpeg berhenti secara tak terduga.");

                var now = sw.Elapsed;
                TimeSpan? adjusted;
                int cropX;
                int cropY;
                lock (_stateLock)
                {
                    adjusted = _timeline.ProcessFrameTimestamp(now);
                    cropX = _cropX;
                    cropY = _cropY;
                }

                if (adjusted.HasValue)
                {
                    var grabbed = false;
                    if (dxgi is not null)
                    {
                        var dxgiStatus = dxgi.TryGrabFrame(cropX, cropY, showCursor, frameBuffer);
                        if (dxgiStatus == DxgiGrabStatus.Captured)
                        {
                            grabbed = true;
                            hasLastValidFrame = true;
                        }
                        else if (dxgiStatus == DxgiGrabStatus.NoChange && hasLastValidFrame)
                        {
                            // Screen unchanged since previous frame — reuse last frame buffer smoothly
                            grabbed = true;
                        }
                        else if (dxgiStatus == DxgiGrabStatus.AccessLost)
                        {
                            // Fallback cleanly to GDI BitBlt if DXGI loses access (UAC/RDP/GPU switch)
                            dxgi.Dispose();
                            dxgi = null;
                        }
                    }

                    if (!grabbed && dxgi is null)
                    {
                        gdi ??= new GdiRegionGrabber(width, height);
                        if (gdi.GrabFrame(monitor.PhysicalX + cropX, monitor.PhysicalY + cropY, showCursor, frameBuffer))
                        {
                            grabbed = true;
                            hasLastValidFrame = true;
                        }
                    }

                    if (grabbed)
                    {
                        stdin.Write(frameBuffer, 0, frameBuffer.Length);
                        double dur;
                        lock (_stateLock)
                        {
                            _writtenFrames++;
                            dur = _timeline.RecordedDurationSeconds;
                        }
                        DurationUpdated?.Invoke(dur);
                    }
                }

                nextFrameTicks += frameIntervalTicks;
                var sleepTicks = nextFrameTicks - sw.ElapsedTicks;
                if (sleepTicks > TimeSpan.TicksPerMillisecond)
                {
                    var sleepMs = (int)(sleepTicks / TimeSpan.TicksPerMillisecond);
                    if (ct.WaitHandle.WaitOne(Math.Min(sleepMs, 50)))
                        break;
                }
                else if (sleepTicks < -frameIntervalTicks * 2)
                {
                    // Re-sync pace if system stalled briefly
                    nextFrameTicks = sw.ElapsedTicks;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                StreamFailed?.Invoke(ex);
        }
        finally
        {
            dxgi?.Dispose();
            gdi?.Dispose();
        }
    }

    public void Dispose() => Abort();
}

internal enum DxgiGrabStatus
{
    Captured,
    NoChange,
    AccessLost
}

/// <summary>
/// GPU-accelerated DXGI Desktop Duplication grabber targeting a specific display monitor,
/// copying only the cropped capture region into a CPU-readable staging texture.
/// </summary>
internal sealed class DxgiMonitorDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _stagingTexture;
    private readonly int _width;
    private readonly int _height;
    private readonly int _monitorPhysX;
    private readonly int _monitorPhysY;
    private readonly int _monitorPhysW;
    private readonly int _monitorPhysH;

    private DxgiMonitorDuplicator(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication,
        ID3D11Texture2D stagingTexture,
        DisplayMonitor monitor,
        int width,
        int height)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        _stagingTexture = stagingTexture;
        _width = width;
        _height = height;
        _monitorPhysX = monitor.PhysicalX;
        _monitorPhysY = monitor.PhysicalY;
        _monitorPhysW = monitor.PhysicalWidth;
        _monitorPhysH = monitor.PhysicalHeight;
    }

    public static DxgiMonitorDuplicator? TryCreate(DisplayMonitor monitor, int width, int height)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            var bounds = desc.DesktopCoordinates;
                            var matchesDevice = string.Equals(desc.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase);
                            var matchesOrigin = bounds.Left == monitor.PhysicalX && bounds.Top == monitor.PhysicalY;
                            if (!matchesDevice && !matchesOrigin)
                                continue;

                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            var createResult = D3D11.D3D11CreateDevice(
                                adapter,
                                DriverType.Unknown,
                                DeviceCreationFlags.BgraSupport,
                                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                                out var device,
                                out var context
                            );
                            if (createResult.Failure || device is null || context is null)
                                return null;

                            try
                            {
                                var duplication = output1.DuplicateOutput(device);
                                var stagingDesc = new Texture2DDescription
                                {
                                    Width = (uint)width,
                                    Height = (uint)height,
                                    MipLevels = 1,
                                    ArraySize = 1,
                                    Format = Format.B8G8R8A8_UNorm,
                                    SampleDescription = new SampleDescription(1, 0),
                                    Usage = ResourceUsage.Staging,
                                    BindFlags = BindFlags.None,
                                    CpuAccessFlags = CpuAccessFlags.Read,
                                    MiscFlags = ResourceOptionFlags.None
                                };
                                var staging = device.CreateTexture2D(stagingDesc);
                                return new DxgiMonitorDuplicator(device, context, duplication, staging, monitor, width, height);
                            }
                            catch
                            {
                                context.Dispose();
                                device.Dispose();
                                return null;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully to GDI BitBlt.
        }
        return null;
    }

    public DxgiGrabStatus TryGrabFrame(int cropX, int cropY, bool showCursor, byte[] destinationBgra)
    {
        var hr = _duplication.AcquireNextFrame(16, out _, out var desktopResource);
        if (hr.Code == ResultCode.WaitTimeout.Code)
            return DxgiGrabStatus.NoChange;

        if (hr.Failure || desktopResource is null)
            return DxgiGrabStatus.AccessLost;

        try
        {
            using (desktopResource)
            using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
            {
                var safeX = Math.Clamp(cropX, 0, Math.Max(0, _monitorPhysW - _width));
                var safeY = Math.Clamp(cropY, 0, Math.Max(0, _monitorPhysH - _height));

                var srcBox = new Box(safeX, safeY, 0, safeX + _width, safeY + _height, 1);
                _context.CopySubresourceRegion(_stagingTexture, 0, 0, 0, 0, desktopTexture, 0, srcBox);

                var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var rowBytes = _width * 4;
                    var srcPtr = mapped.DataPointer;
                    var pitch = (int)mapped.RowPitch;
                    for (var y = 0; y < _height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(srcPtr, y * pitch), destinationBgra, y * rowBytes, rowBytes);
                    }
                }
                finally
                {
                    _context.Unmap(_stagingTexture, 0);
                }
            }

            if (showCursor)
            {
                GdiCursorOverlay.CompositeCursorOntoBgra(
                    destinationBgra,
                    _width,
                    _height,
                    _monitorPhysX + cropX,
                    _monitorPhysY + cropY
                );
            }

            return DxgiGrabStatus.Captured;
        }
        catch
        {
            return DxgiGrabStatus.AccessLost;
        }
        finally
        {
            try { _duplication.ReleaseFrame(); } catch { }
        }
    }

    public void Dispose()
    {
        _stagingTexture.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

/// <summary>
/// Reliable GDI BitBlt fallback grabber (supports RDP, VMs, and hybrid GPU laptops seamlessly).
/// </summary>
internal sealed class GdiRegionGrabber : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldObj;
    private Win32Interop.BITMAPINFO _bmi;

    public GdiRegionGrabber(int width, int height)
    {
        _width = width;
        _height = height;
        _screenDc = Win32Interop.GetDC(IntPtr.Zero);
        _memDc = Win32Interop.CreateCompatibleDC(_screenDc);
        _bitmap = Win32Interop.CreateCompatibleBitmap(_screenDc, width, height);
        _oldObj = Win32Interop.SelectObject(_memDc, _bitmap);

        _bmi = new Win32Interop.BITMAPINFO
        {
            bmiHeader = new Win32Interop.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };
    }

    public bool GrabFrame(int screenX, int screenY, bool showCursor, byte[] destinationBgra)
    {
        if (!Win32Interop.BitBlt(_memDc, 0, 0, _width, _height, _screenDc, screenX, screenY, Win32Interop.SRCCOPY | Win32Interop.CAPTUREBLT))
            return false;

        if (showCursor)
            GdiCursorOverlay.DrawCursorOnDc(_memDc, screenX, screenY);

        var scanLines = Win32Interop.GetDIBits(_memDc, _bitmap, 0, (uint)_height, destinationBgra, ref _bmi, Win32Interop.DIB_RGB_COLORS);
        return scanLines > 0;
    }

    public void Dispose()
    {
        if (_memDc != IntPtr.Zero && _oldObj != IntPtr.Zero)
            Win32Interop.SelectObject(_memDc, _oldObj);
        if (_bitmap != IntPtr.Zero)
            Win32Interop.DeleteObject(_bitmap);
        if (_memDc != IntPtr.Zero)
            Win32Interop.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero)
            Win32Interop.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}

internal static class GdiCursorOverlay
{
    public static void DrawCursorOnDc(IntPtr targetDc, int originX, int originY)
    {
        var ci = new Win32Interop.CURSORINFO { cbSize = Marshal.SizeOf<Win32Interop.CURSORINFO>() };
        if (!Win32Interop.GetCursorInfo(ref ci) || (ci.flags & Win32Interop.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
            return;

        var hotX = 0;
        var hotY = 0;
        if (Win32Interop.GetIconInfo(ci.hCursor, out var iconInfo))
        {
            hotX = iconInfo.xHotspot;
            hotY = iconInfo.yHotspot;
            if (iconInfo.hbmColor != IntPtr.Zero) Win32Interop.DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) Win32Interop.DeleteObject(iconInfo.hbmMask);
        }

        var drawX = ci.ptScreenPos.X - originX - hotX;
        var drawY = ci.ptScreenPos.Y - originY - hotY;
        Win32Interop.DrawIconEx(targetDc, drawX, drawY, ci.hCursor, 0, 0, 0, IntPtr.Zero, Win32Interop.DI_NORMAL);
    }

    public static void CompositeCursorOntoBgra(byte[] bgraBuffer, int width, int height, int originX, int originY)
    {
        var ci = new Win32Interop.CURSORINFO { cbSize = Marshal.SizeOf<Win32Interop.CURSORINFO>() };
        if (!Win32Interop.GetCursorInfo(ref ci) || (ci.flags & Win32Interop.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
            return;

        var localX = ci.ptScreenPos.X - originX;
        var localY = ci.ptScreenPos.Y - originY;
        if (localX < -32 || localY < -32 || localX > width + 32 || localY > height + 32)
            return;

        // Render cursor onto a tiny 48x48 scratch DIB and alpha-blend into destination buffer
        const int size = 48;
        var hotX = 0;
        var hotY = 0;
        if (Win32Interop.GetIconInfo(ci.hCursor, out var iconInfo))
        {
            hotX = iconInfo.xHotspot;
            hotY = iconInfo.yHotspot;
            if (iconInfo.hbmColor != IntPtr.Zero) Win32Interop.DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) Win32Interop.DeleteObject(iconInfo.hbmMask);
        }

        var drawLeft = localX - hotX;
        var drawTop = localY - hotY;

        var screenDc = Win32Interop.GetDC(IntPtr.Zero);
        var memDc = Win32Interop.CreateCompatibleDC(screenDc);
        var bmp = Win32Interop.CreateCompatibleBitmap(screenDc, size, size);
        var old = Win32Interop.SelectObject(memDc, bmp);
        try
        {
            var bmi = new Win32Interop.BITMAPINFO
            {
                bmiHeader = new Win32Interop.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            Win32Interop.DrawIconEx(memDc, 0, 0, ci.hCursor, 0, 0, 0, IntPtr.Zero, Win32Interop.DI_NORMAL);
            var cursorBytes = new byte[size * size * 4];
            if (Win32Interop.GetDIBits(memDc, bmp, 0, size, cursorBytes, ref bmi, Win32Interop.DIB_RGB_COLORS) <= 0)
                return;

            for (var cy = 0; cy < size; cy++)
            {
                var dy = drawTop + cy;
                if (dy < 0 || dy >= height) continue;

                for (var cx = 0; cx < size; cx++)
                {
                    var dx = drawLeft + cx;
                    if (dx < 0 || dx >= width) continue;

                    var si = (cy * size + cx) * 4;
                    var a = cursorBytes[si + 3];
                    var b = cursorBytes[si];
                    var g = cursorBytes[si + 1];
                    var r = cursorBytes[si + 2];

                    if (a == 0 && b == 0 && g == 0 && r == 0)
                        continue;

                    var di = (dy * width + dx) * 4;
                    if (a == 255 || (a == 0 && (b != 0 || g != 0 || r != 0)))
                    {
                        bgraBuffer[di] = b;
                        bgraBuffer[di + 1] = g;
                        bgraBuffer[di + 2] = r;
                        bgraBuffer[di + 3] = 255;
                    }
                    else
                    {
                        var inv = 255 - a;
                        bgraBuffer[di] = (byte)((b * a + bgraBuffer[di] * inv) / 255);
                        bgraBuffer[di + 1] = (byte)((g * a + bgraBuffer[di + 1] * inv) / 255);
                        bgraBuffer[di + 2] = (byte)((r * a + bgraBuffer[di + 2] * inv) / 255);
                        bgraBuffer[di + 3] = 255;
                    }
                }
            }
        }
        finally
        {
            Win32Interop.SelectObject(memDc, old);
            Win32Interop.DeleteObject(bmp);
            Win32Interop.DeleteDC(memDc);
            Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
