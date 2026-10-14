using System.IO;
using System.Text.Json;

namespace BatteryMonitor.Services;

public class AppSettings
{
    public string Language { get; set; } = "";
    public string OverlayCorner { get; set; } = "TopRight";
    public bool OverlayShowPercent { get; set; } = true;
    public bool OverlayShowWatt { get; set; } = true;
    public bool OverlayShowTime { get; set; } = true;
    public bool OverlayShowVoltage { get; set; } = false;
    public bool OverlayShowHealth { get; set; } = false;
    public double AdapterWatts { get; set; } = 0;
    public bool OverlayShowSystem { get; set; } = false;
    public bool OverlayShowChargeCircle { get; set; } = false;
    public bool OverlayShowHealthCircle { get; set; } = false;

    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryMonitor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
