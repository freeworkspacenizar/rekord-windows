using System;

namespace RekordCore;

public readonly record struct SizeD(double Width, double Height);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2.0;
    public double MidY => Y + Height / 2.0;

    public bool Contains(RectD other) =>
        other.MinX >= MinX &&
        other.MinY >= MinY &&
        other.MaxX <= MaxX &&
        other.MaxY <= MaxY;

    public bool Intersects(RectD other) =>
        MinX < other.MaxX &&
        MaxX > other.MinX &&
        MinY < other.MaxY &&
        MaxY > other.MinY;

    public RectD Intersect(RectD other)
    {
        var left = Math.Max(MinX, other.MinX);
        var top = Math.Max(MinY, other.MinY);
        var right = Math.Min(MaxX, other.MaxX);
        var bottom = Math.Min(MaxY, other.MaxY);
        if (right <= left || bottom <= top)
            return default;
        return new RectD(left, top, right - left, bottom - top);
    }

    public RectD InsetBy(double dx, double dy) =>
        new(X + dx, Y + dy, Math.Max(0, Width - dx * 2), Math.Max(0, Height - dy * 2));
}

public enum AspectRatioPreset
{
    Free,
    Ratio16x9,
    Ratio4x3,
    Ratio1x1
}

public static class AspectRatioPresetExtensions
{
    public static double? GetRatio(this AspectRatioPreset preset) => preset switch
    {
        AspectRatioPreset.Free => null,
        AspectRatioPreset.Ratio16x9 => 16.0 / 9.0,
        AspectRatioPreset.Ratio4x3 => 4.0 / 3.0,
        AspectRatioPreset.Ratio1x1 => 1.0,
        _ => null
    };

    public static string GetDisplayName(this AspectRatioPreset preset) => preset switch
    {
        AspectRatioPreset.Free => "Free",
        AspectRatioPreset.Ratio16x9 => "16:9",
        AspectRatioPreset.Ratio4x3 => "4:3",
        AspectRatioPreset.Ratio1x1 => "1:1",
        _ => "Free"
    };
}

public enum ResizeHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

/// <summary>
/// Deep module encapsulating dynamic capture region geometry (top-left origin, Windows/WPF coordinates),
/// aspect-ratio clamping, live pan, and even-pixel encoder alignment.
/// </summary>
public static class CaptureGeometry
{
    public const double MinimumDimension = 120.0;

    /// <summary>
    /// Aligns a dimension down to the nearest even integer (minimum 2) for H.264/MediaFoundation compatibility.
    /// </summary>
    public static int EvenPixelDimension(double value)
    {
        var floored = Math.Max(2, (int)Math.Floor(value));
        return floored % 2 == 0 ? floored : Math.Max(2, floored - 1);
    }

    /// <summary>
    /// Clamps a rectangle inside <paramref name="bounds"/> while respecting <paramref name="minSize"/>.
    /// </summary>
    public static RectD ClampRect(RectD rect, RectD bounds, double minSize = MinimumDimension)
    {
        var maxW = Math.Max(minSize, bounds.Width);
        var maxH = Math.Max(minSize, bounds.Height);
        var w = Math.Min(Math.Max(rect.Width, minSize), maxW);
        var h = Math.Min(Math.Max(rect.Height, minSize), maxH);

        var minX = bounds.MinX;
        var maxX = bounds.MaxX - w;
        var minY = bounds.MinY;
        var maxY = bounds.MaxY - h;

        var x = Math.Min(Math.Max(rect.X, minX), Math.Max(minX, maxX));
        var y = Math.Min(Math.Max(rect.Y, minY), Math.Max(minY, maxY));
        return new RectD(Math.Round(x), Math.Round(y), Math.Round(w), Math.Round(h));
    }

    /// <summary>
    /// Applies an aspect ratio preset around the center of <paramref name="rect"/>, clamped to <paramref name="bounds"/>.
    /// </summary>
    public static RectD ApplyAspectRatio(AspectRatioPreset preset, RectD rect, RectD bounds)
    {
        var targetRatio = preset.GetRatio();
        if (targetRatio is null)
            return ClampRect(rect, bounds);

        var ratio = targetRatio.Value;
        var centerX = rect.MidX;
        var centerY = rect.MidY;
        var newWidth = Math.Max(rect.Width, MinimumDimension);
        var newHeight = newWidth / ratio;

        if (newHeight < MinimumDimension)
        {
            newHeight = MinimumDimension;
            newWidth = newHeight * ratio;
        }

        if (newWidth > bounds.Width)
        {
            newWidth = bounds.Width;
            newHeight = newWidth / ratio;
        }

        if (newHeight > bounds.Height)
        {
            newHeight = bounds.Height;
            newWidth = newHeight * ratio;
        }

        var candidate = new RectD(
            centerX - newWidth / 2.0,
            centerY - newHeight / 2.0,
            newWidth,
            newHeight
        );
        return ClampRect(candidate, bounds);
    }

    /// <summary>
    /// Computes a resized rectangle in top-left origin coordinates (Windows/WPF) for a dragged handle.
    /// </summary>
    public static RectD Resize(
        RectD rect,
        ResizeHandle handle,
        SizeD delta,
        AspectRatioPreset aspectRatio,
        RectD bounds)
    {
        var minX = rect.MinX;
        var maxX = rect.MaxX;
        var minY = rect.MinY;
        var maxY = rect.MaxY;

        switch (handle)
        {
            case ResizeHandle.Left:
                minX += delta.Width;
                break;
            case ResizeHandle.Right:
                maxX += delta.Width;
                break;
            case ResizeHandle.Top:
                minY += delta.Height;
                break;
            case ResizeHandle.Bottom:
                maxY += delta.Height;
                break;
            case ResizeHandle.TopLeft:
                minX += delta.Width;
                minY += delta.Height;
                break;
            case ResizeHandle.TopRight:
                maxX += delta.Width;
                minY += delta.Height;
                break;
            case ResizeHandle.BottomLeft:
                minX += delta.Width;
                maxY += delta.Height;
                break;
            case ResizeHandle.BottomRight:
                maxX += delta.Width;
                maxY += delta.Height;
                break;
        }

        // Enforce minimum dimensions while anchoring the opposite edge.
        if (maxX - minX < MinimumDimension)
        {
            if (handle is ResizeHandle.Left or ResizeHandle.TopLeft or ResizeHandle.BottomLeft)
                minX = maxX - MinimumDimension;
            else
                maxX = minX + MinimumDimension;
        }

        if (maxY - minY < MinimumDimension)
        {
            if (handle is ResizeHandle.Top or ResizeHandle.TopLeft or ResizeHandle.TopRight)
                minY = maxY - MinimumDimension;
            else
                maxY = minY + MinimumDimension;
        }

        // Clamp raw edges to display bounds.
        minX = Math.Max(bounds.MinX, minX);
        maxX = Math.Min(bounds.MaxX, maxX);
        minY = Math.Max(bounds.MinY, minY);
        maxY = Math.Min(bounds.MaxY, maxY);

        if (aspectRatio.GetRatio() is double ratio)
        {
            var width = Math.Max(MinimumDimension, maxX - minX);
            var height = Math.Max(MinimumDimension, maxY - minY);

            switch (handle)
            {
                case ResizeHandle.Top:
                case ResizeHandle.Bottom:
                {
                    var targetW = Math.Min(bounds.Width, Math.Max(MinimumDimension, height * ratio));
                    var midX = (minX + maxX) / 2.0;
                    minX = midX - targetW / 2.0;
                    maxX = midX + targetW / 2.0;
                    if (minX < bounds.MinX)
                    {
                        minX = bounds.MinX;
                        maxX = minX + targetW;
                    }
                    else if (maxX > bounds.MaxX)
                    {
                        maxX = bounds.MaxX;
                        minX = maxX - targetW;
                    }
                    break;
                }
                case ResizeHandle.Left:
                case ResizeHandle.Right:
                {
                    var targetH = Math.Min(bounds.Height, Math.Max(MinimumDimension, width / ratio));
                    var midY = (minY + maxY) / 2.0;
                    minY = midY - targetH / 2.0;
                    maxY = midY + targetH / 2.0;
                    if (minY < bounds.MinY)
                    {
                        minY = bounds.MinY;
                        maxY = minY + targetH;
                    }
                    else if (maxY > bounds.MaxY)
                    {
                        maxY = bounds.MaxY;
                        minY = maxY - targetH;
                    }
                    break;
                }
                case ResizeHandle.TopLeft:
                case ResizeHandle.TopRight:
                case ResizeHandle.BottomLeft:
                case ResizeHandle.BottomRight:
                {
                    var adjustedH = width / ratio;
                    if (handle is ResizeHandle.TopLeft or ResizeHandle.TopRight)
                    {
                        minY = maxY - adjustedH;
                        if (minY < bounds.MinY)
                        {
                            minY = bounds.MinY;
                            var clampedW = (maxY - minY) * ratio;
                            if (handle == ResizeHandle.TopLeft)
                                minX = maxX - clampedW;
                            else
                                maxX = minX + clampedW;
                        }
                    }
                    else
                    {
                        maxY = minY + adjustedH;
                        if (maxY > bounds.MaxY)
                        {
                            maxY = bounds.MaxY;
                            var clampedW = (maxY - minY) * ratio;
                            if (handle == ResizeHandle.BottomLeft)
                                minX = maxX - clampedW;
                            else
                                maxX = minX + clampedW;
                        }
                    }
                    break;
                }
            }
        }

        var result = new RectD(minX, minY, maxX - minX, maxY - minY);
        return ClampRect(result, bounds);
    }

    /// <summary>
    /// Creates a new selection rectangle from a click-and-drag gesture between <c>(anchorX, anchorY)</c>
    /// and <c>(currentX, currentY)</c> in any quadrant, optionally enforcing <paramref name="aspectRatio"/>
    /// and clamping inside <paramref name="bounds"/> with at least <see cref="MinimumDimension"/>.
    /// </summary>
    public static RectD CreateFromDrag(
        double anchorX,
        double anchorY,
        double currentX,
        double currentY,
        AspectRatioPreset aspectRatio,
        RectD bounds)
    {
        var ax = Math.Clamp(anchorX, bounds.MinX, bounds.MaxX);
        var ay = Math.Clamp(anchorY, bounds.MinY, bounds.MaxY);
        var cx = Math.Clamp(currentX, bounds.MinX, bounds.MaxX);
        var cy = Math.Clamp(currentY, bounds.MinY, bounds.MaxY);

        var dx = cx - ax;
        var dy = cy - ay;
        var signX = dx >= 0 ? 1.0 : -1.0;
        var signY = dy >= 0 ? 1.0 : -1.0;

        var width = Math.Max(MinimumDimension, Math.Abs(dx));
        var height = Math.Max(MinimumDimension, Math.Abs(dy));

        if (aspectRatio.GetRatio() is double ratio)
        {
            // Fit width/height cleanly to the target aspect ratio while preserving drag direction
            var candidateH = width / ratio;
            if (candidateH < MinimumDimension)
            {
                height = MinimumDimension;
                width = height * ratio;
            }
            else
            {
                height = candidateH;
            }

            if (width > bounds.Width)
            {
                width = bounds.Width;
                height = width / ratio;
            }
            if (height > bounds.Height)
            {
                height = bounds.Height;
                width = height * ratio;
            }
        }

        var x = signX >= 0 ? ax : ax - width;
        var y = signY >= 0 ? ay : ay - height;
        return ClampRect(new RectD(x, y, width, height), bounds);
    }

    /// <summary>
    /// Moves a rectangle by <paramref name="delta"/> while locking width &amp; height (used for live panning during recording).
    /// </summary>
    public static RectD Pan(RectD rect, SizeD delta, RectD bounds)
    {
        var moved = new RectD(rect.X + delta.Width, rect.Y + delta.Height, rect.Width, rect.Height);
        return ClampRect(moved, bounds, Math.Min(rect.Width, rect.Height));
    }

    /// <summary>
    /// Computes output capture pixel dimensions (even-aligned) for a logical DIP size and monitor DPI scale.
    /// </summary>
    public static (int Width, int Height) CapturePixelSize(SizeD logicalSize, double dpiScale)
    {
        var safeScale = Math.Max(1.0, dpiScale);
        var w = EvenPixelDimension(logicalSize.Width * safeScale);
        var h = EvenPixelDimension(logicalSize.Height * safeScale);
        return (w, h);
    }

    /// <summary>
    /// Converts a display-local selection rect in WPF DIPs into physical pixel coordinates clamped to physical display dimensions.
    /// </summary>
    public static (int X, int Y, int Width, int Height) ToPhysicalPixelRect(
        RectD logicalLocalRect,
        int physicalDisplayWidth,
        int physicalDisplayHeight,
        double dpiScale)
    {
        var safeScale = Math.Max(1.0, dpiScale);
        var (w, h) = CapturePixelSize(new SizeD(logicalLocalRect.Width, logicalLocalRect.Height), safeScale);
        w = Math.Min(w, EvenPixelDimension(physicalDisplayWidth));
        h = Math.Min(h, EvenPixelDimension(physicalDisplayHeight));

        var rawX = (int)Math.Round(logicalLocalRect.X * safeScale);
        var rawY = (int)Math.Round(logicalLocalRect.Y * safeScale);
        var x = Math.Clamp(rawX, 0, Math.Max(0, physicalDisplayWidth - w));
        var y = Math.Clamp(rawY, 0, Math.Max(0, physicalDisplayHeight - h));
        return (x, y, w, h);
    }

    /// <summary>
    /// Default centered selection rect (55% width x 55% height, clamped cleanly) for a display in logical DIPs.
    /// </summary>
    public static RectD DefaultSelectionRect(SizeD displaySize)
    {
        var w = Math.Min(Math.Max(640.0, displaySize.Width * 0.55), Math.Max(MinimumDimension, displaySize.Width - 80.0));
        var h = Math.Min(Math.Max(400.0, displaySize.Height * 0.55), Math.Max(MinimumDimension, displaySize.Height - 140.0));
        var rect = new RectD(
            (displaySize.Width - w) / 2.0,
            (displaySize.Height - h) / 2.0,
            w,
            h
        );
        return ClampRect(rect, new RectD(0, 0, displaySize.Width, displaySize.Height));
    }
}
