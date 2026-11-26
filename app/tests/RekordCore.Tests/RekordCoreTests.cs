using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RekordCore;
using Xunit;

namespace RekordCore.Tests;

public sealed class RekordCoreTests
{
    [Fact]
    public void EvenPixelDimension_AlignsOddValuesDown()
    {
        Assert.Equal(800, CaptureGeometry.EvenPixelDimension(801.9));
        Assert.Equal(800, CaptureGeometry.EvenPixelDimension(800.0));
        Assert.Equal(2, CaptureGeometry.EvenPixelDimension(1.0));
    }

    [Fact]
    public void ToPhysicalPixelRect_ScalesDpiAndAlignsEvenDimensions()
    {
        var logical = new RectD(100, 50, 401, 301);
        var (x, y, w, h) = CaptureGeometry.ToPhysicalPixelRect(logical, 2560, 1440, 1.5);

        Assert.Equal(150, x);
        Assert.Equal(75, y);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.Equal(600, w);
        Assert.Equal(450, h);
    }

    [Fact]
    public void ApplyAspectRatio_16x9_ClampsInsideBounds()
    {
        var bounds = new RectD(0, 0, 1280, 800);
        var initial = new RectD(200, 200, 640, 640);
        var result = CaptureGeometry.ApplyAspectRatio(AspectRatioPreset.Ratio16x9, initial, bounds);

        Assert.True(Math.Abs((result.Width / result.Height) - (16.0 / 9.0)) < 0.02);
        Assert.True(bounds.Contains(result));
    }

    [Fact]
    public void Resize_WorksForFreeAndAspectLockedModes()
    {
        var bounds = new RectD(0, 0, 1440, 900);
        var initial = new RectD(200, 200, 400, 400);

        var freeResized = CaptureGeometry.Resize(
            initial,
            ResizeHandle.BottomRight,
            new SizeD(100, 50),
            AspectRatioPreset.Free,
            bounds
        );
        Assert.True(Math.Abs(freeResized.Width - 500) <= 1);
        Assert.True(Math.Abs(freeResized.Height - 450) <= 1);

        var squareResized = CaptureGeometry.Resize(
            initial,
            ResizeHandle.TopRight,
            new SizeD(100, -20),
            AspectRatioPreset.Ratio1x1,
            bounds
        );
        Assert.True(Math.Abs(squareResized.Width - squareResized.Height) <= 1);
        Assert.True(bounds.Contains(squareResized));
    }

    [Fact]
    public void CreateFromDrag_SupportsAllQuadrantsAspectLockAndBoundsClamping()
    {
        var bounds = new RectD(0, 0, 1440, 900);

        // Top-left to bottom-right (free)
        var br = CaptureGeometry.CreateFromDrag(100, 100, 500, 350, AspectRatioPreset.Free, bounds);
        Assert.Equal(new RectD(100, 100, 400, 250), br);

        // Bottom-right to top-left (reversed quadrant)
        var tl = CaptureGeometry.CreateFromDrag(500, 350, 100, 100, AspectRatioPreset.Free, bounds);
        Assert.Equal(new RectD(100, 100, 400, 250), tl);

        // Tiny drag enforces minimum dimension
        var tiny = CaptureGeometry.CreateFromDrag(200, 200, 220, 215, AspectRatioPreset.Free, bounds);
        Assert.True(tiny.Width >= CaptureGeometry.MinimumDimension);
        Assert.True(tiny.Height >= CaptureGeometry.MinimumDimension);

        // Aspect ratio 16:9 stays locked and inside bounds
        var wide = CaptureGeometry.CreateFromDrag(100, 100, 740, 200, AspectRatioPreset.Ratio16x9, bounds);
        Assert.True(Math.Abs((wide.Width / wide.Height) - (16.0 / 9.0)) < 0.02);
        Assert.True(bounds.Contains(wide));
    }

    [Fact]
    public void Pan_LocksDimensionsAndClampsInsideDisplayEdge()
    {
        var bounds = new RectD(0, 0, 1000, 800);
        var initial = new RectD(700, 500, 250, 200);
        var panned = CaptureGeometry.Pan(initial, new SizeD(300, 300), bounds);

        Assert.Equal(initial.Width, panned.Width);
        Assert.Equal(initial.Height, panned.Height);
        Assert.Equal(1000, panned.MaxX);
        Assert.Equal(800, panned.MaxY);
    }

    [Fact]
    public void RecordingTimeline_RemovesPausedGapsContiguously()
    {
        var timeline = new RecordingTimeline();
        var t0 = TimeSpan.FromSeconds(10.0);
        var t1 = TimeSpan.FromSeconds(11.0);
        var tPause1 = TimeSpan.FromSeconds(11.5);
        var tPause2 = TimeSpan.FromSeconds(14.0);
        var tResume = TimeSpan.FromSeconds(15.0);
        var tAfter = TimeSpan.FromSeconds(16.0);

        Assert.Equal(0.0, timeline.ProcessFrameTimestamp(t0)!.Value.TotalSeconds, 3);
        Assert.Equal(1.0, timeline.ProcessFrameTimestamp(t1)!.Value.TotalSeconds, 3);

        timeline.Pause();
        Assert.Null(timeline.ProcessFrameTimestamp(tPause1));
        Assert.Null(timeline.ProcessFrameTimestamp(tPause2));

        timeline.Resume();
        Assert.Equal(1.5, timeline.ProcessFrameTimestamp(tResume)!.Value.TotalSeconds, 3);
        Assert.Equal(2.5, timeline.ProcessFrameTimestamp(tAfter)!.Value.TotalSeconds, 3);
        Assert.Equal(2.5, timeline.RecordedDurationSeconds, 3);
    }

    [Fact]
    public void FormatDuration_ProducesMmSsAndTenths()
    {
        Assert.Equal("01:05", RecordingTimeline.FormatDuration(65.4));
        Assert.Equal("01:05.4", RecordingTimeline.FormatDuration(65.4, includeTenths: true));
    }

    [Fact]
    public void BuildGifTwoPassArguments_StreamsWithoutSplitFilterAndIncludesTrim()
    {
        var trim = new TrimRange(1.5, 4.5, 8.0);
        var options = new ExportOptions
        {
            Format = ExportFormat.Gif,
            Fps = 24,
            ScaleMode = ExportScaleMode.Logical1x,
            LogicalWidth = 1559,
            NativePixelWidth = 1559,
            Quality = ExportQualityPreset.Balanced,
            LoopForever = true,
            TrimRange = trim,
            TotalDuration = 8.0
        };

        var pass1 = ExportEngine.BuildGifPalettePassArguments(@"C:\tmp\in.mp4", @"C:\tmp\pal.png", options);
        Assert.Contains("-ss", pass1);
        Assert.Contains("1.500", pass1);
        Assert.Contains("-t", pass1);
        Assert.Contains("3.000", pass1);
        Assert.Contains("-frames:v", pass1);
        Assert.Contains("-update", pass1);

        var vfIdx = pass1.ToList().IndexOf("-vf");
        Assert.True(vfIdx >= 0);
        var vf = pass1[vfIdx + 1];
        Assert.DoesNotContain("split", vf);
        Assert.Contains("fps=24,scale=1558:-2:flags=lanczos", vf);
        Assert.Contains("palettegen=max_colors=256:stats_mode=diff", vf);

        var pass2 = ExportEngine.BuildGifEncodePassArguments(@"C:\tmp\in.mp4", @"C:\tmp\pal.png", @"C:\tmp\out.gif", options);
        Assert.Contains("-ss", pass2);
        Assert.Contains("1.500", pass2);
        Assert.Contains("-t", pass2);
        Assert.Contains("3.000", pass2);
        Assert.Contains(@"C:\tmp\pal.png", pass2);
        Assert.Contains("-loop", pass2);
        Assert.Contains("0", pass2);

        var lavfiIdx = pass2.ToList().IndexOf("-lavfi");
        Assert.True(lavfiIdx >= 0);
        var lavfi = pass2[lavfiIdx + 1];
        Assert.DoesNotContain("split", lavfi);
        Assert.Contains("fps=24,scale=1558:-2:flags=lanczos[x]", lavfi);
        Assert.Contains("[x][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle", lavfi);
    }

    [Fact]
    public async Task ExportAnimationAsync_OneMinuteHighResGif_SucceedsWithoutOom()
    {
        var engine = new ExportEngine();
        string ffmpegPath;
        try
        {
            ffmpegPath = engine.ResolveFfmpegPath();
        }
        catch (ExportException)
        {
            // Skip E2E conversion if ffmpeg binary is not staged locally; CI always stages vendor/ffmpeg/ffmpeg.exe.
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"RekordGifStress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputMp4 = Path.Combine(tempDir, "stress_60s.mp4");
            var outputGif = Path.Combine(tempDir, "stress_60s.gif");

            // Generate 60s 1558x944 @ 24 FPS synthetic video matching exact reported OOM parameters
            var genPsi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var a in new[]
            {
                "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=1558x944:rate=24:duration=60",
                "-c:v", "mpeg4", "-q:v", "12", "-pix_fmt", "yuv420p",
                inputMp4
            })
            {
                genPsi.ArgumentList.Add(a);
            }

            using (var genProc = Process.Start(genPsi)!)
            {
                await genProc.WaitForExitAsync();
                Assert.Equal(0, genProc.ExitCode);
            }

            var palettesBefore = Directory.GetFiles(Path.GetTempPath(), "Rekord_Palette_*.png").Length;
            var maxProgress = 0.0;

            var options = new ExportOptions
            {
                Format = ExportFormat.Gif,
                Fps = 24,
                ScaleMode = ExportScaleMode.Logical1x,
                LogicalWidth = 1558,
                NativePixelWidth = 1558,
                Quality = ExportQualityPreset.Balanced,
                LoopForever = true,
                TotalDuration = 60.0
            };

            var exported = await engine.ExportAnimationAsync(inputMp4, outputGif, options, p =>
            {
                if (p > maxProgress) maxProgress = p;
            });

            Assert.True(File.Exists(exported));
            var header = new byte[6];
            using (var fs = File.OpenRead(exported))
            {
                var read = await fs.ReadAsync(header.AsMemory(0, 6));
                Assert.Equal(6, read);
            }
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(header));
            Assert.True(maxProgress >= 0.99, $"Expected progress >= 0.99, got {maxProgress}");

            var palettesAfter = Directory.GetFiles(Path.GetTempPath(), "Rekord_Palette_*.png").Length;
            Assert.Equal(palettesBefore, palettesAfter);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildWebPArguments_IncludesLibwebpAnimAndQualityPreset()
    {
        var options = new ExportOptions
        {
            Format = ExportFormat.WebP,
            Fps = 15,
            ScaleMode = ExportScaleMode.Logical1x,
            LogicalWidth = 640,
            NativePixelWidth = 1280,
            Quality = ExportQualityPreset.Balanced,
            LoopForever = true,
            TotalDuration = 3.0
        };

        var args = ExportEngine.BuildWebPArguments(@"C:\tmp\in.mp4", @"C:\tmp\out.webp", options);
        Assert.Contains("libwebp_anim", args);
        Assert.Contains("-q:v", args);
        Assert.Contains("75", args);
        Assert.Contains("-loop", args);
        Assert.Contains("0", args);
    }

    [Fact]
    public void BuildMp4Arguments_UsesStreamCopyForFullRangeAndHardwareMfForTrim()
    {
        var fullArgs = ExportEngine.BuildMp4Arguments(@"C:\in.mp4", @"C:\out.mp4", null, 5.0);
        Assert.Contains("-c", fullArgs);
        Assert.Contains("copy", fullArgs);

        var trim = new TrimRange(1.0, 3.5, 5.0);
        var trimHwArgs = ExportEngine.BuildMp4Arguments(@"C:\in.mp4", @"C:\out.mp4", trim, 5.0, useHardwareMediaFoundation: true);
        Assert.Contains("h264_mf", trimHwArgs);
        Assert.Contains("-ss", trimHwArgs);

        var trimSwArgs = ExportEngine.BuildMp4Arguments(@"C:\in.mp4", @"C:\out.mp4", trim, 5.0, useHardwareMediaFoundation: false);
        Assert.Contains("mpeg4", trimSwArgs);
    }

    [Fact]
    public void BuildRawCaptureArguments_AlignsEvenSizeAndUsesBgraPipe()
    {
        var args = ExportEngine.BuildRawCaptureArguments(@"C:\tmp\raw.mp4", 801, 451, 30, useHardwareMediaFoundation: true);
        Assert.Contains("rawvideo", args);
        Assert.Contains("bgra", args);
        Assert.Contains("800x450", args);
        Assert.Contains("pipe:0", args);
        Assert.Contains("h264_mf", args);
    }

    [Fact]
    public void ParseProgressFraction_ComputesNormalizedFraction()
    {
        var p = ExportEngine.ParseProgressFraction("out_time_us=2500000", 5.0);
        Assert.NotNull(p);
        Assert.Equal(0.5, p!.Value, 3);
        Assert.Equal(1.0, ExportEngine.ParseProgressFraction("progress=end", 5.0));
        Assert.Null(ExportEngine.ParseProgressFraction("frame=12", 5.0));
    }

    [Fact]
    public void AppSettings_PersistsJsonAndGeneratesCollisionFreeFilenames()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"RekordWinTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            var outDir = Path.Combine(tempDir, "output");

            var settings = new AppSettings(settingsPath)
            {
                QuickExportEnabled = true,
                DefaultFps = 24,
                CountdownSeconds = 3,
                StartWithWindows = true,
                OutputDirectoryPath = outDir
            };

            var reloaded = new AppSettings(settingsPath);
            Assert.True(reloaded.QuickExportEnabled);
            Assert.Equal(24, reloaded.DefaultFps);
            Assert.Equal(3, reloaded.CountdownSeconds);
            Assert.True(reloaded.StartWithWindows);

            var fixedDate = new DateTime(2026, 11, 25, 12, 0, 0);
            var first = reloaded.MakeOutputFilePath("webp", fixedDate);
            File.WriteAllText(first, "x");
            var second = reloaded.MakeOutputFilePath("webp", fixedDate);

            Assert.NotEqual(first, second);
            Assert.EndsWith("_2.webp", second, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
