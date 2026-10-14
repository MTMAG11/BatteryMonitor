using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BatteryMonitor.Services;

/// <summary>
/// Self-checking time-left estimator for discharging.
///
/// Instead of picking a fixed averaging window, it forecasts the power draw from the WHOLE history
/// and lets recent changes adjust it in proportion. It keeps a reference level (your long-term habit,
/// or this session's average) and looks at how far the session average, the last ~10 minutes and the
/// last ~2 minutes sit above or below it (as log-ratios, i.e. percent changes). A small regression, fitted
/// separately for 15-, 45- and 90-minute look-aheads, learns how much of each deviation actually
/// persists, e.g. "a 30 % spike in the last 2 minutes usually means about +6 % over the next hour".
/// The regression is re-fitted every minute from checks against the energy that really left the battery,
/// with older data gradually forgotten, so it adapts to how you use the laptop.
///
/// It also learns where the laptop really stops, and counts down to that level instead of 0 %.
/// Everything persists across sessions.
/// </summary>
public sealed class RuntimePredictor
{
    public readonly record struct Result(double Minutes, double ErrorFraction, double ReservePercent, int SelfChecks);

    private const int F = 4;                                                  // features: 1, session, last 10 min, last 2 min
    private static readonly double[] Horizons = { 15, 45, 90 };               // minutes
    private static readonly int H = Horizons.Length;
    private static readonly double[] PriorCoef = { 0, 0.6, 0.3, 0.3 };        // start: trust the session level, nudge for recent change
    private static readonly double[] Ridge = { 2, 8, 8, 8 };
    private const double Forget = 0.999;                                      // per check: ~1000 checks (~16 h of battery use)
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(150);

    private readonly string? _dir;

    // ── learned (persisted) ──
    private Head[] _heads = NewHeads();
    private double? _cutoffPct;                                         // where the laptop was seen to stop
    private double? _habitWatts;                                        // typical average draw on battery
    private int _habitCount;                                            // sessions behind it
    private double _lastPct;
    private bool _lastDischarging;
    private DateTime _lastSeen;

    // ── per-session (not persisted) ──
    private readonly List<(DateTime T, int Mwh)> _hist = new();
    private readonly List<Pending> _pending = new();
    private DateTime _sessionStart;
    private DateTime _lastMinute;
    private double _policyReservePct = 5;
    private DateTime _lastSave = DateTime.UtcNow;
    private bool _wasDischarging;

    public sealed class Head
    {
        public double[] S { get; set; } = new double[F * F];     // Σ w·x·xᵀ
        public double[] T { get; set; } = new double[F];         // Σ w·x·y
        public double[] C { get; set; } = (double[])PriorCoef.Clone();
        public double W { get; set; }                            // Σ w
        public double Sse { get; set; }                          // Σ w·residual²
        public int Count { get; set; }
    }

    private static Head[] NewHeads() => Enumerable.Range(0, H).Select(_ => new Head()).ToArray();

    private sealed class Pending
    {
        public int Hz;
        public DateTime Start, Due;
        public int StartMwh;
        public double[] X = new double[F];
        public double Ref;
    }

    private sealed class State
    {
        public Head[] Heads { get; set; } = Array.Empty<Head>();
        public double? CutoffPct { get; set; }
        public double? HabitWatts { get; set; }
        public int HabitCount { get; set; }
        public double LastPct { get; set; }
        public bool LastDischarging { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public RuntimePredictor(string? dataDirectory)
    {
        _dir = dataDirectory;
        if (_dir == null) return;
        try
        {
            var path = System.IO.Path.Combine(_dir, "runtime_model.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<State>(File.ReadAllText(path)) is { } s
                && s.Heads.Length == H && s.Heads.All(h => h.S.Length == F * F && h.T.Length == F && h.C.Length == F))
            {
                _heads = s.Heads; _cutoffPct = s.CutoffPct; _habitWatts = s.HabitWatts; _habitCount = s.HabitCount;
                _lastPct = s.LastPct; _lastDischarging = s.LastDischarging; _lastSeen = s.LastSeen;
            }
        }
        catch { }
    }

    /// <summary>The battery-critical level from the Windows power plan (percent), or 5 if unknown.</summary>
    public void SetPolicyReserve(double percent) => _policyReservePct = Math.Clamp(percent, 0, 20);

    /// <summary>
    /// Start the long-term habit level from Windows' battery history, if nothing has been learned yet.
    /// Counts as a few sessions' worth, so real sessions refine it quickly but don't wipe it out.
    /// </summary>
    public void SeedHabit(double watts)
    {
        if (_habitWatts == null && watts >= 3) { _habitWatts = watts; _habitCount = 3; }
    }

    public double ReservePercent => _cutoffPct ?? _policyReservePct;
    public int SelfChecks => _heads.Max(h => h.Count);

    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Call every poll. Returns a time-left estimate while discharging, otherwise null.</summary>
    /// <param name="recentWatts">Fast (about 2 min) average of the reported draw.</param>
    public Result? Update(DateTime now, double percent, bool discharging, int remainingMwh, int fullMwh, double? recentWatts)
    {
        // The machine disappeared for a while (sleep, hibernate, power loss) at a very low level:
        // that is where it actually stops, so remember it.
        if (_lastSeen != default && now - _lastSeen > Gap)
        {
            if (_lastDischarging && _lastPct is >= 0.5 and <= 10)
                _cutoffPct = _cutoffPct is { } c ? 0.5 * c + 0.5 * _lastPct : _lastPct;
            EndSession(save: false, learnHabit: false);   // energy over the gap says nothing about power
        }

        _lastSeen = now; _lastPct = percent; _lastDischarging = discharging;

        if (!discharging)
        {
            if (_wasDischarging) EndSession(save: true);
            _wasDischarging = false;
            MaybeSave(now);
            return null;
        }
        if (!_wasDischarging) { StartSession(now); _wasDischarging = true; }

        if (fullMwh <= 0 || remainingMwh <= 0) return null;

        // Once a minute: record, check older forecasts against what really happened, queue new checks.
        if (now - _lastMinute >= TimeSpan.FromSeconds(58))
        {
            _lastMinute = now;
            _hist.Add((now, remainingMwh));
            while (_hist.Count > 1 && now - _hist[0].T > TimeSpan.FromHours(8)) _hist.RemoveAt(0);

            ScorePending(now, remainingMwh);
            var feat = Features(now, remainingMwh, recentWatts);
            if (feat is { } ft)
            {
                for (var hz = 0; hz < H; hz++)
                    _pending.Add(new Pending
                    {
                        Hz = hz, Start = now, Due = now.AddMinutes(Horizons[hz]), StartMwh = remainingMwh,
                        X = ft.X, Ref = ft.Ref,
                    });
                Log(now, percent, remainingMwh, recentWatts, ft);
            }
        }

        if (Features(now, remainingMwh, recentWatts) is not { } cur) return null;
        var usable = remainingMwh - ReservePercent / 100.0 * fullMwh;

        // The look-ahead that matters is the time actually left, which itself depends on the answer: iterate.
        double minutes = 60, err = 0.2;
        for (var it = 0; it < 3; it++)
        {
            var (lo, f) = Mix(minutes);
            var yhat = (1 - f) * Dot(_heads[lo].C, cur.X) + (f > 0 ? f * Dot(_heads[lo + 1].C, cur.X) : 0);
            var watts = Math.Max(0.5, cur.Ref * Math.Exp(Math.Clamp(yhat, -1.2, 1.2)));
            minutes = usable <= 0 ? 0 : usable / (watts * 1000.0) * 60.0;
            err = (1 - f) * HeadError(_heads[lo]) + (f > 0 ? f * HeadError(_heads[lo + 1]) : 0);
            if (usable <= 0) break;
        }

        MaybeSave(now);
        return new Result(minutes, Math.Clamp(err, 0.05, 0.6), ReservePercent, SelfChecks);
    }

    // ── features ──

    /// <summary>
    /// A reference power level plus how far each view of recent history sits from it (log-ratios).
    /// Missing views fall back to the next-shorter one, so early in a session the deviations are 0.
    /// </summary>
    private (double Ref, double[] X)? Features(DateTime now, int remainingMwh, double? recentWatts)
    {
        var r2 = recentWatts is > 0.5 ? recentWatts : null;
        var w10 = EnergyPower(now, remainingMwh, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));
        var ses = EnergyPower(now, remainingMwh, now - _sessionStart + TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));

        var first = r2 ?? w10 ?? ses ?? _habitWatts;
        if (first is not { } baseW) return null;
        var rv = r2 ?? baseW;
        var w10v = w10 ?? rv;
        var sv = ses ?? w10v;
        var reference = _habitWatts ?? sv;

        double L(double v) => Math.Log(Math.Max(v, 0.5) / reference);
        return (reference, new[] { 1.0, L(sv), L(w10v), L(rv) });
    }

    /// <summary>Average power over the last <paramref name="window"/>, from the energy that actually left the battery.</summary>
    private double? EnergyPower(DateTime now, int remainingMwh, TimeSpan window, TimeSpan minSpan)
    {
        (DateTime T, int Mwh)? start = null;
        foreach (var h in _hist) if (now - h.T <= window) { start = h; break; }
        if (start is not { } s) return null;
        var span = now - s.T;
        if (span < minSpan) return null;
        var w = (s.Mwh - remainingMwh) / 1000.0 / span.TotalHours;
        return w >= 0.5 ? w : null;
    }

    /// <summary>Which two look-ahead models to blend for a given remaining time (log-interpolated).</summary>
    private static (int Lo, double F) Mix(double minutesLeft)
    {
        var x = (Math.Log(Math.Max(minutesLeft, 1)) - Math.Log(Horizons[0])) /
                (Math.Log(Horizons[^1]) - Math.Log(Horizons[0])) * (H - 1);
        x = Math.Clamp(x, 0, H - 1);
        var lo = Math.Min((int)Math.Floor(x), H - 2);
        return (lo, x - lo);
    }

    private static double Dot(double[] c, double[] x)
    {
        double s = 0;
        for (var i = 0; i < F; i++) s += c[i] * x[i];
        return s;
    }

    private static double HeadError(Head h) => h.W >= 5 ? Math.Sqrt(h.Sse / h.W) : 0.3;

    // ── learning ──

    private void ScorePending(DateTime now, int remainingMwh)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pend = _pending[i];
            if (now < pend.Due) continue;
            _pending.RemoveAt(i);

            var hours = (now - pend.Start).TotalHours;
            var actual = (pend.StartMwh - remainingMwh) / 1000.0 / hours;
            if (hours < Horizons[pend.Hz] / 60.0 * 0.9 || actual < 3) continue;   // too idle/quantised to judge

            var y = Math.Clamp(Math.Log(actual / pend.Ref), -1.5, 1.5);
            var h = _heads[pend.Hz];
            var resid = y - Dot(h.C, pend.X);

            h.Sse = h.Sse * Forget + resid * resid;
            h.W = h.W * Forget + 1;
            h.Count++;
            for (var a = 0; a < F; a++)
            {
                for (var b = 0; b < F; b++) h.S[a * F + b] = h.S[a * F + b] * Forget + pend.X[a] * pend.X[b];
                h.T[a] = h.T[a] * Forget + pend.X[a] * y;
            }
            Fit(h);
        }
    }

    // Solve (S + ridge) c = T + ridge·prior; keep the coefficients sensible.
    private static void Fit(Head h)
    {
        var m = new double[F, F + 1];
        for (var i = 0; i < F; i++)
        {
            for (var j = 0; j < F; j++) m[i, j] = h.S[i * F + j] + (i == j ? Ridge[i] : 0);
            m[i, F] = h.T[i] + Ridge[i] * PriorCoef[i];
        }
        for (var col = 0; col < F; col++)
        {
            var piv = col;
            for (var r = col + 1; r < F; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-9) return;
            if (piv != col) for (var k = 0; k <= F; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
            for (var r = col + 1; r < F; r++)
            {
                var f = m[r, col] / m[col, col];
                for (var k = col; k <= F; k++) m[r, k] -= f * m[col, k];
            }
        }
        var c = new double[F];
        for (var i = F - 1; i >= 0; i--)
        {
            var sum = m[i, F];
            for (var j = i + 1; j < F; j++) sum -= m[i, j] * c[j];
            c[i] = sum / m[i, i];
        }
        h.C[0] = Math.Clamp(c[0], -0.6, 0.6);                       // overall bias
        for (var i = 1; i < F; i++) h.C[i] = Math.Clamp(c[i], -0.5, 1.5);
    }

    private void StartSession(DateTime now)
    {
        _hist.Clear(); _pending.Clear();
        _sessionStart = now; _lastMinute = DateTime.MinValue;
    }

    private void EndSession(bool save, bool learnHabit = true)
    {
        // Remember how hard this session was on the battery (the long-term habit level).
        if (learnHabit && _hist.Count >= 2 && _wasDischarging)
        {
            var (t0, e0) = _hist[0]; var (t1, e1) = _hist[^1];
            var h = (t1 - t0).TotalHours;
            if (h >= 10.0 / 60)
            {
                var w = (e0 - e1) / 1000.0 / h;
                if (w >= 3)
                {
                    // running average that settles over about 10 sessions, then keeps slowly adapting
                    var a = Math.Max(1.0 / (_habitCount + 1), 0.1);
                    _habitWatts = _habitWatts is { } hw ? hw + a * (w - hw) : w;
                    _habitCount++;
                }
            }
        }
        _hist.Clear(); _pending.Clear();
        _wasDischarging = false;
        if (save) Save();
    }

    // ── persistence & log ──

    private void MaybeSave(DateTime now)
    {
        if (now - _lastSave > TimeSpan.FromMinutes(2)) Save();
    }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        if (_dir == null) return;
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(System.IO.Path.Combine(_dir, "runtime_model.json"), JsonSerializer.Serialize(new State
            {
                Heads = _heads, CutoffPct = _cutoffPct, HabitWatts = _habitWatts, HabitCount = _habitCount,
                LastPct = _lastPct, LastDischarging = _lastDischarging, LastSeen = _lastSeen,
            }));
        }
        catch { }
    }

    /// <summary>Called on a clean exit so the next launch doesn't mistake it for a power loss.</summary>
    public void Shutdown()
    {
        _lastDischarging = false;
        Save();
    }

    public void Reset()
    {
        _heads = NewHeads();
        _cutoffPct = null; _habitWatts = null; _habitCount = 0;
        _hist.Clear(); _pending.Clear();
        if (_dir != null) try { File.Delete(System.IO.Path.Combine(_dir, "runtime_model.json")); } catch { }
    }

    private void Log(DateTime now, double pct, int mwh, double? recent, (double Ref, double[] X) ft)
    {
        if (_dir == null) return;
        try
        {
            var path = System.IO.Path.Combine(_dir, "runtime_log.csv");
            Directory.CreateDirectory(_dir);
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
            {
                var old = path + ".old"; File.Delete(old); File.Move(path, old);
            }
            string F1(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            if (!File.Exists(path))
                sb.AppendLine("time,percent,mwh,recent_w,ref_w,dev_session,dev_10min,dev_2min,c15_session,c15_10min,c15_2min,c90_session,c90_10min,c90_2min,reserve_pct");
            sb.Append(now.ToString("o", CultureInfo.InvariantCulture)).Append(',').Append(pct.ToString("F1", CultureInfo.InvariantCulture))
              .Append(',').Append(mwh).Append(',').Append(recent is { } r ? F1(r) : "")
              .Append(',').Append(F1(ft.Ref)).Append(',').Append(F1(ft.X[1])).Append(',').Append(F1(ft.X[2])).Append(',').Append(F1(ft.X[3]));
            foreach (var hz in new[] { 0, H - 1 })
                for (var i = 1; i < F; i++) sb.Append(',').Append(F1(_heads[hz].C[i]));
            sb.Append(',').Append(ReservePercent.ToString("F1", CultureInfo.InvariantCulture)).AppendLine();
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }
}
