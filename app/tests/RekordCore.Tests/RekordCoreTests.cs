using System;
using System.IO;
using System.Linq;
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
    public void BuildGifArguments_Includes2PassPaletteAndTrim()
    {
        var trim = new TrimRange(1.5, 4.5, 8.0);
        var options = new ExportOptions
        {
            Format = ExportFormat.Gif,
            Fps = 20,
            ScaleMode = ExportScaleMode.Logical1x,
            LogicalWidth = 641,
            NativePixelWidth = 1282,
            Quality = ExportQualityPreset.Balanced,
            LoopForever = true,
            TrimRange = trim,
            TotalDuration = 8.0
        };

        var args = ExportEngine.BuildGifArguments(@"C:\tmp\in.mp4", @"C:\tmp\out.gif", options);
        Assert.Contains("-ss", args);
        Assert.Contains("1.500", args);
        Assert.Contains("-t", args);
        Assert.Contains("3.000", args);
        Assert.Contains("-loop", args);
        Assert.Contains("0", args);

        var filterIdx = args.ToList().IndexOf("-filter_complex");
        Assert.True(filterIdx >= 0);
        var filter = args[filterIdx + 1];
        Assert.Contains("fps=20,scale=640:-2:flags=lanczos", filter);
        Assert.Contains("palettegen=max_colors=256:stats_mode=diff", filter);
        Assert.Contains("paletteuse=dither=sierra2_4a:diff_mode=rectangle", filter);
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
