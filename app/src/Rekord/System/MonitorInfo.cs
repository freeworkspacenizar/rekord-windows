using System;
using System.Collections.Generic;
using System.Linq;
using Rekord.Native;
using RekordCore;
using WinForms = System.Windows.Forms;

namespace Rekord.SystemServices;

public sealed record DisplayMonitor(
    string DeviceName,
    int PhysicalX,
    int PhysicalY,
    int PhysicalWidth,
    int PhysicalHeight,
    RectD LogicalBounds,
    RectD LogicalWorkArea,
    double DpiScale,
    bool IsPrimary
);

public static class MonitorInfo
{
    public static IReadOnlyList<DisplayMonitor> GetAllMonitors()
    {
        var screens = WinForms.Screen.AllScreens;
        if (screens is null || screens.Length == 0)
            return Array.Empty<DisplayMonitor>();

        var result = new List<DisplayMonitor>(screens.Length);
        foreach (var screen in screens)
        {
            var b = screen.Bounds;
            var w = screen.WorkingArea;
            var dpiScale = GetDpiScaleForPoint(b.X + b.Width / 2, b.Y + b.Height / 2);

            var logicalBounds = new RectD(
                b.X / dpiScale,
                b.Y / dpiScale,
                b.Width / dpiScale,
                b.Height / dpiScale
            );
            var logicalWork = new RectD(
                w.X / dpiScale,
                w.Y / dpiScale,
                w.Width / dpiScale,
                w.Height / dpiScale
            );

            result.Add(new DisplayMonitor(
                DeviceName: screen.DeviceName,
                PhysicalX: b.X,
                PhysicalY: b.Y,
                PhysicalWidth: b.Width,
                PhysicalHeight: b.Height,
                LogicalBounds: logicalBounds,
                LogicalWorkArea: logicalWork,
                DpiScale: dpiScale,
                IsPrimary: screen.Primary
            ));
        }

        return result;
    }

    public static DisplayMonitor? FindMonitorAtCursor()
    {
        var monitors = GetAllMonitors();
        if (monitors.Count == 0)
            return null;

        if (Win32Interop.GetCursorPos(out var pt))
        {
            var match = monitors.FirstOrDefault(m =>
                pt.X >= m.PhysicalX && pt.X < m.PhysicalX + m.PhysicalWidth &&
                pt.Y >= m.PhysicalY && pt.Y < m.PhysicalY + m.PhysicalHeight);
            if (match is not null)
                return match;
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static double GetDpiScaleForPoint(int physicalX, int physicalY)
    {
        try
        {
            var hMon = Win32Interop.MonitorFromPoint(
                new Win32Interop.POINT { X = physicalX, Y = physicalY },
                Win32Interop.MONITOR_DEFAULTTONEAREST
            );
            if (hMon != IntPtr.Zero &&
                Win32Interop.GetDpiForMonitor(hMon, Win32Interop.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 &&
                dpiX >= 96)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // Fallback to 1.0 if shcore is unavailable.
        }
        return 1.0;
    }
}
