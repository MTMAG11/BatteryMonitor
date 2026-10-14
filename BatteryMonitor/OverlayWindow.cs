using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BatteryMonitor.Services;

namespace BatteryMonitor;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Small always-on-top, click-through readout (like an FPS counter).
/// The mouse passes straight through it to whatever is underneath.
/// </summary>
public class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const double ScreenGap = 12;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static readonly SolidColorBrush Green = Frozen(0x30, 0xD1, 0x58);
    private static readonly SolidColorBrush Amber = Frozen(0xFF, 0x9F, 0x0A);
    private static readonly SolidColorBrush Red = Frozen(0xFF, 0x45, 0x3A);
    private static readonly SolidColorBrush Blue = Frozen(0x4A, 0x9E, 0xFF);
    private static readonly SolidColorBrush White = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush Muted = Frozen(0xB0, 0xBC, 0xDA);

    private readonly TextBlock _text = new()
    {
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = White,
    };

    private readonly StackPanel _circles = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
    private OverlayCorner _corner;
    private readonly AppSettings _settings;
    private BatteryInfo? _last;

    public OverlayWindow(OverlayCorner corner, AppSettings settings)
    {
        _corner = corner;
        _settings = settings;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x0D, 0x0F, 0x14)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Child = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _circles, _text },
            },
        };
        _text.VerticalAlignment = VerticalAlignment.Center;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => Reposition();
    }

    public void SetCorner(OverlayCorner corner)
    {
        _corner = corner;
        Reposition();
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = _corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.Left + ScreenGap
            : area.Right - ActualWidth - ScreenGap;
        Top = _corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Top + ScreenGap
            : area.Bottom - ActualHeight - ScreenGap;
    }

    /// <summary>Re-render with the latest data, e.g. after the visible items changed.</summary>
    public void Refresh()
    {
        if (_last != null) UpdateInfo(_last);
    }

    public void UpdateInfo(BatteryInfo info)
    {
        _last = info;
        var pctColor = info.Charging ? Blue
            : info.ChargePercent >= 50 ? Green
            : info.ChargePercent >= 20 ? Amber
            : Red;

        var watt = info.Charging ? info.ChargeRateWatt : info.DischargeRateWatt;
        var minutes = info.Charging ? info.EstimatedTimeToFullMinutes : info.EstimatedRuntimeMinutes;

        var parts = new List<System.Windows.Documents.Run>();
        if (_settings.OverlayShowPercent)
            parts.Add(new((info.Charging ? "⚡ " : "") + $"{info.ChargePercent}%")
                { Foreground = pctColor, FontWeight = FontWeights.Bold });
        if (_settings.OverlayShowWatt)
            // + into the battery, − out of it
            parts.Add(new(watt is > 0 ? $"{(info.Charging ? "+" : "−")}{watt.Value:F1} W" : "… W")
                { Foreground = White });
        // Only while plugged in: on battery the watt reading above already is the system draw.
        if (_settings.OverlayShowSystem && info.PowerOnline && PowerSplit.ForSystem(info, _settings.AdapterWatts) is { } sys)
            parts.Add(new($"sys {sys.Prefix}{sys.Watts:F0} W") { Foreground = Muted });
        if (_settings.OverlayShowTime && minutes is > 0)
            parts.Add(new($"{minutes.Value / 60}:{minutes.Value % 60:D2}") { Foreground = Muted });
        if (_settings.OverlayShowVoltage && info.VoltageMv > 0)
            parts.Add(new($"{info.VoltageMv / 1000.0:F2} V") { Foreground = Muted });
        if (_settings.OverlayShowHealth)
            parts.Add(new($"{info.HealthPercent:F0}% health") { Foreground = Muted });
        if (parts.Count == 0)
            parts.Add(new("—") { Foreground = Muted });

        _circles.Children.Clear();
        if (_settings.OverlayShowChargeCircle)
            _circles.Children.Add(MakeCircle(info.ChargePercent, pctColor));
        if (_settings.OverlayShowHealthCircle)
        {
            var healthColor = info.HealthPercent >= 85 ? Green : info.HealthPercent >= 65 ? Amber : Red;
            _circles.Children.Add(MakeCircle(info.HealthPercent, healthColor));
        }
        _text.Visibility = _settings.OverlayShowPercent || _settings.OverlayShowWatt || _settings.OverlayShowTime
            || _settings.OverlayShowVoltage || _settings.OverlayShowHealth || _circles.Children.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        _text.Inlines.Clear();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) _text.Inlines.Add(new System.Windows.Documents.Run("   "));
            _text.Inlines.Add(parts[i]);
        }

        // Keep above other topmost windows that may have appeared since.
        Topmost = false;
        Topmost = true;
    }

    /// <summary>Small ring gauge with the value in the middle.</summary>
    private static FrameworkElement MakeCircle(double percent, Brush color)
    {
        const double size = 34, thickness = 4, r = (size - thickness) / 2, c = size / 2;
        var grid = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, 8, 0) };

        grid.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = thickness,
            Margin = new Thickness(thickness / 2),
        });

        var pct = Math.Clamp(percent, 0, 99.9);
        if (pct > 0.5)
        {
            var end = (-90 + pct / 100 * 360) * Math.PI / 180;
            var figure = new PathFigure { StartPoint = new Point(c, c - r) };
            figure.Segments.Add(new ArcSegment(
                new Point(c + r * Math.Cos(end), c + r * Math.Sin(end)),
                new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true));
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry { Figures = { figure } },
                Stroke = color,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        grid.Children.Add(new TextBlock
        {
            Text = $"{percent:F0}",
            Foreground = White,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

