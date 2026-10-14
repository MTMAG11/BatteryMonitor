using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace BatteryMonitor.Services;

/// <summary>
/// Reads Windows' own battery history (<c>powercfg /batteryreport</c>, no admin needed) to give a steadier
/// full-charge capacity and health figure than a single gauge reading, a rough wear trend, and a
/// long-term "typical draw on battery" figure to start the time-left model from.
/// </summary>
public sealed class HealthHistory
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/battery/2012";

    public sealed record Entry(DateTime Start, DateTime End, int Design, int Full, double? DcEnergyMwh, double? DcHours);

    private class State { public List<Entry> Entries { get; set; } = new(); public DateTime LastRun { get; set; } }

    private readonly string? _path;
    private List<Entry> _entries = new();
    private DateTime _lastRun;
    private int _refreshing;

    /// <summary>Median full-charge capacity over about the last three weeks (mWh), if there is enough data.</summary>
    public int? SmoothedFullMwh { get; private set; }
    public int? DesignMwh { get; private set; }
    /// <summary>Long-term average draw while on battery (W), from the report's active-on-battery energy and time.</summary>
    public double? HabitWatts { get; private set; }
    /// <summary>Linear wear trend in percentage points of design capacity per 30 days, once 30+ days exist.</summary>
    public double? TrendPercentPer30Days { get; private set; }
    public int DaysCovered { get; private set; }

    public HealthHistory(string? dataDirectory)
    {
        _path = dataDirectory == null ? null : System.IO.Path.Combine(dataDirectory, "health.json");
        try
        {
            if (_path != null && File.Exists(_path) && JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) is { } s)
            {
                _entries = s.Entries; _lastRun = s.LastRun;
                Recompute();
            }
        }
        catch { }
    }

    public bool NeedsRefresh => DateTime.UtcNow - _lastRun > TimeSpan.FromHours(12);

    /// <summary>Runs the report and updates the figures. Safe to call from a background thread; ignores overlapping calls.</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"batteryreport_{Guid.NewGuid():N}.xml");
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg", Arguments = $"/batteryreport /xml /output \"{tmp}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (p == null) return;
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                if (!p.WaitForExit(30_000) || !File.Exists(tmp)) return;

                var parsed = Parse(XDocument.Load(tmp));
                if (parsed.Count == 0) return;

                // Merge by start date, newest data wins.
                var byStart = _entries.ToDictionary(e => e.Start);
                foreach (var e in parsed) byStart[e.Start] = e;
                _entries = byStart.Values.Where(e => e.End > DateTime.UtcNow.AddDays(-400)).OrderBy(e => e.Start).ToList();
                _lastRun = DateTime.UtcNow;
                Recompute();
                Save();
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch { }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    private static List<Entry> Parse(XDocument doc)
    {
        var list = new List<Entry>();
        foreach (var e in doc.Descendants(Ns + "HistoryEntry"))
        {
            try
            {
                var start = DateTime.Parse((string)e.Attribute("StartDate")!, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                var end = DateTime.Parse((string)e.Attribute("EndDate")!, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                var design = (int?)e.Attribute("DesignCapacity") ?? 0;
                var full = (int?)e.Attribute("FullChargeCapacity") ?? 0;
                double? energy = (double?)e.Attribute("ActiveDcEnergy");
                double? hours = e.Attribute("ActiveDcTime") is { } a ? XmlConvert.ToTimeSpan(a.Value).TotalHours : null;
                list.Add(new Entry(start, end, design, full, energy, hours));
            }
            catch { }
        }
        return list;
    }

    private void Recompute()
    {
        var now = DateTime.UtcNow;
        DesignMwh = _entries.LastOrDefault(e => e.Design > 0)?.Design;
        DaysCovered = _entries.Count == 0 ? 0 : (int)(_entries[^1].End - _entries[0].Start).TotalDays;

        // Steadier capacity: median of the short (≤ 3-day) entries in the last three weeks.
        var recent = _entries.Where(e => e.Full > 0 && (e.End - e.Start).TotalDays <= 3 && e.End > now.AddDays(-21))
                             .Select(e => e.Full).OrderBy(x => x).ToList();
        SmoothedFullMwh = recent.Count >= 3 ? recent[recent.Count / 2] : null;

        // Typical draw on battery over the last 30 days.
        var dc = _entries.Where(e => e.DcEnergyMwh is > 0 && e.DcHours is >= 0.25 && e.End > now.AddDays(-30)).ToList();
        var hrs = dc.Sum(e => e.DcHours!.Value);
        HabitWatts = hrs >= 1 ? dc.Sum(e => e.DcEnergyMwh!.Value) / 1000.0 / hrs : null;

        // Wear trend: least-squares slope of full capacity over time (needs a month of history; very noisy).
        TrendPercentPer30Days = null;
        var pts = _entries.Where(e => e.Full > 0 && e.Design > 0 && (e.End - e.Start).TotalDays <= 8)
                          .Select(e => (X: (e.Start - _entries[0].Start).TotalDays, Y: 100.0 * e.Full / e.Design)).ToList();
        if (pts.Count >= 10 && pts.Max(p => p.X) - pts.Min(p => p.X) >= 30)
        {
            var mx = pts.Average(p => p.X); var my = pts.Average(p => p.Y);
            var sxx = pts.Sum(p => (p.X - mx) * (p.X - mx));
            if (sxx > 0) TrendPercentPer30Days = 30 * pts.Sum(p => (p.X - mx) * (p.Y - my)) / sxx;
        }
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new State { Entries = _entries, LastRun = _lastRun }));
        }
        catch { }
    }
}
