using System;
using System.Windows.Interop;
using Rekord.Native;

namespace Rekord.SystemServices;

/// <summary>
/// Registers a system-wide Windows hotkey (Ctrl+Shift+6) using Win32 RegisterHotKey
/// on a dedicated message-only HWND so recording can be triggered from any app without admin privileges.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    public const string ShortcutDisplayLabel = "Ctrl+Shift+6";

    private const int HotkeyId = 0x524B; // 'RK'
    private readonly Action _onTrigger;
    private HwndSource? _hwndSource;
    private bool _isRegistered;

    public GlobalHotkeyManager(Action onTrigger)
    {
        _onTrigger = onTrigger;
    }

    public bool Register()
    {
        Unregister();

        var parameters = new HwndSourceParameters("RekordHotkeyMessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _isRegistered = Win32Interop.RegisterHotKey(
            _hwndSource.Handle,
            HotkeyId,
            Win32Interop.MOD_CONTROL | Win32Interop.MOD_SHIFT | Win32Interop.MOD_NOREPEAT,
            Win32Interop.VK_6
        );
        return _isRegistered;
    }

    public void Unregister()
    {
        if (_hwndSource is not null)
        {
            if (_isRegistered)
            {
                Win32Interop.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
                _isRegistered = false;
            }
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _onTrigger();
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}
