using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BatteryMonitor.Services;

/// <summary>Global hotkey (works while other apps have focus) via a message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_O = 0x4F;
    private const int HotkeyId = 0xB477;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;

    public const string Description = "Ctrl+Alt+O";
    public bool Registered { get; }
    public event Action? Pressed;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("BatteryMonitorHotkey") { ParentWindow = HWND_MESSAGE });
        _source.AddHook(WndProc);
        Registered = RegisterHotKey(_source.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_O);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_source.Handle, HotkeyId);
        _source.Dispose();
    }
}
