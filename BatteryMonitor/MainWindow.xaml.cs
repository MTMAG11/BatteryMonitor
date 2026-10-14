using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatteryMonitor.Services;

namespace BatteryMonitor;

public partial class MainWindow : Window
{
    private readonly BatteryService _batteryService;
    private readonly TrayIconService _trayService;
    private bool _isDarkMode = true;
    private BadgeDisplayMode _badgeMode = BadgeDisplayMode.ChargePercent;
    private BatteryInfo? _lastBatteryInfo;

    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush AccentBlue = new(Color.FromRgb(0x4A, 0x9E, 0xFF));

    // Dark theme — bright whites
    private static readonly SolidColorBrush DarkBg = new(Color.FromRgb(0x0D, 0x0F, 0x14));
    private static readonly SolidColorBrush DarkCardBg = new(Color.FromRgb(0x14, 0x17, 0x20));
    private static readonly SolidColorBrush DarkCardBorder = new(Color.FromRgb(0x1E, 0x23, 0x30));
    private static readonly SolidColorBrush DarkTextPrimary = new(Color.FromRgb(0xFF, 0xFF, 0xFF)); // pure white
    private static readonly SolidColorBrush DarkTextMuted = new(Color.FromRgb(0xB0, 0xBC, 0xDA));  // much brighter muted
    private static readonly SolidColorBrush DarkTextDim = new(Color.FromRgb(0x60, 0x6E, 0x90));    // brighter dim

    // Light theme — pure black text
    private static readonly SolidColorBrush LightBg = new(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly SolidColorBrush LightCardBg = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LightCardBorder = new(Color.FromRgb(0xD8, 0xDB, 0xE6));
    private static readonly SolidColorBrush LightTextPrimary = new(Color.FromRgb(0x0A, 0x0A, 0x0A)); // near-black
    private static readonly SolidColorBrush LightTextMuted = new(Color.FromRgb(0x4A, 0x55, 0x68));   // dark grey
    private static readonly SolidColorBrush LightTextDim = new(Color.FromRgb(0x90, 0x9A, 0xB0));
    private static readonly SolidColorBrush LightAccentBlue = new(Color.FromRgb(0x1D, 0x4E, 0xD8));

    static MainWindow()
    {
        Green.Freeze(); Amber.Freeze(); Red.Freeze(); AccentBlue.Freeze();
        DarkBg.Freeze(); DarkCardBg.Freeze(); DarkCardBorder.Freeze();
        DarkTextPrimary.Freeze(); DarkTextMuted.Freeze(); DarkTextDim.Freeze();
        LightBg.Freeze(); LightCardBg.Freeze(); LightCardBorder.Freeze();
        LightTextPrimary.Freeze(); LightTextMuted.Freeze(); LightTextDim.Freeze();
        LightAccentBlue.Freeze();
    }

    private readonly AppSettings _settings;
    private OverlayWindow? _overlay;
    private OverlayCorner _overlayCorner;
    private readonly HotkeyService _hotkey;

    /// <summary>Launch straight into the click-through overlay with the main window hidden.</summary>
    public void StartInOverlayMode()
    {
        _trayService.SetVisible(true);
        SetOverlay(true);
    }

    private void SetOverlay(bool on, bool swapWindows = true)
    {
        _trayService.SetOverlayChecked(on);
        if (on)
        {
            if (_overlay == null)
            {
                _overlay = new OverlayWindow(_overlayCorner, _settings);
                if (_lastBatteryInfo != null) _overlay.UpdateInfo(_lastBatteryInfo);
                _overlay.Show();
            }
            if (swapWindows && IsVisible) HideToTray();
        }
        else
        {
            _overlay?.Close();
            _overlay = null;
            if (swapWindows && !IsVisible) ShowFromTray();
        }
    }

    private void ResetModel_Click(object sender, RoutedEventArgs e)
    {
        _batteryService.ResetLoadModel();
        UpdateModelStatus();
    }

    private void UpdateModelStatus()
    {
        var (quality, error, samples) = _batteryService.LoadModelStatus;
        ModelStatusText.Text = samples < 1
            ? $"{Loc.Get("model_status")}: {Loc.Get("model_none")}"
            : $"{Loc.Get("model_status")}: {Loc.Get("q_" + quality)}"
              + (double.IsNaN(error) ? "" : $", ±{error:F1} W")
              + $"  ·  {samples:F0} samples";
    }

    private void AdapterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = AdapterBox.Text.Trim().Replace(',', '.');
        var ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w is > 0 and <= 500;
        _settings.AdapterWatts = ok ? w : 0;
        _settings.Save();
        if (_lastBatteryInfo != null) UpdateUI(_lastBatteryInfo);
    }

    private void OverlayOption_Click(object sender, RoutedEventArgs e)
    {
        _settings.OverlayShowPercent = OvPercentCb.IsChecked == true;
        _settings.OverlayShowWatt = OvWattCb.IsChecked == true;
        _settings.OverlayShowTime = OvTimeCb.IsChecked == true;
        _settings.OverlayShowVoltage = OvVoltageCb.IsChecked == true;
        _settings.OverlayShowHealth = OvHealthCb.IsChecked == true;
        _settings.OverlayShowSystem = OvSystemCb.IsChecked == true;
        _settings.OverlayShowChargeCircle = OvChargeCircleCb.IsChecked == true;
        _settings.OverlayShowHealthCircle = OvHealthCircleCb.IsChecked == true;
        _settings.Save();
        _overlay?.Refresh();
    }

    private void LoadOverlayOptions()
    {
        OvPercentCb.IsChecked = _settings.OverlayShowPercent;
        OvWattCb.IsChecked = _settings.OverlayShowWatt;
        OvTimeCb.IsChecked = _settings.OverlayShowTime;
        OvVoltageCb.IsChecked = _settings.OverlayShowVoltage;
        OvHealthCb.IsChecked = _settings.OverlayShowHealth;
        OvSystemCb.IsChecked = _settings.OverlayShowSystem;
        AdapterBox.Text = _settings.AdapterWatts > 0 ? _settings.AdapterWatts.ToString("0.##") : "";
        OvChargeCircleCb.IsChecked = _settings.OverlayShowChargeCircle;
        OvHealthCircleCb.IsChecked = _settings.OverlayShowHealthCircle;
    }

    private void OverlayToggle_Click(object sender, RoutedEventArgs e) => SetOverlay(_overlay == null);

    public MainWindow()
    {
        InitializeComponent();

        // Load settings and apply language before building UI
        _settings = AppSettings.Load();
        var lang = string.IsNullOrEmpty(_settings.Language)
            ? Loc.DetectSystemLanguage()
            : _settings.Language;
        Loc.SetLanguage(lang);

        _trayService = new TrayIconService();
        _trayService.ShowRequested += () => Dispatcher.Invoke(ShowFromTray);
        _trayService.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        _trayService.BadgeDisplayModeChanged += mode => Dispatcher.Invoke(() =>
        {
            _badgeMode = mode;
            if (_lastBatteryInfo != null) UpdateBadge(_lastBatteryInfo);
        });

        _overlayCorner = Enum.TryParse<OverlayCorner>(_settings.OverlayCorner, out var corner)
            ? corner : OverlayCorner.TopRight;
        _trayService.SetOverlayCorner(_overlayCorner);
        _trayService.OverlayToggleRequested += on => Dispatcher.Invoke(() => SetOverlay(on));
        _trayService.OverlayCornerChanged += c => Dispatcher.Invoke(() =>
        {
            _overlayCorner = c;
            _overlay?.SetCorner(c);
            _settings.OverlayCorner = c.ToString();
            _settings.Save();
        });

        LoadOverlayOptions();
        _hotkey = new HotkeyService();
        _hotkey.Pressed += () => Dispatcher.Invoke(() => SetOverlay(_overlay == null, swapWindows: false));

        _batteryService = new BatteryService();
        _batteryService.BatteryUpdated += info => Dispatcher.BeginInvoke(() => UpdateUI(info));
        _batteryService.ErrorChanged += err => Dispatcher.BeginInvoke(() => ErrorText.Text = err ?? "");
        _batteryService.Start();

        // Closing the window hides it to the tray, except when the app is really quitting
        // (Exit menu, Windows shutting down, or the installer asking it to close).
        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            HideToTray();
        };
        Application.Current.SessionEnding += (_, _) => _exiting = true;

        ApplyTheme();
        ApplyLocalization();
    }

    private SolidColorBrush Bg => _isDarkMode ? DarkBg : LightBg;
    private SolidColorBrush CardBg => _isDarkMode ? DarkCardBg : LightCardBg;
    private SolidColorBrush CardBorder => _isDarkMode ? DarkCardBorder : LightCardBorder;
    private SolidColorBrush TextPrimary => _isDarkMode ? DarkTextPrimary : LightTextPrimary;
    private SolidColorBrush TextMuted => _isDarkMode ? DarkTextMuted : LightTextMuted;
    private SolidColorBrush TextDim => _isDarkMode ? DarkTextDim : LightTextDim;
    private SolidColorBrush Accent => _isDarkMode ? AccentBlue : LightAccentBlue;

    private void ApplyTheme()
    {
        RootWindow.Background = Bg;
        RootBorder.BorderBrush = CardBorder;
        TitleBarGrid.Background = Bg;
        RootGrid.Background = Bg;

        // Title bar
        TitleIcon.Foreground = Amber;
        TitleText.Foreground = TextPrimary;
        StatusText.Foreground = TextMuted;
        ThemeToggleBtn.Content = _isDarkMode ? "\u2600" : "\u263E"; // sun / moon
        ThemeToggleBtn.Foreground = TextMuted;
        MinimizeBtn.Foreground = TextMuted;
        OverlayBtn.Foreground = TextMuted;
        CloseBtn.Foreground = TextPrimary;

        // Device subline
        DeviceSubline.Foreground = TextDim;

        // Watt box
        WattBoxBorder.Background = CardBg;
        WattBoxBorder.BorderBrush = CardBorder;
        WattBoxLabel.Foreground = Green;
        ChargeRateText.Foreground = Green;
        WattSep.Fill = CardBorder;
        DischargeLabel.Foreground = Amber;
        DischargeRateText.Foreground = Amber;

        // Estimate card
        EstimateCard.Background = CardBg;
        EstimateCard.BorderBrush = CardBorder;
        EstimateTitle.Foreground = Accent;
        EstimateSep.Fill = CardBorder;
        EstimateLabel.Foreground = TextDim;
        EstimateTimeText.Foreground = TextPrimary;
        EstimateUnitText.Foreground = TextMuted;
        VoltageLabel.Foreground = TextDim;
        VoltageText.Foreground = TextPrimary;
        VoltUnitText.Foreground = TextMuted;
        PowerLabel.Foreground = TextDim;
        PowerText.Foreground = TextPrimary;
        PowerUnitText.Foreground = TextMuted;

        // Capacity card
        CapacityCard.Background = CardBg;
        CapacityCard.BorderBrush = CardBorder;
        CapTitle.Foreground = Accent;
        CapSep.Fill = CardBorder;
        DesignCapLabel.Foreground = TextMuted;
        DesignCapText.Foreground = TextPrimary;
        FullCapLabel.Foreground = TextMuted;
        FullCapText.Foreground = TextPrimary;
        RemCapLabel.Foreground = TextMuted;
        BarBg.Fill = CardBorder;

        // Device card
        DeviceCard.Background = CardBg;
        DeviceCard.BorderBrush = CardBorder;
        DeviceTitle.Foreground = Accent;
        DeviceSep.Fill = CardBorder;
        ModelLabel.Foreground = TextMuted;
        ModelText.Foreground = TextPrimary;
        MfgLabel.Foreground = TextMuted;
        ManufacturerText.Foreground = TextPrimary;
        SnLabel.Foreground = TextMuted;
        SerialText.Foreground = TextPrimary;
        CyclesLabel.Foreground = TextMuted;
        CyclesText.Foreground = TextPrimary;

        // Adapter card
        AdapterCard.Background = CardBg;
        AdapterCard.BorderBrush = CardBorder;
        AdapterTitle.Foreground = Accent;
        AdapterSep.Fill = CardBorder;
        AdapterLabel.Foreground = TextPrimary;
        AdapterUnit.Foreground = TextMuted;
        AdapterHint.Foreground = TextDim;
        ModelStatusText.Foreground = TextPrimary;
        AdapterBox.Background = Bg;
        AdapterBox.Foreground = TextPrimary;
        AdapterBox.BorderBrush = CardBorder;
        AdapterBox.CaretBrush = TextPrimary;

        // Overlay card
        OverlayCard.Background = CardBg;
        OverlayCard.BorderBrush = CardBorder;
        OverlayTitle.Foreground = Accent;
        OverlaySep.Fill = CardBorder;
        foreach (var cb in new[] { OvPercentCb, OvWattCb, OvTimeCb, OvVoltageCb, OvHealthCb, OvSystemCb, OvChargeCircleCb, OvHealthCircleCb })
            cb.Foreground = TextPrimary;
        OverlayHotkeyText.Foreground = TextMuted;

        // Footer
        ErrorText.Foreground = Red;
        UpdatedText.Foreground = TextDim;

        // Gauge colors update
        ChargeGauge.UpdateTheme(TextPrimary, TextMuted, Accent, CardBorder);
        HealthGauge.UpdateTheme(TextPrimary, TextMuted, Accent, CardBorder);
    }

    private void ApplyLocalization()
    {
        LangBtn.Content = Loc.Current.ToUpperInvariant();
        LangBtn.Foreground = TextMuted;

        ChargeGauge.Label  = Loc.Get("charge_level");
        HealthGauge.Label  = Loc.Get("health");

        WattBoxLabel.Text  = Loc.Get("charge_rate");
        DischargeLabel.Text = Loc.Get("discharge");

        EstimateTitle.Text = Loc.Get("forecast");
        VoltageLabel.Text  = Loc.Get("voltage");
        VoltUnitText.Text  = Loc.Get("volt");
        PowerUnitText.Text = Loc.Get("watt");

        CapTitle.Text      = Loc.Get("capacity");
        DesignCapLabel.Text = Loc.Get("design_cap");
        FullCapLabel.Text  = Loc.Get("full_cap");
        RemCapLabel.Text   = Loc.Get("remaining");

        OverlayTitle.Text  = Loc.Get("overlay_card");
        OvPercentCb.Content = Loc.Get("ov_percent");
        OvWattCb.Content   = Loc.Get("ov_watt");
        OvTimeCb.Content   = Loc.Get("ov_time");
        OvVoltageCb.Content = Loc.Get("ov_voltage");
        OvHealthCb.Content = Loc.Get("ov_health");
        OvSystemCb.Content = Loc.Get("ov_system");
        AdapterTitle.Text  = Loc.Get("adapter_card");
        AdapterLabel.Text  = Loc.Get("adapter_label");
        AdapterHint.Text   = Loc.Get("adapter_hint");
        ResetModelBtn.Content = Loc.Get("model_reset");
        OvChargeCircleCb.Content = Loc.Get("ov_charge_circle");
        OvHealthCircleCb.Content = Loc.Get("ov_health_circle");
        OverlayHotkeyText.Text = _hotkey.Registered
            ? $"{Loc.Get("overlay_hotkey")} {HotkeyService.Description}"
            : Loc.Get("overlay_hotkey_fail");

        DeviceTitle.Text   = Loc.Get("device");
        ModelLabel.Text    = Loc.Get("model");
        MfgLabel.Text      = Loc.Get("manufacturer");
        SnLabel.Text       = Loc.Get("serial");
        CyclesLabel.Text   = Loc.Get("cycles");

        _trayService.ApplyLanguage();

        if (_lastBatteryInfo != null) UpdateUI(_lastBatteryInfo);
    }

    private void LangBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = LangBtn };
        foreach (var (code, name) in Loc.Languages)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = name,
                IsChecked = code == Loc.Current,
            };
            var captured = code;
            item.Click += (_, _) =>
            {
                Loc.SetLanguage(captured);
                _settings.Language = captured;
                _settings.Save();
                ApplyLocalization();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        _trayService.SetDarkMode(_isDarkMode);
    }

    private void UpdateUI(BatteryInfo info)
    {
        // Status dot + text
        if (info.Charging)
        {
            StatusDot.Fill = Green;
            StatusText.Text = Loc.Get("charging");
        }
        else if (info.PowerOnline)
        {
            StatusDot.Fill = Amber;
            StatusText.Text = Loc.Get("on_power");
        }
        else
        {
            StatusDot.Fill = Red;
            StatusText.Text = Loc.Get("on_battery");
        }

        // Device subline
        DeviceSubline.Text = $"{info.DeviceName}  \u00B7  {info.Manufacturer}  \u00B7  SN {info.SerialNumber}";

        // Gauges
        var chargeColor = info.ChargePercent >= 50 ? Green : info.ChargePercent >= 20 ? Amber : Red;
        ChargeGauge.Value = info.ChargePercent;
        ChargeGauge.GaugeColor = chargeColor;

        var healthColor = info.HealthPercent >= 85 ? Green : info.HealthPercent >= 65 ? Amber : Red;
        HealthGauge.Value = info.HealthPercent;
        HealthGauge.GaugeColor = healthColor;
        HealthGauge.ToolTip = info.SmoothedFullChargeMwh != null
            ? string.Format(Loc.Get("health_tip"), info.HealthHistoryDays, info.HealthRawPercent)
              + (info.HealthTrendPercentPer30Days is { } tr ? string.Format(Loc.Get("health_trend"), tr) : "")
            : null;

        // Watt box
        ChargeRateText.Text = info.ChargeRateWatt.HasValue ? $"{info.ChargeRateWatt.Value:F1} W" : "\u2026";
        // Plugged in with a known adapter rating: show the estimated system load instead.
        var sys = PowerSplit.ForSystem(info, _settings.AdapterWatts);
        if (sys is { } s)
        {
            DischargeLabel.Text = Loc.Get("system_est");
            DischargeRateText.Text = $"{s.Prefix} {s.Watts:F0} W";
            DischargeRateText.ToolTip = s.Note;
        }
        else
        {
            DischargeLabel.Text = Loc.Get("discharge");
            DischargeRateText.Text = $"{info.DischargeRateWatt:F1} W";
            DischargeRateText.ToolTip = null;
        }
        UpdateModelStatus();

        // Estimates card
        if (info.Charging)
        {
            EstimateLabel.Text = Loc.Get("full_in");
            EstimateTimeText.ToolTip = string.Format(Loc.Get("charge_tip"), info.ChargeCurveKnownPercent);
            var ttf = info.EstimatedTimeToFullMinutes;
            if (ttf.HasValue && ttf.Value > 0)
            {
                var h = ttf.Value / 60;
                var m = ttf.Value % 60;
                EstimateTimeText.Text = h > 0 ? $"{h}:{m:D2}" : $"{m}";
                EstimateUnitText.Text = h > 0 ? Loc.Get("hours_min") : Loc.Get("minutes");
            }
            else
            {
                EstimateTimeText.Text = "\u2026";
                EstimateUnitText.Text = "";
            }
            PowerLabel.Text = Loc.Get("charge_rate");
            PowerText.Text = info.ChargeRateWatt?.ToString("F1") ?? "\u2026";
        }
        else
        {
            EstimateLabel.Text = Loc.Get("runtime");
            var rt = info.EstimatedRuntimeMinutes;
            if (rt.HasValue && rt.Value > 0)
            {
                var h = rt.Value / 60;
                var m = rt.Value % 60;
                EstimateTimeText.Text = h > 0 ? $"{h}:{m:D2}" : $"{m}";
                EstimateUnitText.Text = h > 0 ? Loc.Get("hours_min") : Loc.Get("minutes");
            }
            else
            {
                EstimateTimeText.Text = "\u2026";
                EstimateUnitText.Text = "";
            }
            EstimateTimeText.ToolTip = info.RuntimeErrorFraction is { } er && info.RuntimeReservePercent is { } rp
                ? string.Format(Loc.Get("runtime_tip"), rp, er, info.RuntimeSelfChecks)
                : null;
            PowerLabel.Text = Loc.Get("consumption");
            PowerText.Text = info.DischargeRateWatt > 0 ? info.DischargeRateWatt.ToString("F1") : "\u2026";
        }

        // Voltage
        VoltageText.Text = info.VoltageMv > 0 ? (info.VoltageMv / 1000.0).ToString("F2") : "\u2026";

        // Capacity card
        DesignCapText.Text = FormatMwh(info.DesignedCapacityMwh);
        FullCapText.Text = FormatMwh(info.FullChargedCapacityMwh);
        RemainingCapText.Text = FormatMwh(info.RemainingCapacityMwh);
        RemainingCapText.Foreground = chargeColor;

        // Progress bar
        var barPct = info.FullChargedCapacityMwh > 0
            ? Math.Clamp(info.RemainingCapacityMwh / (double)info.FullChargedCapacityMwh, 0, 1)
            : 0;
        ChargeBar.Width = ChargeBar.Parent is Grid grid
            ? grid.ActualWidth * barPct
            : 0;
        ChargeBar.Fill = chargeColor;

        // Device card
        ModelText.Text = info.DeviceName;
        ManufacturerText.Text = info.Manufacturer;
        SerialText.Text = info.SerialNumber;
        CyclesText.Text = info.CycleCount?.ToString() ?? "—";

        // Footer
        UpdatedText.Text = $"{Loc.Get("updated")} {DateTime.Now:HH:mm:ss}";

        // Taskbar overlay badge
        _lastBatteryInfo = info;
        UpdateBadge(info);
        _overlay?.UpdateInfo(info);

        // Tray
        _trayService.UpdateIcon(info);
    }

    private void UpdateBadge(BatteryInfo info)
    {
        if (_badgeMode == BadgeDisplayMode.Off)
        {
            TaskbarInfo.Overlay = null;
            return;
        }

        string text;
        if (_badgeMode == BadgeDisplayMode.Watt)
        {
            var w = info.Charging ? info.ChargeRateWatt : info.DischargeRateWatt;
            text = w.HasValue && w.Value > 0 ? $"{w.Value:F0}W" : "…";
        }
        else
        {
            text = $"{info.ChargePercent}";
        }

        var bg = info.Charging
            ? Color.FromRgb(0x4A, 0x9E, 0xFF)
            : info.ChargePercent >= 50
                ? Color.FromRgb(0x30, 0xD1, 0x58)
                : info.ChargePercent >= 20
                    ? Color.FromRgb(0xFF, 0x9F, 0x0A)
                    : Color.FromRgb(0xFF, 0x45, 0x3A);

        TaskbarInfo.Overlay = RenderBadge(text, bg, Colors.White);
    }

    private static string FormatMwh(int mwh) => $"{mwh:N0} mWh  ({mwh / 1000.0:F1} Wh)";

    private static ImageSource RenderBadge(string text, Color bg, Color fg)
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(bg), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                text.Length <= 2 ? 16 : 12,
                new SolidColorBrush(fg),
                96);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void Close_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void HideToTray()
    {
        Hide();
        _trayService.SetVisible(true);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Show the main window in front, e.g. when the exe is launched a second time.</summary>
    public void BringToFront()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;      // nudge above other windows, then release
        Topmost = false;
        Activate();
    }

    private bool _exiting;

    /// <summary>Quit cleanly when asked by another process (the installer's <c>--exit</c>).</summary>
    public void ExitFromOutside() => ExitApp();

    private void ExitApp()
    {
        _exiting = true;
        _hotkey.Dispose();
        _trayService.Dispose();
        _batteryService.Dispose();
        Application.Current.Shutdown();
    }
}

