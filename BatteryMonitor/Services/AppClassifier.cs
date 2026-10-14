using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryMonitor.Services;

/// <summary>
/// Decides whether a process is something the user would recognise as an app, and what to call it.
/// An app is a process with a visible window, or one that shares its executable with such a process
/// (Chrome's many helper processes), living outside the Windows folder. Everything else is
/// "Windows &amp; background" and the caller lumps it together.
/// </summary>
public sealed class AppClassifier
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Shell and system hosts that show windows or UI on the user's behalf but aren't "apps".
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "applicationframehost", "textinputhost", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "searchapp", "lockapp", "dwm", "sihost", "ctfmon", "runtimebroker", "widgets",
        "widgetservice", "systemsettingsbroker", "backgroundtaskhost", "taskhostw", "csrss", "winlogon",
        "fontdrvhost", "conhost", "dllhost", "wmiprvse", "svchost",
    };

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private readonly Dictionary<int, (DateTime At, string? Name)> _cache = new();
    private readonly Dictionary<int, string?> _paths = new();
    private HashSet<int> _windowPids = new();
    private HashSet<string> _windowPaths = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _windowsAt = DateTime.MinValue;

    /// <summary>The friendly app name, or null if this process is Windows or background activity.</summary>
    public string? Classify(int pid)
    {
        var now = DateTime.UtcNow;
        RefreshWindows(now);

        if (_cache.TryGetValue(pid, out var c) && now - c.At < TimeSpan.FromSeconds(60)) return c.Name;

        var name = Resolve(pid);
        if (_cache.Count > 2000) _cache.Clear();
        _cache[pid] = (now, name);
        return name;
    }

    private string? Resolve(int pid)
    {
        var path = ImagePath(pid);
        var exe = path == null ? null : System.IO.Path.GetFileNameWithoutExtension(path);
        if (exe != null && NotApps.Contains(exe)) return null;

        var windowed = _windowPids.Contains(pid) || (path != null && _windowPaths.Contains(path));
        if (!windowed) return null;

        // Background tools outside the Windows folder only count once they own a window (see above);
        // anything inside the Windows folder needs a window of its own to count.
        if (path != null && path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase) && !_windowPids.Contains(pid))
            return null;

        return FriendlyName(path, pid);
    }

    private static string FriendlyName(string? path, int pid)
    {
        try
        {
            if (path != null)
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                foreach (var candidate in new[] { v.ProductName, v.FileDescription })
                {
                    var s = candidate?.Trim();
                    if (!string.IsNullOrEmpty(s) && !s.Contains("Operating System", StringComparison.OrdinalIgnoreCase)
                        && s.Length <= 40)
                        return s;
                }
                return Capitalise(System.IO.Path.GetFileNameWithoutExtension(path));
            }
        }
        catch { }
        try { return Capitalise(Process.GetProcessById(pid).ProcessName); } catch { return $"pid {pid}"; }
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private string? ImagePath(int pid)
    {
        if (_paths.TryGetValue(pid, out var cached)) return cached;
        string? path = null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);   // works for elevated processes too
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024); var size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally { CloseHandle(h); }
        }
        if (_paths.Count > 2000) _paths.Clear();
        return _paths[pid] = path;
    }

    // Which processes own a normal, visible, un-cloaked top-level window right now.
    private void RefreshWindows(DateTime now)
    {
        if (now - _windowsAt < TimeSpan.FromSeconds(15)) return;
        _windowsAt = now;

        var pids = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0 || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            // Cloaked by an app or its host (suspended UWP apps) isn't a real window; cloaked by the shell
            // (bit 2) just means it's on another virtual desktop, which still counts.
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0 && (cloaked & 2) == 0)
                return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid != 0) pids.Add((int)pid);
            return true;
        }, IntPtr.Zero);

        _windowPids = pids;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in pids)
            if (ImagePath(pid) is { } p && !p.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) paths.Add(p);
        _windowPaths = paths;
        _cache.Clear();      // classifications depend on the window set
    }
}
