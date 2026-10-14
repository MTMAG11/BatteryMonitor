using System.Management;
using System.Windows.Forms;

namespace BatteryMonitor.Services;

public record BatteryInfo
{
    // Live
    public bool PowerOnline { get; init; }
    public bool Charging { get; init; }
    /// <summary>Power flowing INTO the battery (W). Null = charging but rate unknown.</summary>
    public double? ChargeRateWatt { get; init; }
    /// <summary>Power drawn FROM the battery (W).</summary>
    public double DischargeRateWatt { get; init; }
    public int RemainingCapacityMwh { get; init; }
    public double VoltageMv { get; init; }

    /// <summary>Percentage as reported by Windows itself (matches the system battery icon).</summary>
    public double? SystemPercent { get; init; }

    // Estimates
    public int? EstimatedRuntimeMinutes { get; init; }

    // Static
    public string DeviceName { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public int DesignedCapacityMwh { get; init; }
    public int FullChargedCapacityMwh { get; init; }
    public int? CycleCount { get; init; }
    public int BatteryCount { get; init; } = 1;

    // Learned system-load estimate (only meaningful while plugged in)
    public double? SystemLoadWatts { get; init; }
    public double? SystemLoadErrorWatts { get; init; }
    public string? SystemLoadQuality { get; init; }
    public double SystemLoadSamples { get; init; }

    // Learned time-left model (only while discharging)
    public double? RuntimeErrorFraction { get; init; }
    public double? RuntimeReservePercent { get; init; }
    public int RuntimeSelfChecks { get; init; }

    // Computed
    public int ChargePercent => SystemPercent is { } p
        ? (int)Math.Round(p)
        : FullChargedCapacityMwh > 0
            ? (int)Math.Round(RemainingCapacityMwh / (double)FullChargedCapacityMwh * 100)
            : 0;

    /// <summary>What the gauge reports right now, which can wobble by several percent and exceed 100.</summary>
    public double HealthRawPercent => DesignedCapacityMwh > 0
        ? Math.Round(FullChargedCapacityMwh / (double)DesignedCapacityMwh * 100, 1)
        : 0;

    /// <summary>Health from Windows' battery history when available (steadier), never above 100.</summary>
    public double HealthPercent => DesignedCapacityMwh > 0
        ? Math.Min(100, Math.Round((SmoothedFullChargeMwh ?? FullChargedCapacityMwh) / (double)DesignedCapacityMwh * 100, 1))
        : 0;

    /// <summary>Estimated minutes until full while charging (learned charging curve).</summary>
    public int? EstimatedTimeToFullMinutes { get; init; }
    public int ChargeCurveKnownPercent { get; init; }

    // Battery history (Windows battery report)
    public int? SmoothedFullChargeMwh { get; init; }
    public double? HealthTrendPercentPer30Days { get; init; }
    public int HealthHistoryDays { get; init; }

    public string? SystemLoadBreakdown { get; init; }
}

/// <summary>
/// Rough system-load estimate while on a charger, from the adapter's rated wattage.
/// The adapter's real output isn't readable, so this is a bound, not a measurement.
/// </summary>
public static class PowerSplit
{
    /// <param name="Watts">Estimated system load.</param>
    /// <param name="Prefix">"≤" (at most) or "~" (approximately).</param>
    /// <param name="Note">How the number was obtained, for a tooltip.</param>
    public readonly record struct Estimate(double Watts, string Prefix, string Note = "");

    public static Estimate? ForSystem(BatteryInfo info, double adapterWatts)
    {
        if (!info.PowerOnline) return null;

        // Preferred: the model learned from time on battery.
        if (info.SystemLoadWatts is { } learned)
        {
            var err = info.SystemLoadErrorWatts is { } e && !double.IsNaN(e) ? $", about ±{e:F0} W" : "";
            var parts = info.SystemLoadBreakdown is { Length: > 0 } b ? $"\n{b}" : "";
            return new(learned, "~", $"Learned estimate ({info.SystemLoadQuality}{err}){parts}");
        }

        // Fallback: bound from the charger's rated wattage.
        if (adapterWatts <= 0) return null;

        // Battery is supplying power too, so the adapter is maxed out: system ≈ adapter + battery.
        if (info.DischargeRateWatt > 0.5)
            return new(adapterWatts + info.DischargeRateWatt, "~", "From adapter rating plus battery");

        // Adapter feeds the system first and the battery gets the rest: system ≤ rating − charge.
        var charge = info.ChargeRateWatt ?? 0;
        return new(Math.Max(0, adapterWatts - charge), "≤", "At most: adapter rating minus battery charge");
    }
}

public class BatteryService : IDisposable
{
    private const int PollSeconds = 2;
    private const int StaticRefreshPolls = 30;          // re-read capacities/cycles every ~60 s
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CapacityWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MinDerivedSpan = TimeSpan.FromSeconds(45);

    private enum Mode { Idle, Charging, Discharging }

    private System.Threading.Timer? _timer;
    private int _polling;
    private int _pollCount;
    private bool _disposed;

    private string _deviceName = "";
    private string _manufacturer = "";
    private string _serialNumber = "";
    private int _designedCapacity;
    private int _fullChargedCapacity;
    private int? _cycleCount;
    private string? _errorMessage;

    // Smoothing / cross-check state (only touched from the poll thread)
    private Mode _mode = Mode.Idle;
    private readonly Queue<(DateTime T, double W)> _rateSamples = new();
    private readonly Queue<(DateTime T, int Mwh)> _capSamples = new();

    // Time-estimate smoothing: a slow average of the power draw, and a predicted
    // "empty/full at" clock time that is eased toward each new prediction. Showing
    // (that time − now) makes the estimate count down steadily instead of jumping.
    private const double RateTauSeconds = 120;
    private const double EtaTauSeconds = 90;
    private double? _longWatts;
    private DateTime? _eta;
    private DateTime _lastPoll;

    // System-load learning: sensors, model, and 30 s averaging windows for the regressors.
    private readonly PowerSensors _sensors = new();
    private readonly LoadModel _model = new();
    private readonly Queue<(DateTime T, double V)> _cpuSamples = new();
    private readonly Queue<(DateTime T, double V)> _gpuSamples = new();
    private DateTime _lastLearn;
    private static readonly TimeSpan LearnEvery = TimeSpan.FromSeconds(4);

    private static readonly string DataDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryMonitor");

    // Self-checking time-left predictor, and the battery-critical level it counts down to.
    private readonly RuntimePredictor _runtime = new(DataDir);
    private DateTime _reserveChecked = DateTime.MinValue;

    // Learned charging curve, and Windows' battery history (steadier health, typical draw, wear trend).
    private readonly ChargePredictor _charge = new(DataDir);
    private readonly HealthHistory _health = new(DataDir);
    private DateTime _healthTried = DateTime.MinValue;

    /// <summary>Forget everything learned about this laptop's power use and battery.</summary>
    public void ResetLoadModel() { _model.Reset(); _runtime.Reset(); _charge.Reset(); }

    // Read-only query of the active power plan's critical battery level; refreshed every 15 minutes.
    private void RefreshReservePolicy(DateTime now)
    {
        if (now - _reserveChecked < TimeSpan.FromMinutes(15)) return;
        _reserveChecked = now;
        try
        {
            static int? Query(string setting)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powercfg", Arguments = $"/query SCHEME_CURRENT SUB_BATTERY {setting}",
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                });
                if (p == null) return null;
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    @"Current DC Power Setting Index:\s*0x([0-9A-Fa-f]+)");
                return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : null;
            }
            var level = Query("BATLEVELCRIT");
            var action = Query("BATACTIONCRIT");           // 0 = do nothing
            if (level is { } l)
                _runtime.SetPolicyReserve(action == 0 ? 2 : l);   // "do nothing": the firmware cuts power near 2 %
        }
        catch { /* keep the default */ }
    }

    public (string Quality, double Error, double Samples) LoadModelStatus =>
        (_model.Quality, _model.ErrorWatts, _model.EffectiveSamples);

    public event Action<BatteryInfo>? BatteryUpdated;
    public event Action<string?>? ErrorChanged;

    /// <summary>Call after subscribing to the events so the first reading isn't lost.</summary>
    public void Start()
    {
        if (_health.HabitWatts is { } habit) _runtime.SeedHabit(habit);   // history on disk from an earlier run
        _timer = new System.Threading.Timer(_ => Poll(), null, 0, PollSeconds * 1000);
    }

    // ── Static data (capacities change slowly, so it is refreshed periodically) ──

    private void LoadStaticData()
    {
        try
        {
            string name = "", mfr = "", serial = "";
            int design = 0, full = 0;
            int? cycles = null;

            using (var s = new ManagementObjectSearcher("root\\wmi",
                "SELECT DeviceName, ManufactureName, SerialNumber, DesignedCapacity FROM BatteryStaticData"))
            {
                foreach (ManagementObject obj in s.Get())
                {
                    if (name == "") name = obj["DeviceName"]?.ToString() ?? "";
                    if (mfr == "") mfr = obj["ManufactureName"]?.ToString() ?? "";
                    if (serial == "") serial = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                    design += Valid(obj["DesignedCapacity"], 2_000_000);
                }
            }

            using (var s = new ManagementObjectSearcher("root\\wmi",
                "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
            {
                foreach (ManagementObject obj in s.Get())
                    full += Valid(obj["FullChargedCapacity"], 2_000_000);
            }

            try
            {
                using var s = new ManagementObjectSearcher("root\\wmi", "SELECT CycleCount FROM BatteryCycleCount");
                foreach (ManagementObject obj in s.Get())
                {
                    var c = Valid(obj["CycleCount"], 100_000);
                    if (c > 0) cycles = (cycles ?? 0) + c;   // 0 means "not reported"
                }
            }
            catch { /* not available on all systems */ }

            _deviceName = name; _manufacturer = mfr; _serialNumber = serial;
            _designedCapacity = design;
            if (full > 0 && full != _fullChargedCapacity)
            {
                _fullChargedCapacity = full;
                _capSamples.Clear();
            }
            _cycleCount = cycles;
            SetError(null);
        }
        catch (Exception ex)
        {
            SetError($"WMI error (static data): {ex.Message}");
        }
    }

    // ── Live polling ──

    private void Poll()
    {
        // Never overlap polls (a slow WMI call must not pile up).
        if (_disposed || Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            if (_pollCount++ % StaticRefreshPolls == 0) LoadStaticData();

            bool online = false, charging = false, discharging = false;
            long chargeMw = 0, dischargeMw = 0;
            int remaining = 0, count = 0, voltCount = 0;
            double voltSum = 0;

            using (var s = new ManagementObjectSearcher("root\\wmi",
                "SELECT PowerOnline, Charging, Discharging, ChargeRate, DischargeRate, RemainingCapacity, Voltage FROM BatteryStatus"))
            {
                foreach (ManagementObject obj in s.Get())
                {
                    count++;
                    online |= Convert.ToBoolean(obj["PowerOnline"]);
                    charging |= Convert.ToBoolean(obj["Charging"]);
                    discharging |= Convert.ToBoolean(obj["Discharging"]);
                    chargeMw += Valid(obj["ChargeRate"], 500_000);        // >500 W = "unknown" marker
                    dischargeMw += Valid(obj["DischargeRate"], 500_000);
                    remaining += Valid(obj["RemainingCapacity"], 2_000_000);
                    var v = Valid(obj["Voltage"], 100_000);                // 0xFFFFFFFF = unknown
                    if (v > 0) { voltSum += v; voltCount++; }
                }
            }

            if (count == 0) return;

            var mode = charging ? Mode.Charging : discharging ? Mode.Discharging : Mode.Idle;
            if (mode != _mode)
            {
                _mode = mode;
                _rateSamples.Clear();
                _capSamples.Clear();
                _longWatts = null;
                _eta = null;
            }

            var now = DateTime.UtcNow;
            var dt = _lastPoll == default ? PollSeconds : Math.Clamp((now - _lastPoll).TotalSeconds, 0.5, 30);
            _lastPoll = now;
            var reportedW = mode switch
            {
                Mode.Charging => chargeMw / 1000.0,
                Mode.Discharging => dischargeMw / 1000.0,
                _ => 0.0,
            };
            // Zero while actively charging/discharging is a firmware gap, not a real reading.
            if (mode != Mode.Idle && reportedW > 0) _rateSamples.Enqueue((now, reportedW));
            while (_rateSamples.Count > 0 && now - _rateSamples.Peek().T > RateWindow) _rateSamples.Dequeue();

            _capSamples.Enqueue((now, remaining));
            while (_capSamples.Count > 0 && now - _capSamples.Peek().T > CapacityWindow) _capSamples.Dequeue();

            double? smoothed = _rateSamples.Count > 0 ? _rateSamples.Average(x => x.W) : null;
            var watts = ResolveWatts(mode, smoothed, now, remaining);

            var windowsPercent = ReadWindowsPercent();
            // Battery history from Windows (refreshed in the background); seeds the time-left model's habit level.
            if (now - _healthTried > TimeSpan.FromHours(1) && _health.NeedsRefresh)
            {
                _healthTried = now;
                Task.Run(() => { _health.Refresh(); if (_health.HabitWatts is { } hw) _runtime.SeedHabit(hw); });
            }
            var fullForCalc = _health.SmoothedFullMwh ?? _fullChargedCapacity;

            // ── Learned system load ──
            double? systemLoad = null; string? breakdown = null;
            if (_sensors.CpuAvailable && _sensors.ReadCpuWatts() is { } cpuNow)
            {
                var bright = _sensors.ReadBrightness();
                _cpuSamples.Enqueue((now, cpuNow));
                _gpuSamples.Enqueue((now, _sensors.ReadDiscreteGpuLoad()));
                while (_cpuSamples.Count > 0 && now - _cpuSamples.Peek().T > RateWindow) _cpuSamples.Dequeue();
                while (_gpuSamples.Count > 0 && now - _gpuSamples.Peek().T > RateWindow) _gpuSamples.Dequeue();
                var cpuAvg = _cpuSamples.Average(x => x.V);
                var gpuAvg = _gpuSamples.Average(x => x.V);

                if (mode == Mode.Discharging && !online && smoothed is > 1.0
                    && _cpuSamples.Count >= 5 && now - _lastLearn >= LearnEvery)
                {
                    _lastLearn = now;
                    _model.Learn(cpuAvg, gpuAvg, bright, smoothed.Value);   // on battery, discharge = true system draw
                }
                if (online) systemLoad = _model.Predict(cpuAvg, gpuAvg, bright);

                var co = _model.Coefficients;
                breakdown = $"Base {co.Base:F0} W + CPU {co.PerCpuWatt * cpuAvg:F0} W + GPU {co.PerGpuPercent * gpuAvg:F0} W + screen {co.PerBrightness * bright:F0} W ({bright:F0}%)";
            }

            // Slow (about 2 min) average of the draw; also the "recent" guess for the self-checking predictor.
            if (mode != Mode.Idle && watts is > 0.3)
            {
                var a = 1 - Math.Exp(-dt / RateTauSeconds);
                _longWatts = _longWatts is { } w ? w + a * (watts.Value - w) : watts.Value;
            }

            var percentNow = windowsPercent ?? (_fullChargedCapacity > 0 ? remaining * 100.0 / _fullChargedCapacity : 0);
            RefreshReservePolicy(now);
            var learned = _runtime.Update(now, percentNow, mode == Mode.Discharging, remaining, fullForCalc, _longWatts);
            var chargeMinutes = _charge.Update(now, percentNow, mode == Mode.Charging, mode == Mode.Charging ? watts : null, fullForCalc / 1000.0);

            var smoothedMinutes = SmoothedMinutes(mode, watts, remaining, now, dt,
                mode == Mode.Discharging ? learned?.Minutes : chargeMinutes);
            var estimatedRuntime = mode == Mode.Discharging
                ? smoothedMinutes ?? EstimateRuntimeFallback()
                : null;
            var timeToFull = mode == Mode.Charging ? smoothedMinutes : null;

            var info = new BatteryInfo
            {
                PowerOnline = online,
                Charging = charging,
                ChargeRateWatt = mode == Mode.Charging ? watts : 0,
                DischargeRateWatt = mode == Mode.Discharging ? (watts ?? 0) : 0,
                RemainingCapacityMwh = remaining,
                VoltageMv = voltCount > 0 ? voltSum / voltCount : 0,
                SystemPercent = windowsPercent,
                EstimatedRuntimeMinutes = estimatedRuntime,
                EstimatedTimeToFullMinutes = timeToFull,
                DeviceName = _deviceName,
                Manufacturer = _manufacturer,
                SerialNumber = _serialNumber,
                DesignedCapacityMwh = _designedCapacity,
                FullChargedCapacityMwh = _fullChargedCapacity,
                CycleCount = _cycleCount,
                BatteryCount = count,
                SystemLoadWatts = systemLoad,
                SystemLoadErrorWatts = systemLoad != null ? _model.ErrorWatts : null,
                SystemLoadQuality = systemLoad != null ? _model.Quality : null,
                SystemLoadSamples = _model.EffectiveSamples,
                RuntimeErrorFraction = learned?.ErrorFraction,
                RuntimeReservePercent = learned?.ReservePercent,
                RuntimeSelfChecks = _runtime.SelfChecks,
                ChargeCurveKnownPercent = _charge.KnownPercent,
                SmoothedFullChargeMwh = _health.SmoothedFullMwh,
                HealthTrendPercentPer30Days = _health.TrendPercentPer30Days,
                HealthHistoryDays = _health.DaysCovered,
                SystemLoadBreakdown = breakdown,
            };

            SetError(null);
            BatteryUpdated?.Invoke(info);
        }
        catch (Exception ex)
        {
            SetError($"WMI error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>
    /// Smoothed firmware rate when available; otherwise the rate implied by how fast the
    /// remaining capacity is actually changing. Null = charging but nothing usable yet.
    /// </summary>
    private double? ResolveWatts(Mode mode, double? smoothed, DateTime now, int remaining)
    {
        if (mode == Mode.Idle) return 0;
        if (smoothed is > 0.3) return Math.Round(smoothed.Value, 1);

        // Capacity moves in coarse steps, so only trust a long enough window.
        if (_capSamples.Count > 1)
        {
            var oldest = _capSamples.Peek();
            var span = now - oldest.T;
            if (span >= MinDerivedSpan)
            {
                var wDelta = (remaining - oldest.Mwh) / 1000.0 / span.TotalHours;   // + = charging
                var w = mode == Mode.Charging ? wDelta : -wDelta;
                if (w > 0.5) return Math.Round(w, 1);
            }
        }
        return mode == Mode.Charging ? null : 0;
    }

    /// <summary>
    /// Minutes until empty (discharging) or full (charging), or null if there is no usable rate yet.
    /// </summary>
    private int? SmoothedMinutes(Mode mode, double? watts, int remainingMwh, DateTime now, double dt, double? learnedMinutes)
    {
        if (mode == Mode.Idle) return null;

        var energyMwh = mode == Mode.Discharging ? remainingMwh : _fullChargedCapacity - remainingMwh;
        if (energyMwh <= 0) return 0;

        // Discharging: use the self-checking predictor when it has an answer. Otherwise (and when charging)
        // fall back to remaining energy ÷ a slow average of the power.
        double minutes;
        if (learnedMinutes is { } learned)
            minutes = learned;
        else if (_longWatts is > 0.5)
            minutes = energyMwh / (_longWatts.Value * 1000.0) * 60.0;
        else
            return null;

        var target = now.AddMinutes(minutes);
        if (_eta is not { } eta)
        {
            _eta = target;
        }
        else
        {
            var left = Math.Max((eta - now).TotalMinutes, 10);
            if (Math.Abs((target - eta).TotalMinutes) > left * 0.4)
                _eta = target;                                   // genuine change in load: follow it
            else
                _eta = eta.AddSeconds((target - eta).TotalSeconds * (1 - Math.Exp(-dt / EtaTauSeconds)));
        }

        return Math.Max(0, (int)Math.Round((_eta.Value - now).TotalMinutes));
    }

    private static int? EstimateRuntimeFallback()
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\wmi", "SELECT EstimatedRuntime FROM BatteryRuntime");
            foreach (ManagementObject obj in s.Get())
            {
                var secs = Convert.ToInt64(obj["EstimatedRuntime"]);
                if (secs > 0 && secs < 999_999) return (int)(secs / 60);
                break;
            }
        }
        catch { /* BatteryRuntime not available on all systems */ }

        var left = SystemInformation.PowerStatus.BatteryLifeRemaining;   // seconds, -1 = unknown
        return left > 0 ? left / 60 : null;
    }

    private static double? ReadWindowsPercent()
    {
        try
        {
            var ps = SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & (BatteryChargeStatus.NoSystemBattery | BatteryChargeStatus.Unknown)) != 0)
                return null;
            var p = ps.BatteryLifePercent;
            return p is >= 0 and <= 1 ? p * 100.0 : null;
        }
        catch { return null; }
    }

    /// <summary>Converts a WMI value to int, treating anything above <paramref name="max"/> as unknown (0).</summary>
    private static int Valid(object? value, long max)
    {
        if (value == null) return 0;
        var v = Convert.ToInt64(value);
        return v < 0 || v > max ? 0 : (int)v;
    }

    private void SetError(string? msg)
    {
        if (_disposed || _errorMessage == msg) return;
        _errorMessage = msg;
        try { ErrorChanged?.Invoke(msg); } catch { /* UI already shutting down */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _model.Save();
        _charge.Save();
        _runtime.Shutdown();
    }
}



