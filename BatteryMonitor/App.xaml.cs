using System.Runtime.InteropServices;
using System.Windows;
using BatteryMonitor.Services;

namespace BatteryMonitor;

public partial class App : Application
{
    private const string MutexName = @"Local\BatteryMonitor.SingleInstance";
    private const string ShowEventName = @"Local\BatteryMonitor.ShowWindow";
    private const string ExitEventName = @"Local\BatteryMonitor.Exit";

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--exit" asks a running copy to quit cleanly (the installer uses this before upgrading or removing the app).
        var wantsExit = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));

        // Already running? Ask that instance to show its window (or to exit), then quit.
        _instanceMutex = new Mutex(true, MutexName, out var isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        if (!isFirst)
        {
            if (wantsExit) _exitEvent.Set();
            else
            {
                AllowSetForegroundWindow(-1);   // ASFW_ANY: let the running instance take focus
                _showEvent.Set();
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        if (wantsExit) { Shutdown(); return; }   // nothing was running

        // --overlay / --window skip the prompt (handy for startup shortcuts).
        bool? overlay = null;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--overlay", StringComparison.OrdinalIgnoreCase)) overlay = true;
            else if (arg.Equals("--window", StringComparison.OrdinalIgnoreCase)) overlay = false;
        }

        if (overlay == null)
        {
            var settings = AppSettings.Load();
            Loc.SetLanguage(string.IsNullOrEmpty(settings.Language)
                ? Loc.DetectSystemLanguage()
                : settings.Language);

            var choice = new StartupChoiceWindow();
            choice.ShowDialog();
            if (choice.ChoseOverlay == null)
            {
                Shutdown();
                return;
            }
            overlay = choice.ChoseOverlay;
        }

        var main = new MainWindow();
        MainWindow = main;
        if (overlay == true)
            main.StartInOverlayMode();
        else
            main.Show();

        // Later launches signal these events: bring the window up, or quit cleanly.
        var waiter = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showEvent, _exitEvent };
            while (true)
            {
                if (WaitHandle.WaitAny(handles) == 0) Dispatcher.BeginInvoke(main.BringToFront);
                else { Dispatcher.BeginInvoke(main.ExitFromOutside); break; }
            }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        waiter.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
