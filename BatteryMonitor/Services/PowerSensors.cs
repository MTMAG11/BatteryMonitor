using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BatteryMonitor.Services;

/// <summary>
/// CPU package power and discrete-GPU load from Windows performance counters.
/// Deliberately avoids the NVIDIA driver API, which can keep a sleeping GPU awake on hybrid laptops.
/// </summary>
public sealed class PowerSensors
{
    private static readonly Regex LuidRx = new(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)", RegexOptions.Compiled);
    private static readonly Regex EngRx = new(@"engtype_(\w+)", RegexOptions.Compiled);


    private readonly PerformanceCounter? _cpu;
    private double _brightness = 50;
    private DateTime _brightnessChecked = DateTime.MinValue;

    /// <summary>Screen brightness in percent (cached 10 s); 50 if the panel doesn't report it.</summary>
    public double ReadBrightness()
    {
        if (DateTime.UtcNow - _brightnessChecked < TimeSpan.FromSeconds(10)) return _brightness;
        _brightnessChecked = DateTime.UtcNow;
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("root\\wmi",
                "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                _brightness = Convert.ToDouble(o["CurrentBrightness"]);
                break;
            }
        }
        catch { /* external monitor / not supported: keep last value */ }
        return _brightness;
    }


    private readonly Dictionary<string, CounterSample> _prevGpu = new();
    private string? _dGpuLuid;
    private DateTime _luidChecked = DateTime.MinValue;

    public bool CpuAvailable => _cpu != null;

    public PowerSensors()
    {
        try
        {
            var instance = new PerformanceCounterCategory("Energy Meter").GetInstanceNames()
                .FirstOrDefault(n => n.Contains("pkg", StringComparison.OrdinalIgnoreCase));
            if (instance != null)
            {
                _cpu = new PerformanceCounter("Energy Meter", "Power", instance, true);
                _cpu.NextValue();
            }
        }
        catch { /* counter set not present on this machine */ }
    }

    /// <summary>CPU package power in watts, or null if the counter isn't available.</summary>
    public double? ReadCpuWatts()
    {
        try { return _cpu?.NextValue() / 1000.0; }   // counter reports milliwatts
        catch { return null; }
    }

    /// <summary>Busiest-engine utilisation (0-100) of the discrete GPU; 0 if it is asleep or absent.</summary>
    public double ReadDiscreteGpuLoad()
    {
        try
        {
            RefreshDiscreteGpu();
            if (_dGpuLuid == null) return 0;

            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            if (!data.Contains("Utilization Percentage")) return 0;

            var perType = new Dictionary<string, double>();
            var seen = new HashSet<string>();
            foreach (DictionaryEntry e in data["Utilization Percentage"])
            {
                var name = (string)e.Key;
                var m = LuidRx.Match(name);
                if (!m.Success || m.Groups[1].Value != _dGpuLuid) continue;

                var sample = ((InstanceData)e.Value!).Sample;
                seen.Add(name);
                if (_prevGpu.TryGetValue(name, out var prev))
                {
                    var type = EngRx.Match(name).Groups[1].Value;
                    perType[type] = perType.GetValueOrDefault(type) + CounterSample.Calculate(prev, sample);
                }
                _prevGpu[name] = sample;
            }
            foreach (var stale in _prevGpu.Keys.Where(k => !seen.Contains(k)).ToList()) _prevGpu.Remove(stale);

            return perType.Count == 0 ? 0 : Math.Min(100, perType.Values.Max());
        }
        catch { return 0; }
    }

    // The discrete GPU is the adapter with the most dedicated video memory in use.
    private void RefreshDiscreteGpu()
    {
        if (DateTime.UtcNow - _luidChecked < TimeSpan.FromSeconds(60)) return;
        _luidChecked = DateTime.UtcNow;
        try
        {
            var data = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            if (!data.Contains("Dedicated Usage")) return;

            long best = -1; string? luid = null;
            foreach (DictionaryEntry e in data["Dedicated Usage"])
            {
                var m = LuidRx.Match((string)e.Key);
                var raw = ((InstanceData)e.Value!).RawValue;
                if (m.Success && raw > best) { best = raw; luid = m.Groups[1].Value; }
            }
            _dGpuLuid = luid;
        }
        catch { }
    }
}
