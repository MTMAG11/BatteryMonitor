using System.IO;
using System.Text.Json;

namespace BatteryMonitor.Services;

/// <summary>
/// Learns the CPU's true idle power over days: the lowest level it ever settles at. Drops immediately
/// to any new low, and creeps up extremely slowly so a one-off glitch doesn't stay forever.
/// Power above this level is what gets credited to apps.
/// </summary>
public sealed class FloorTracker
{
    private const double Prior = 4.0;       // typical idle package power for a modern laptop CPU, until we've seen better
    private const double MinFloor = 2.0;

    private readonly string? _path;
    private double _floor = Prior;
    private DateTime _lastSave = DateTime.UtcNow;

    private class State { public double Floor { get; set; } = Prior; }

    public FloorTracker(string? dataDirectory)
    {
        _path = dataDirectory == null ? null : System.IO.Path.Combine(dataDirectory, "cpu_floor.json");
        try
        {
            if (_path != null && File.Exists(_path) && JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) is { } s
                && s.Floor is >= MinFloor and <= 30)
                _floor = s.Floor;
        }
        catch { }
    }

    public double Value => _floor;

    public void Observe(double watts)
    {
        if (watts < 0.5) return;
        if (watts < _floor) _floor = Math.Max(MinFloor, watts);
        else _floor += (watts - _floor) * 1e-5;          // per ~4 s sample: a time constant of a few days

        if (_path != null && DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(10)) Save();
    }

    public void Reset() { _floor = Prior; if (_path != null) try { File.Delete(_path); } catch { } }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new State { Floor = _floor }));
        }
        catch { }
    }
}
