using System.IO;
using System.Text.Json;

namespace BatteryMonitor.Services;

/// <summary>
/// Learns "system watts ≈ base + a·CPU watts + b·GPU load % + c·screen brightness %" from time spent on
/// battery (where the discharge rate is the true system draw), using exponentially-forgotten least squares.
/// </summary>
public sealed class LoadModel
{
    private const int F = 4;
    private const double Forget = 0.9998;                 // per sample: ~5000 samples ≈ 5-6 h of battery time
    private const double LearnedAfter = 150;              // effective samples before "learning" ends
    private static readonly double[] Prior = { 6, 1.0, 0.35, 0.08 };    // W, W per CPU W, W per GPU %, W per brightness %
    private static readonly double[] Ridge = { 10, 10, 3000, 100000 };
    private static readonly double[] Lo = { 0, 0, 0, 0 };
    private static readonly double[] Hi = { 50, 3, 1.5, 0.3 };

    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryMonitor", "load_model.json");

    private readonly object _gate = new();
    private double[] _s = new double[F * F];     // Σ w·x·xᵀ
    private double[] _t = new double[F];         // Σ w·x·y
    private double _weight;                      // Σ w
    private double _sse;                         // Σ w·err²
    private double[] _coef = (double[])Prior.Clone();
    private DateTime _lastSave = DateTime.UtcNow;

    private class State { public double[] S { get; set; } = new double[F * F]; public double[] T { get; set; } = new double[F]; public double W { get; set; } public double Sse { get; set; } }

    /// <param name="path">Override for tests; null uses the app's data folder.</param>
    public LoadModel(string? path = null)
    {
        _path = path ?? Path_;
        try
        {
            if (!File.Exists(_path)) return;
            var st = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            if (st == null) return;

            if (st.S.Length == F * F && st.T.Length == F)
            {
                _s = st.S; _t = st.T; _weight = st.W; _sse = st.Sse;
            }
            else if (st.S.Length == 9 && st.T.Length == 3)
            {
                // Migrate the earlier 3-input model: treat brightness as a constant 45 % for that older data,
                // which is exactly what its samples would have looked like with the extra input added.
                const double b = 45;
                for (var i = 0; i < 3; i++)
                    for (var j = 0; j < 3; j++) _s[i * F + j] = st.S[i * 3 + j];
                for (var i = 0; i < 3; i++) { _s[i * F + 3] = b * st.S[i * 3]; _s[3 * F + i] = b * st.S[i * 3]; _t[i] = st.T[i]; }
                _s[3 * F + 3] = b * b * st.S[0];
                _t[3] = b * st.T[0];
                _weight = st.W; _sse = st.Sse;
            }
            Solve();
        }
        catch { }
    }

    private readonly string _path;

    public double EffectiveSamples { get { lock (_gate) return _weight; } }

    public double ErrorWatts { get { lock (_gate) return _weight >= 5 ? Math.Sqrt(_sse / _weight) : double.NaN; } }

    public string Quality
    {
        get
        {
            lock (_gate)
            {
                if (_weight < LearnedAfter) return "learning";
                var rms = Math.Sqrt(_sse / _weight);
                return rms <= 2.5 ? "good" : rms <= 5 ? "fair" : "rough";
            }
        }
    }

    /// <summary>Watts per CPU watt, and per GPU-load percent, as currently learned.</summary>
    public (double Base, double PerCpuWatt, double PerGpuPercent, double PerBrightness) Coefficients
    {
        get { lock (_gate) return (_coef[0], _coef[1], _coef[2], _coef[3]); }
    }

    public double Predict(double cpuWatts, double gpuLoad, double brightness)
    {
        lock (_gate) return Math.Max(0, _coef[0] + _coef[1] * cpuWatts + _coef[2] * gpuLoad + _coef[3] * brightness);
    }

    /// <summary>Feed one on-battery observation: measured system draw next to what the sensors read.</summary>
    public void Learn(double cpuWatts, double gpuLoad, double brightness, double systemWatts)
    {
        lock (_gate)
        {
            var err = systemWatts - (_coef[0] + _coef[1] * cpuWatts + _coef[2] * gpuLoad + _coef[3] * brightness);
            _sse = _sse * Forget + err * err;
            _weight = _weight * Forget + 1;

            double[] x = { 1, cpuWatts, gpuLoad, brightness };
            for (var i = 0; i < F; i++)
            {
                for (var j = 0; j < F; j++) _s[i * F + j] = _s[i * F + j] * Forget + x[i] * x[j];
                _t[i] = _t[i] * Forget + x[i] * systemWatts;
            }
            Solve();

            if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(2)) SaveLocked();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _s = new double[F * F]; _t = new double[F]; _weight = 0; _sse = 0;
            _coef = (double[])Prior.Clone();
            try { File.Delete(_path); } catch { }
        }
    }

    public void Save() { lock (_gate) SaveLocked(); }

    private void SaveLocked()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new State { S = _s, T = _t, W = _weight, Sse = _sse }));
        }
        catch { }
    }

    // Solve (S + ridge) c = T + ridge·prior, then keep coefficients physically sensible.
    private void Solve()
    {
        var a = new double[F, F + 1];
        for (var i = 0; i < F; i++)
        {
            for (var j = 0; j < F; j++) a[i, j] = _s[i * F + j] + (i == j ? Ridge[i] : 0);
            a[i, F] = _t[i] + Ridge[i] * Prior[i];
        }
        for (var col = 0; col < F; col++)
        {
            var piv = col;
            for (var r = col + 1; r < F; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-9) return;
            if (piv != col) for (var k = 0; k <= F; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
            for (var r = col + 1; r < F; r++)
            {
                var f = a[r, col] / a[col, col];
                for (var k = col; k <= F; k++) a[r, k] -= f * a[col, k];
            }
        }
        var c = new double[F];
        for (var i = F - 1; i >= 0; i--)
        {
            var sum = a[i, F];
            for (var j = i + 1; j < F; j++) sum -= a[i, j] * c[j];
            c[i] = sum / a[i, i];
        }
        for (var i = 0; i < F; i++) _coef[i] = Math.Clamp(c[i], Lo[i], Hi[i]);
    }
}

