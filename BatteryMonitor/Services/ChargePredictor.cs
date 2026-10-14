using System.IO;
using System.Text.Json;

namespace BatteryMonitor.Services;

/// <summary>
/// Time-to-full that understands charging tapers off near full.
///
/// It learns a charging-power curve (watts the battery takes at each state of charge, in 2 % steps) from
/// every charge, and the curve persists. To estimate, it adds up the time of each remaining step at that
/// step's learned power, scaled by how far the current intake sits below the curve (for example while a
/// game is taking the charger's power), with that adjustment fading out over about 15 minutes because
/// the load usually doesn't last. Steps with no data yet use a typical Li-ion taper shape scaled to the
/// current intake.
/// </summary>
public sealed class ChargePredictor
{
    private const int Bins = 50;                    // 2 % each
    private const double MinWatts = 1.0;

    private readonly string? _path;
    private double[] _curve = new double[Bins];     // learned watts per bin; 0 = unknown
    private int[] _count = new int[Bins];

    private double _ratio = 1;                      // current intake ÷ learned curve, smoothed
    private DateTime _lastSeen;
    private DateTime _lastSave = DateTime.UtcNow;

    private class State { public double[] Curve { get; set; } = new double[Bins]; public int[] Count { get; set; } = new int[Bins]; }

    public ChargePredictor(string? dataDirectory)
    {
        _path = dataDirectory == null ? null : System.IO.Path.Combine(dataDirectory, "charge_curve.json");
        try
        {
            if (_path != null && File.Exists(_path) && JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) is { } s
                && s.Curve.Length == Bins && s.Count.Length == Bins)
            { _curve = s.Curve; _count = s.Count; }
        }
        catch { }
    }

    /// <summary>How much of the charging curve (0-100 %) has been learned.</summary>
    public int KnownPercent => _count.Count(c => c > 0) * 100 / Bins;

    public void Reset()
    {
        _curve = new double[Bins]; _count = new int[Bins]; _ratio = 1;
        if (_path != null) try { File.Delete(_path); } catch { }
    }

    /// <summary>Typical Li-ion shape: full power to ~80 %, then easing off towards ~35 % at 100 %.</summary>
    private static double Shape(double percent) =>
        percent <= 80 ? 1.0 : 1.0 - 0.65 * (percent - 80) / 20.0;

    /// <summary>
    /// Call every poll. Returns minutes to full while charging with a usable intake reading, else null.
    /// </summary>
    /// <param name="intakeWatts">Smoothed power going into the battery.</param>
    /// <param name="fullWh">Battery capacity when full, in watt-hours.</param>
    public double? Update(DateTime now, double percent, bool charging, double? intakeWatts, double fullWh)
    {
        if (_lastSeen != default && now - _lastSeen > TimeSpan.FromMinutes(5)) _ratio = 1;
        _lastSeen = now;

        if (!charging || percent >= 99.5 || percent < 0) { if (!charging) _ratio = 1; return null; }
        if (intakeWatts is not > MinWatts) return null;
        var intake = intakeWatts.Value;

        var bin = Math.Clamp((int)(percent / 100.0 * Bins), 0, Bins - 1);

        // ── learn: slow to move down (loads dip the intake), quicker to move up (the unloaded charger power) ──
        if (_count[bin] == 0) _curve[bin] = intake;
        else
        {
            var a = intake > _curve[bin] ? 0.12 : 0.015;
            _curve[bin] += a * (intake - _curve[bin]);
        }
        _count[bin] = Math.Min(_count[bin] + 1, 100000);

        // ── how far below (or above) the curve is the intake right now? ──
        var expectedNow = ExpectedAt(bin, percent, intake);
        var target = Math.Clamp(intake / Math.Max(expectedNow, MinWatts), 0.15, 1.3);
        _ratio += (1 - Math.Exp(-2.0 / 180.0)) * (target - _ratio);   // ~3 min smoothing at the 2 s poll

        // ── estimate: walk the remaining bins ──
        double minutes = 0, elapsed = 0;
        for (var b = bin; b < Bins; b++)
        {
            var binStart = Math.Max(percent, b * 100.0 / Bins);
            var span = (b + 1) * 100.0 / Bins - binStart;               // percent of capacity left in this bin
            if (span <= 0) continue;
            var power = ExpectedAt(b, binStart, intake);
            var fade = Math.Exp(-elapsed / 15.0);                        // the current load effect wears off
            var effective = Math.Max(MinWatts, power * (1 + (_ratio - 1) * fade));
            var energyWh = span / 100.0 * fullWh;
            var stepMinutes = energyWh / effective * 60.0;
            minutes += stepMinutes; elapsed += stepMinutes;
        }

        if (_path != null && now - _lastSave > TimeSpan.FromMinutes(2)) Save();
        return minutes;
    }

    // Expected intake at a bin: learned curve if known, otherwise the typical shape scaled to what we see now.
    private double ExpectedAt(int bin, double percentInBin, double currentIntake)
    {
        if (_count[bin] > 0 && _curve[bin] > MinWatts) return _curve[bin];

        // Scale the typical shape from the nearest bin we know about, else from the current intake.
        double refPct = percentInBin, refWatts = currentIntake;
        var best = -1;
        for (var d = 1; d < Bins && best < 0; d++)
        {
            if (bin - d >= 0 && _count[bin - d] > 0) best = bin - d;
            else if (bin + d < Bins && _count[bin + d] > 0) best = bin + d;
        }
        if (best >= 0) { refPct = (best + 0.5) * 100.0 / Bins; refWatts = _curve[best]; }
        return Math.Max(MinWatts, refWatts * Shape(percentInBin) / Math.Max(Shape(refPct), 0.1));
    }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new State { Curve = _curve, Count = _count }));
        }
        catch { }
    }
}
