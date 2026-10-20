using System.Windows.Interop;
using SaymonTools.App.Native;

namespace SaymonTools.App.Shell;

/// <summary>A message-only window that receives the global hotkey.</summary>
internal sealed class HotkeyListener : IDisposable
{
    private const int Id = 0x4641; // "FA"
    private readonly HwndSource _source;
    private readonly Action _pressed;
    private bool _registered;

    public HotkeyListener(Action pressed)
    {
        _pressed = pressed;
        _source = new HwndSource(new HwndSourceParameters("SaymonToolsHotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers the preset; returns false if another app already owns that combination.</summary>
    public bool Register(HotkeyPreset preset)
    {
        Unregister();
        var (mods, vk) = preset switch
        {
            HotkeyPreset.CtrlAltSpace => (Win32.MOD_CONTROL | Win32.MOD_ALT, 0x20u),
            HotkeyPreset.CtrlShiftSpace => (Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x20u),
            HotkeyPreset.CtrlAltF => (Win32.MOD_CONTROL | Win32.MOD_ALT, 0x46u),
            HotkeyPreset.CtrlAltW => (Win32.MOD_CONTROL | Win32.MOD_ALT, 0x57u),
            _ => (0u, 0u),
        };
        if (vk == 0)
            return true;
        _registered = Win32.RegisterHotKey(_source.Handle, Id, mods | Win32.MOD_NOREPEAT, vk);
        return _registered;
    }

    private void Unregister()
    {
        if (_registered)
            Win32.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
