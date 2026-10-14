using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatteryMonitor.Services;

namespace BatteryMonitor;

/// <summary>Shown at launch: pick the normal window or the click-through overlay.</summary>
public class StartupChoiceWindow : Window
{
    /// <summary>True if the user picked overlay mode; null if the dialog was dismissed.</summary>
    public bool? ChoseOverlay { get; private set; }

    public StartupChoiceWindow()
    {
        Title = "Battery Monitor";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Resources/AppIcon.ico"));
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x14));
        Topmost = true;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("startup_prompt"),
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 14),
        });
        panel.Children.Add(MakeButton(Loc.Get("startup_window"), false, isDefault: true));
        panel.Children.Add(MakeButton(Loc.Get("startup_overlay"), true, isDefault: false));
        Content = panel;
    }

    private System.Windows.Controls.Button MakeButton(string text, bool overlay, bool isDefault)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = text,
            Padding = new Thickness(0, 9, 0, 9),
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 13,
            IsDefault = isDefault,
        };
        btn.Click += (_, _) =>
        {
            ChoseOverlay = overlay;
            Close();
        };
        return btn;
    }
}
