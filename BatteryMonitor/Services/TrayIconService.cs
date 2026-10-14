using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;

namespace BatteryMonitor.Services;

public enum TrayDisplayMode
{
    ChargePercent,
    Watt,
    CapacityMwh
}

public enum BadgeDisplayMode
{
    Off,
    ChargePercent,
    Watt
}

public class TrayIconService : IDisposable
{
    private readonly Hardcodet.Wpf.TaskbarNotification.TaskbarIcon _trayIcon;
    private TrayDisplayMode _displayMode = TrayDisplayMode.ChargePercent;
    private BatteryInfo? _lastInfo;
    private bool _isDarkMode = true;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<BadgeDisplayMode>? BadgeDisplayModeChanged;

    private BadgeDisplayMode _badgeMode = BadgeDisplayMode.ChargePercent;

    public TrayDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            _displayMode = value;
            if (_lastInfo != null) UpdateIcon(_lastInfo);
            UpdateMenuChecks();
        }
    }

    public TrayIconService()
    {
        _trayIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon
        {
            ToolTipText = "Battery Monitor",
            MenuActivation = Hardcodet.Wpf.TaskbarNotification.PopupActivationMode.RightClick
        };

        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke();

        BuildContextMenu();
        UpdateIcon(null);
    }

    private System.Windows.Controls.MenuItem _menuChargePercent = null!;
    private System.Windows.Controls.MenuItem _menuWatt = null!;
    private System.Windows.Controls.MenuItem _menuMwh = null!;
    private System.Windows.Controls.MenuItem _menuBadgeOff = null!;
    private System.Windows.Controls.MenuItem _menuBadgePercent = null!;
    private System.Windows.Controls.MenuItem _menuBadgeWatt = null!;
    private System.Windows.Controls.MenuItem _menuOverlay = null!;
    private System.Windows.Controls.MenuItem _menuCorner = null!;
    private System.Windows.Controls.MenuItem _menuOpen = null!;

    public event Action<bool>? OverlayToggleRequested;
    public event Action<OverlayCorner>? OverlayCornerChanged;

    private static readonly (OverlayCorner Corner, string Key)[] CornerKeys =
    [
        (OverlayCorner.TopLeft, "corner_tl"),
        (OverlayCorner.TopRight, "corner_tr"),
        (OverlayCorner.BottomLeft, "corner_bl"),
        (OverlayCorner.BottomRight, "corner_br"),
    ];

    public void SetOverlayChecked(bool on) => _menuOverlay.IsChecked = on;

    public void SetOverlayCorner(OverlayCorner current)
    {
        foreach (System.Windows.Controls.MenuItem item in _menuCorner.Items)
            item.IsChecked = item.Tag is OverlayCorner c && c == current;
    }
    private System.Windows.Controls.MenuItem _menuExit = null!;

    private void BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        _menuChargePercent = new System.Windows.Controls.MenuItem { Header = Loc.Get("show_percent"), IsChecked = true };
        _menuChargePercent.Click += (_, _) => DisplayMode = TrayDisplayMode.ChargePercent;

        _menuWatt = new System.Windows.Controls.MenuItem { Header = Loc.Get("show_watt") };
        _menuWatt.Click += (_, _) => DisplayMode = TrayDisplayMode.Watt;

        _menuMwh = new System.Windows.Controls.MenuItem { Header = Loc.Get("show_mwh") };
        _menuMwh.Click += (_, _) => DisplayMode = TrayDisplayMode.CapacityMwh;

        menu.Items.Add(_menuChargePercent);
        menu.Items.Add(_menuWatt);
        menu.Items.Add(_menuMwh);
        menu.Items.Add(new System.Windows.Controls.Separator());

        // Badge submenu
        _menuBadgeOff = new System.Windows.Controls.MenuItem { Header = Loc.Get("badge_off") };
        _menuBadgeOff.Click += (_, _) => SetBadgeMode(BadgeDisplayMode.Off);

        _menuBadgePercent = new System.Windows.Controls.MenuItem { Header = Loc.Get("badge_percent"), IsChecked = true };
        _menuBadgePercent.Click += (_, _) => SetBadgeMode(BadgeDisplayMode.ChargePercent);

        _menuBadgeWatt = new System.Windows.Controls.MenuItem { Header = Loc.Get("badge_watt") };
        _menuBadgeWatt.Click += (_, _) => SetBadgeMode(BadgeDisplayMode.Watt);

        menu.Items.Add(_menuBadgeOff);
        menu.Items.Add(_menuBadgePercent);
        menu.Items.Add(_menuBadgeWatt);
        menu.Items.Add(new System.Windows.Controls.Separator());

        // Overlay
        _menuOverlay = new System.Windows.Controls.MenuItem { Header = Loc.Get("overlay_toggle"), IsCheckable = true };
        _menuOverlay.Click += (_, _) => OverlayToggleRequested?.Invoke(_menuOverlay.IsChecked);
        menu.Items.Add(_menuOverlay);

        _menuCorner = new System.Windows.Controls.MenuItem { Header = Loc.Get("overlay_corner") };
        foreach (var (corner, key) in CornerKeys)
        {
            var item = new System.Windows.Controls.MenuItem { Header = Loc.Get(key), Tag = corner };
            var captured = corner;
            item.Click += (_, _) =>
            {
                SetOverlayCorner(captured);
                OverlayCornerChanged?.Invoke(captured);
            };
            _menuCorner.Items.Add(item);
        }
        menu.Items.Add(_menuCorner);
        menu.Items.Add(new System.Windows.Controls.Separator());

        _menuOpen = new System.Windows.Controls.MenuItem { Header = Loc.Get("open") };
        _menuOpen.Click += (_, _) => ShowRequested?.Invoke();
        menu.Items.Add(_menuOpen);

        _menuExit = new System.Windows.Controls.MenuItem { Header = Loc.Get("exit") };
        _menuExit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(_menuExit);

        _trayIcon.ContextMenu = menu;
    }

    public void ApplyLanguage()
    {
        _menuChargePercent.Header = Loc.Get("show_percent");
        _menuWatt.Header          = Loc.Get("show_watt");
        _menuMwh.Header           = Loc.Get("show_mwh");
        _menuBadgeOff.Header      = Loc.Get("badge_off");
        _menuBadgePercent.Header  = Loc.Get("badge_percent");
        _menuBadgeWatt.Header     = Loc.Get("badge_watt");
        _menuOpen.Header          = Loc.Get("open");
        _menuExit.Header          = Loc.Get("exit");
        _menuOverlay.Header       = Loc.Get("overlay_toggle");
        _menuCorner.Header        = Loc.Get("overlay_corner");
        foreach (System.Windows.Controls.MenuItem item in _menuCorner.Items)
            item.Header = Loc.Get(CornerKeys.First(k => k.Corner.Equals(item.Tag)).Key);
        if (_lastInfo != null) UpdateIcon(_lastInfo);
    }

    private void UpdateMenuChecks()
    {
        _menuChargePercent.IsChecked = _displayMode == TrayDisplayMode.ChargePercent;
        _menuWatt.IsChecked = _displayMode == TrayDisplayMode.Watt;
        _menuMwh.IsChecked = _displayMode == TrayDisplayMode.CapacityMwh;
        _menuBadgeOff.IsChecked     = _badgeMode == BadgeDisplayMode.Off;
        _menuBadgePercent.IsChecked = _badgeMode == BadgeDisplayMode.ChargePercent;
        _menuBadgeWatt.IsChecked    = _badgeMode == BadgeDisplayMode.Watt;
    }

    private void SetBadgeMode(BadgeDisplayMode mode)
    {
        _badgeMode = mode;
        UpdateMenuChecks();
        BadgeDisplayModeChanged?.Invoke(mode);
    }

    public void SetDarkMode(bool dark)
    {
        _isDarkMode = dark;
        if (_lastInfo != null) UpdateIcon(_lastInfo);
    }

    public void UpdateIcon(BatteryInfo? info)
    {
        _lastInfo = info;

        string text;
        if (_displayMode == TrayDisplayMode.Watt)
        {
            // Show the active watt value: charge rate when charging, discharge rate otherwise
            if (info == null)
                text = "?W";
            else if (info.Charging && info.ChargeRateWatt is > 0)
                text = $"{info.ChargeRateWatt:F0}";
            else if (!info.Charging && info.DischargeRateWatt > 0)
                text = $"{info.DischargeRateWatt:F0}";
            else
                text = "0";
        }
        else
        {
            text = _displayMode switch
            {
                TrayDisplayMode.ChargePercent => info != null ? $"{info.ChargePercent}" : "?",
                TrayDisplayMode.CapacityMwh => info != null ? $"{info.RemainingCapacityMwh / 1000}k" : "?",
                _ => "?"
            };
        }

        var tooltip = info != null
            ? $"Battery Monitor — {info.ChargePercent}%  ·  " +
              (info.Charging
                  ? $"{Loc.Get("charging_tt")} {info.ChargeRateWatt?.ToString("F1") ?? "…"}W"
                  : $"{Loc.Get("discharging_tt")} {info.DischargeRateWatt:F1}W") +
              $"  ·  {(info.Charging ? Loc.Get("charging") : info.PowerOnline ? Loc.Get("on_power") : Loc.Get("on_battery"))}"
            : "Battery Monitor";

        _trayIcon.ToolTipText = tooltip;
        _trayIcon.Icon = RenderTextIcon(text);
    }

    private System.Drawing.Icon RenderTextIcon(string text)
    {
        const int size = 64; // double size for crispness
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(System.Drawing.Color.Transparent);

        var fontSize = text.Length <= 2 ? 38f : text.Length <= 3 ? 30f : 24f;
        using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        var textColor = _isDarkMode ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(30, 30, 50);
        using var brush = new SolidBrush(textColor);

        var measured = g.MeasureString(text, font);
        var x = (size - measured.Width) / 2f;
        var y = (size - measured.Height) / 2f;
        g.DrawString(text, font, brush, x, y);

        var hIcon = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(hIcon);
    }

    public void SetVisible(bool visible)
    {
        _trayIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
    }
}
