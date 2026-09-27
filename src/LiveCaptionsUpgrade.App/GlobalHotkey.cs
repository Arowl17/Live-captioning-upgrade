using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>A system-wide keyboard shortcut that works whichever app has focus.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4C43;

    private readonly HwndSource _source;
    private bool _registered;

    /// <param name="owner">Window that receives the hotkey messages. It keeps working while the window is hidden.</param>
    public GlobalHotkey(Window owner)
    {
        IntPtr handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Window has no HwndSource.");
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    /// <summary>Replaces the current shortcut. Returns false if it is invalid or another app already uses it.</summary>
    public bool Register(Hotkey? hotkey)
    {
        Unregister();
        if (hotkey is null || !hotkey.IsValid || !Enum.TryParse(hotkey.Key, ignoreCase: true, out Key key))
        {
            return hotkey is null;
        }

        uint modifiers = (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            // Not a real key (e.g. "None" typed into the settings file).
            return false;
        }

        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }
}
