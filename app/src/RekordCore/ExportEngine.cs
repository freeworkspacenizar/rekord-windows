using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RekordCore;

public enum ExportFormat
{
    WebP,
    Gif
}

public static class ExportFormatExtensions
{
    public static string GetDisplayName(this ExportFormat format) => format switch
    {
        ExportFormat.WebP => "WebP",
        ExportFormat.Gif => "GIF",
        _ => "WebP"
    };

    public static string GetFileExtension(this ExportFormat format) => format switch
    {
        ExportFormat.WebP => "webp",
        ExportFormat.Gif => "gif",
        _ => "webp"
    };

    public static string GetBadgeText(this ExportFormat format) => format switch
    {
        ExportFormat.WebP => "5–10× smaller size",
        ExportFormat.Gif => "Universal compatibility",
        _ => ""
    };
}

public enum ExportQualityPreset
{
    Compact,
    Balanced,
    High
}

public static class ExportQualityPresetExtensions
{
    public static string GetDisplayName(this ExportQualityPreset preset) => preset switch
    {
        ExportQualityPreset.Compact => "Compact",
        ExportQualityPreset.Balanced => "Balanced",
        ExportQualityPreset.High => "High",
        _ => "Balanced"
    };

    public static string GetSubtitle(this ExportQualityPreset preset, ExportFormat format) => (format, preset) switch
    {
        (ExportFormat.WebP, ExportQualityPreset.Compact) => "Lossy q=55 • Smallest file",
        (ExportFormat.WebP, ExportQualityPreset.Balanced) => "Lossy q=75 • Crisp & ultra-light",
        (ExportFormat.WebP, ExportQualityPreset.High) => "Lossy q=90 • Full 24-bit color",
        (ExportFormat.Gif, ExportQualityPreset.Compact) => "128 colors • Smaller file",
        (ExportFormat.Gif, ExportQualityPreset.Balanced) => "256 colors • Crisp & efficient",
        (ExportFormat.Gif, ExportQualityPreset.High) => "256 colors • Best gradients",
        _ => ""
    };

    public static int GetWebPQuality(this ExportQualityPreset preset) => preset switch
    {
        ExportQualityPreset.Compact => 55,
        ExportQualityPreset.Balanced => 75,
        ExportQualityPreset.High => 90,
        _ => 75
    };

    public static int GetMaxColors(this ExportQualityPreset preset) => preset switch
    {
        ExportQualityPreset.Compact => 128,
        ExportQualityPreset.Balanced => 256,
        ExportQualityPreset.High => 256,
        _ => 256
    };

    public static string GetStatsMode(this ExportQualityPreset preset) => preset switch
    {
        ExportQualityPreset.Compact => "diff",
        ExportQualityPreset.Balanced => "diff",
        ExportQualityPreset.High => "full",
        _ => "diff"
    };

    public static string GetDitherMode(this ExportQualityPreset preset) => preset switch
    {
        ExportQualityPreset.Compact => "bayer:bayer_scale=3",
        ExportQualityPreset.Balanced => "sierra2_4a",
        ExportQualityPreset.High => "floyd_steinberg",
        _ => "sierra2_4a"
    };
}

public enum ExportScaleMode
{
    Logical1x,
    Native2x,
    Custom
}

public static class ExportScaleModeExtensions
{
    public static string GetShortLabel(this ExportScaleMode mode) => mode switch
    {
        ExportScaleMode.Logical1x => "1x",
        ExportScaleMode.Native2x => "2x",
        ExportScaleMode.Custom => "Custom",
        _ => "1x"
    };

    public static string GetDisplayLabel(this ExportScaleMode mode) => mode switch
    {
        ExportScaleMode.Logical1x => "1x (Logical)",
        ExportScaleMode.Native2x => "2x (Native DPI)",
        ExportScaleMode.Custom => "Custom Width",
        _ => "1x (Logical)"
    };
}

public sealed record ExportOptions
{
    public ExportFormat Format { get; init; } = ExportFormat.WebP;
    public int Fps { get; init; } = 15;
    public ExportScaleMode ScaleMode { get; init; } = ExportScaleMode.Logical1x;
    public int CustomWidth { get; init; } = 800;
    public int LogicalWidth { get; init; } = 800;
    public int NativePixelWidth { get; init; } = 1600;
    public ExportQualityPreset Quality { get; init; } = ExportQualityPreset.Balanced;
    public bool LoopForever { get; init; } = true;
    public TrimRange? TrimRange { get; init; }
    public double TotalDuration { get; init; }

    public int ClampedFps => Math.Clamp(Fps, 1, 60);

    /// <summary>
    /// Effective output pixel width for animated export (always even, never upscaled above native pixels unless custom).
    /// </summary>
    public int ResolvedTargetWidth
    {
        get
        {
            var safeLogical = Math.Max(120, LogicalWidth);
            var safeNative = Math.Max(120, NativePixelWidth);
            var safeCustom = Math.Max(120, CustomWidth);

            var raw = ScaleMode switch
            {
                ExportScaleMode.Logical1x => Math.Min(safeLogical, safeNative),
                ExportScaleMode.Native2x => safeNative,
                ExportScaleMode.Custom => safeCustom,
                _ => safeLogical
            };
            var clamped = Math.Max(64, raw);
            return clamped % 2 == 0 ? clamped : clamped - 1;
        }
    }

    public double EffectiveDuration => TrimRange?.Duration ?? Math.Max(0.0, TotalDuration);
}

public sealed class ExportException : Exception
{
    public ExportErrorKind Kind { get; }
    public int? ExitCode { get; }

    public ExportException(ExportErrorKind kind, string message, int? exitCode = null)
        : base(message)
    {
        Kind = kind;
        ExitCode = exitCode;
    }
}

public enum ExportErrorKind
{
    FfmpegNotFound,
    ProcessFailed,
    Cancelled
}

/// <summary>
/// Deep module responsible for FFmpeg discovery, Animated WebP / 2-pass GIF conversion,
/// trimmed MP4 export, raw-pipe capture encoding arguments, and real-time progress reporting.
/// </summary>
public sealed class ExportEngine
{
    private readonly string? _explicitBinaryPath;
    private readonly object _lock = new();
    private Process? _runningProcess;
    private bool _cancelRequested;

    public ExportEngine(string? ffmpegBinaryPath = null)
    {
        _explicitBinaryPath = ffmpegBinaryPath;
    }

    /// <summary>
    /// Resolves the bundled <c>ffmpeg.exe</c> (or <c>ffmpeg</c> on Unix during dev/testing),
    /// with fallback to <c>vendor/ffmpeg/</c> and system <c>PATH</c>.
    /// </summary>
    public string ResolveFfmpegPath(string? baseDirectory = null, string? workingDirectory = null)
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var appBase = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var workDir = string.IsNullOrWhiteSpace(workingDirectory) ? Directory.GetCurrentDirectory() : workingDirectory;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_explicitBinaryPath))
            candidates.Add(_explicitBinaryPath);

        candidates.Add(Path.Combine(appBase, exeName));
        candidates.Add(Path.Combine(appBase, "ffmpeg", exeName));
        candidates.Add(Path.Combine(appBase, "Resources", exeName));
        candidates.Add(Path.Combine(workDir, "vendor", "ffmpeg", exeName));

        var current = new DirectoryInfo(workDir);
        for (var i = 0; i < 7 && current?.Parent is not null; i++)
        {
            current = current.Parent;
            candidates.Add(Path.Combine(current.FullName, "vendor", "ffmpeg", exeName));
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
                return path;
        }

        // PATH fallback
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new ExportException(
            ExportErrorKind.FfmpegNotFound,
            $"Bundled {exeName} tidak ditemukan. Jalankan scripts/fetch-ffmpeg.ps1 terlebih dahulu.\nLokasi yang dicek:\n" +
            string.Join("\n", candidates.Distinct())
        );
    }

    /// <summary>
    /// Pure function generating FFmpeg CLI arguments for Pass 1 of True Two-Pass GIF encoding:
    /// streams frames in O(1) memory to generate an optimal 16x16 palette PNG on disk.
    /// </summary>
    public static IReadOnlyList<string> BuildGifPalettePassArguments(string inputPath, string palettePath, ExportOptions options)
    {
        var args = new List<string>
        {
            "-nostdin",
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-progress", "pipe:1",
            "-nostats"
        };

        if (options.TrimRange is TrimRange trim && !trim.IsFullRange(options.TotalDuration))
        {
            args.Add("-ss");
            args.Add(trim.Start.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-t");
            args.Add(trim.Duration.ToString("F3", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(inputPath);

        var targetWidth = options.ResolvedTargetWidth;
        var paletteFilter = $"fps={options.ClampedFps},scale={targetWidth}:-2:flags=lanczos,palettegen=max_colors={options.Quality.GetMaxColors()}:stats_mode={options.Quality.GetStatsMode()}";

        args.Add("-vf");
        args.Add(paletteFilter);
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-update");
        args.Add("1");
        args.Add(palettePath);

        return args;
    }

    /// <summary>
    /// Pure function generating FFmpeg CLI arguments for Pass 2 of True Two-Pass GIF encoding:
    /// streams frames in O(1) memory using the pre-generated palette PNG.
    /// </summary>
    public static IReadOnlyList<string> BuildGifEncodePassArguments(
        string inputPath,
        string palettePath,
        string outputPath,
        ExportOptions options)
    {
        var args = new List<string>
        {
            "-nostdin",
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-progress", "pipe:1",
            "-nostats"
        };

        if (options.TrimRange is TrimRange trim && !trim.IsFullRange(options.TotalDuration))
        {
            args.Add("-ss");
            args.Add(trim.Start.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-t");
            args.Add(trim.Duration.ToString("F3", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(inputPath);
        args.Add("-i");
        args.Add(palettePath);

        var targetWidth = options.ResolvedTargetWidth;
        var lavfi = $"fps={options.ClampedFps},scale={targetWidth}:-2:flags=lanczos[x];[x][1:v]paletteuse=dither={options.Quality.GetDitherMode()}:diff_mode=rectangle";

        args.Add("-lavfi");
        args.Add(lavfi);
        args.Add("-loop");
        args.Add(options.LoopForever ? "0" : "-1");
        args.Add(outputPath);

        return args;
    }

    /// <summary>
    /// Pure function generating FFmpeg CLI arguments for Animated WebP encoding (5–10x smaller than GIF).
    /// </summary>
    public static IReadOnlyList<string> BuildWebPArguments(string inputPath, string outputPath, ExportOptions options)
    {
        var args = new List<string>
        {
            "-nostdin",
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-progress", "pipe:1",
            "-nostats"
        };

        if (options.TrimRange is TrimRange trim && !trim.IsFullRange(options.TotalDuration))
        {
            args.Add("-ss");
            args.Add(trim.Start.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-t");
            args.Add(trim.Duration.ToString("F3", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(inputPath);

        var targetWidth = options.ResolvedTargetWidth;
        var filter = $"fps={options.ClampedFps},scale={targetWidth}:-2:flags=lanczos";

        args.Add("-vf");
        args.Add(filter);
        args.Add("-c:v");
        args.Add("libwebp_anim");
        args.Add("-lossless");
        args.Add("0");
        args.Add("-q:v");
        args.Add(options.Quality.GetWebPQuality().ToString(CultureInfo.InvariantCulture));
        args.Add("-compression_level");
        args.Add("4");
        args.Add("-preset");
        args.Add("default");
        args.Add("-loop");
        args.Add(options.LoopForever ? "0" : "1");
        args.Add("-an");
        args.Add(outputPath);

        return args;
    }

    /// <summary>
    /// Pure function generating FFmpeg CLI arguments for trimmed MP4 export.
    /// Uses stream copy when full range; uses LGPL MediaFoundation hardware H.264 (<c>h264_mf</c>)
    /// or LGPL software fallback (<c>mpeg4</c>) when trimmed.
    /// </summary>
    public static IReadOnlyList<string> BuildMp4Arguments(
        string inputPath,
        string outputPath,
        TrimRange? trimRange,
        double totalDuration,
        bool useHardwareMediaFoundation = true)
    {
        var args = new List<string>
        {
            "-nostdin",
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-progress", "pipe:1",
            "-nostats"
        };

        var needsTrim = trimRange.HasValue && !trimRange.Value.IsFullRange(totalDuration);
        if (needsTrim && trimRange is TrimRange trim)
        {
            args.Add("-ss");
            args.Add(trim.Start.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-t");
            args.Add(trim.Duration.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(inputPath);

            if (useHardwareMediaFoundation)
            {
                args.Add("-vf");
                args.Add("format=nv12");
                args.Add("-c:v");
                args.Add("h264_mf");
                args.Add("-rate_control");
                args.Add("quality");
                args.Add("-quality");
                args.Add("80");
            }
            else
            {
                args.Add("-c:v");
                args.Add("mpeg4");
                args.Add("-q:v");
                args.Add("3");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }

            args.Add("-movflags");
            args.Add("+faststart");
            args.Add(outputPath);
        }
        else
        {
            args.Add("-i");
            args.Add(inputPath);
            args.Add("-c");
            args.Add("copy");
            args.Add("-movflags");
            args.Add("+faststart");
            args.Add(outputPath);
        }

        return args;
    }

    /// <summary>
    /// Pure function generating FFmpeg CLI arguments for live raw BGRA stdin recording to MP4.
    /// </summary>
    public static IReadOnlyList<string> BuildRawCaptureArguments(
        string outputPath,
        int pixelWidth,
        int pixelHeight,
        int captureFps,
        bool useHardwareMediaFoundation = true)
    {
        var evenW = CaptureGeometry.EvenPixelDimension(pixelWidth);
        var evenH = CaptureGeometry.EvenPixelDimension(pixelHeight);
        var clampedFps = Math.Clamp(captureFps, 15, 60);

        var args = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", $"{evenW}x{evenH}",
            "-r", clampedFps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-an"
        };

        if (useHardwareMediaFoundation)
        {
            var bitrate = Math.Max(4_000_000, evenW * evenH * 6);
            args.Add("-vf");
            args.Add("format=nv12");
            args.Add("-c:v");
            args.Add("h264_mf");
            args.Add("-b:v");
            args.Add(bitrate.ToString(CultureInfo.InvariantCulture));
            args.Add("-g");
            args.Add(clampedFps.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add("-c:v");
            args.Add("mpeg4");
            args.Add("-q:v");
            args.Add("3");
            args.Add("-pix_fmt");
            args.Add("yuv420p");
        }

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);

        return args;
    }

    /// <summary>
    /// Parses an FFmpeg <c>-progress pipe:1</c> line (e.g., <c>out_time_us=1500000</c>) into a 0.0..1.0 fraction.
    /// </summary>
    public static double? ParseProgressFraction(string line, double expectedDuration)
    {
        if (expectedDuration <= 0 || string.IsNullOrEmpty(line))
            return null;

        if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
        {
            var raw = line["out_time_us=".Length..].Trim();
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var micros) && micros >= 0)
            {
                return Math.Min(0.99, Math.Max(0.0, (micros / 1_000_000.0) / expectedDuration));
            }
        }
        else if (line.StartsWith("progress=end", StringComparison.Ordinal))
        {
            return 1.0;
        }

        return null;
    }

    public async Task<string> ExportAnimationAsync(
        string inputPath,
        string outputPath,
        ExportOptions options,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var binaryPath = ResolveFfmpegPath();

        if (options.Format == ExportFormat.Gif)
        {
            var palettePath = Path.Combine(Path.GetTempPath(), $"Rekord_Palette_{Guid.NewGuid():N}.png");
            try
            {
                // Pass 1 (0% -> 35%): O(1)-memory palette generation
                var pass1Args = BuildGifPalettePassArguments(inputPath, palettePath, options);
                await RunFfmpegAsync(
                    binaryPath,
                    pass1Args,
                    options.EffectiveDuration,
                    p => onProgress?.Invoke(Math.Clamp(p, 0.0, 1.0) * 0.35),
                    cancellationToken
                );

                bool cancelledAfterPass1;
                lock (_lock)
                {
                    cancelledAfterPass1 = _cancelRequested || cancellationToken.IsCancellationRequested;
                }
                if (cancelledAfterPass1)
                    throw new ExportException(ExportErrorKind.Cancelled, "Conversion cancelled.");

                // Pass 2 (35% -> 100%): O(1)-memory GIF encoding with pre-generated palette
                var pass2Args = BuildGifEncodePassArguments(inputPath, palettePath, outputPath, options);
                await RunFfmpegAsync(
                    binaryPath,
                    pass2Args,
                    options.EffectiveDuration,
                    p => onProgress?.Invoke(0.35 + Math.Clamp(p, 0.0, 1.0) * 0.65),
                    cancellationToken
                );

                return outputPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(palettePath))
                        File.Delete(palettePath);
                }
                catch { }
            }
        }

        var webpArgs = BuildWebPArguments(inputPath, outputPath, options);
        await RunFfmpegAsync(binaryPath, webpArgs, options.EffectiveDuration, onProgress, cancellationToken);
        return outputPath;
    }

    public async Task<string> ExportMp4Async(
        string inputPath,
        string outputPath,
        TrimRange? trimRange,
        double totalDuration,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var binaryPath = ResolveFfmpegPath();
        var duration = trimRange?.Duration ?? totalDuration;
        var primaryArgs = BuildMp4Arguments(inputPath, outputPath, trimRange, totalDuration, useHardwareMediaFoundation: true);

        try
        {
            await RunFfmpegAsync(binaryPath, primaryArgs, duration, onProgress, cancellationToken);
            return outputPath;
        }
        catch (ExportException ex) when (ex.Kind == ExportErrorKind.ProcessFailed && trimRange.HasValue && !trimRange.Value.IsFullRange(totalDuration))
        {
            // Fallback to LGPL software encoder if MediaFoundation h264_mf is unavailable (e.g., Server/VM).
            var fallbackArgs = BuildMp4Arguments(inputPath, outputPath, trimRange, totalDuration, useHardwareMediaFoundation: false);
            await RunFfmpegAsync(binaryPath, fallbackArgs, duration, onProgress, cancellationToken);
            return outputPath;
        }
    }

    public void Cancel()
    {
        Process? proc;
        lock (_lock)
        {
            _cancelRequested = true;
            proc = _runningProcess;
        }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process already exited.
            }
        }
    }

    private async Task RunFfmpegAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        double expectedDuration,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();

        lock (_lock)
        {
            _cancelRequested = false;
            _runningProcess = process;
        }

        using var reg = cancellationToken.Register(Cancel);

        try
        {
            if (!process.Start())
                throw new ExportException(ExportErrorKind.ProcessFailed, "Gagal menjalankan proses FFmpeg.");

            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is string line)
                {
                    var fraction = ParseProgressFraction(line, expectedDuration);
                    if (fraction.HasValue)
                        onProgress?.Invoke(fraction.Value);
                }
            }, CancellationToken.None);

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is string line)
                {
                    lock (stderrBuilder)
                    {
                        stderrBuilder.AppendLine(line);
                    }
                }
            }, CancellationToken.None);

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);

            bool wasCancelled;
            lock (_lock)
            {
                wasCancelled = _cancelRequested || cancellationToken.IsCancellationRequested;
            }

            if (wasCancelled)
                throw new ExportException(ExportErrorKind.Cancelled, "Conversion cancelled.");

            if (process.ExitCode != 0)
            {
                var stderrLines = stderrBuilder.ToString()
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var tail = string.Join("\n", stderrLines.TakeLast(6));
                throw new ExportException(
                    ExportErrorKind.ProcessFailed,
                    $"FFmpeg exited with code {process.ExitCode}: {tail}",
                    process.ExitCode
                );
            }

            onProgress?.Invoke(1.0);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_runningProcess, process))
                    _runningProcess = null;
            }
        }
    }
}
