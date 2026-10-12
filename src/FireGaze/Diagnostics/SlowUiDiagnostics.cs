using System.Diagnostics;
using System.Globalization;

namespace FireGaze.Diagnostics;

internal sealed class SlowUiDiagnostics
{
    private readonly Action<string> report;
    private readonly Func<long> ticks;
    private readonly Dictionary<string, long> lastReports = new(StringComparer.Ordinal);

    public SlowUiDiagnostics(Action<string> report, Func<long>? ticks = null)
    {
        this.report = report;
        this.ticks = ticks ?? (() => Environment.TickCount64);
    }

    public Scope Measure(string phase) => new(this, phase);

    public void Report(string phase, double elapsedMilliseconds)
    {
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 100) return;
        try
        {
            var now = ticks();
            if (lastReports.TryGetValue(phase, out var previous) && now - previous < 30_000) return;
            lastReports[phase] = now;
            report($"{phase}: {elapsedMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");
        }
        catch
        {
            // Diagnostics must not interrupt rendering when their sink is unavailable.
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly SlowUiDiagnostics owner;
        private readonly string phase;
        private readonly long started;

        public Scope(SlowUiDiagnostics owner, string phase)
        {
            this.owner = owner;
            this.phase = phase;
            started = Stopwatch.GetTimestamp();
        }

        public void Dispose() => owner.Report(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
