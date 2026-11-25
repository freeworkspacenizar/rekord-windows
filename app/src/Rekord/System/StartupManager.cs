using System;
using Microsoft.Win32;

namespace Rekord.SystemServices;

/// <summary>
/// Manages the current-user Windows Startup registry entry (HKCU\...\Run) without requiring Administrator rights.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "Rekord";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                    key.SetValue(AppValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry access failures on restricted environments.
        }
    }
}
