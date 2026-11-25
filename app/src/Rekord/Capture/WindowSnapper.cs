using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Rekord.Native;
using Rekord.SystemServices;
using RekordCore;

namespace Rekord.Capture;

public sealed record SnapWindowCandidate(
    IntPtr Handle,
    string AppName,
    string Title,
    RectD LogicalLocalRect
);

/// <summary>
/// Enumerates visible, non-minimized, non-cloaked top-level Windows intersecting the target monitor
/// (excluding Rekord itself) and returns their monitor-local WPF DIP rectangles for Snap-to-Window.
/// </summary>
public static class WindowSnapper
{
    public static IReadOnlyList<SnapWindowCandidate> FetchCandidates(DisplayMonitor monitor)
    {
        var results = new List<SnapWindowCandidate>();
        var myPid = (uint)Environment.ProcessId;
        var monitorPhys = new RectD(monitor.PhysicalX, monitor.PhysicalY, monitor.PhysicalWidth, monitor.PhysicalHeight);
        var monitorLocalLogicalBounds = new RectD(0, 0, monitor.LogicalBounds.Width, monitor.LogicalBounds.Height);

        Win32Interop.EnumWindows((hWnd, _) =>
        {
            if (!Win32Interop.IsWindowVisible(hWnd) || Win32Interop.IsIconic(hWnd))
                return true;

            Win32Interop.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0 || pid == myPid)
                return true;

            // Skip UWP suspended / virtual-desktop cloaked windows
            if (Win32Interop.DwmGetWindowAttribute(hWnd, Win32Interop.DWMWA_CLOAKED, out int cloaked, Marshal.SizeOf<int>()) == 0 && cloaked != 0)
                return true;

            var titleLen = Win32Interop.GetWindowTextLength(hWnd);
            if (titleLen <= 0)
                return true;

            // Prefer DWM extended frame bounds (excludes invisible Win10/11 drop shadow margins)
            Win32Interop.RECT rect;
            if (Win32Interop.DwmGetWindowAttribute(hWnd, Win32Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<Win32Interop.RECT>()) != 0)
            {
                if (!Win32Interop.GetWindowRect(hWnd, out rect))
                    return true;
            }

            if (rect.Width <= 0 || rect.Height <= 0)
                return true;

            var winPhys = new RectD(rect.Left, rect.Top, rect.Width, rect.Height);
            var intersection = winPhys.Intersect(monitorPhys);
            var minPhys = CaptureGeometry.MinimumDimension * monitor.DpiScale;
            if (intersection.Width < minPhys || intersection.Height < minPhys)
                return true;

            var sb = new StringBuilder(titleLen + 1);
            Win32Interop.GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title))
                return true;

            var appName = title;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (!string.IsNullOrWhiteSpace(proc.ProcessName))
                    appName = proc.ProcessName;
            }
            catch
            {
                // Keep window title as fallback app name.
            }

            var localLogical = new RectD(
                (intersection.X - monitor.PhysicalX) / monitor.DpiScale,
                (intersection.Y - monitor.PhysicalY) / monitor.DpiScale,
                intersection.Width / monitor.DpiScale,
                intersection.Height / monitor.DpiScale
            );

            results.Add(new SnapWindowCandidate(
                Handle: hWnd,
                AppName: appName,
                Title: title,
                LogicalLocalRect: CaptureGeometry.ClampRect(localLogical, monitorLocalLogicalBounds)
            ));

            return true;
        }, IntPtr.Zero);

        return results;
    }
}
