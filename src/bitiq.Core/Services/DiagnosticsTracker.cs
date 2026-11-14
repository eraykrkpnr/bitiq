using Bitiq.Core.Models;

namespace Bitiq.Core.Services;

public sealed class DiagnosticsTracker
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Queue<TelemetryRecord> _records = new();

    public event Action<TelemetryRecord>? RecordAdded;

    public DiagnosticsTracker(int capacity = 50)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Add(TelemetryRecord record)
    {
        lock (_lock)
        {
            if (_records.Count >= _capacity)
                _records.Dequeue();
            _records.Enqueue(record);
        }

        RecordAdded?.Invoke(record);
    }

    public IReadOnlyList<TelemetryRecord> GetSnapshot()
    {
        lock (_lock)
        {
            return _records.ToArray();
        }
    }

    public DiagnosticsSummary GetSummary()
    {
        TelemetryRecord[] items;
        lock (_lock)
        {
            items = _records.ToArray();
        }

        if (items.Length == 0)
        {
            var empty = new StageMetrics(0, 0);
            return new DiagnosticsSummary(0, 0, empty, empty, empty, empty, empty, empty);
        }

        return new DiagnosticsSummary(
            SampleCount: items.Length,
            CacheHits: items.Count(r => r.CacheHit),
            Capture: ComputeMetrics(items.Select(r => r.CaptureMs)),
            Ocr: ComputeMetrics(items.Select(r => r.OcrMs)),
            FirstToken: ComputeMetrics(items.Select(r => r.FirstTokenMs)),
            TotalTranslate: ComputeMetrics(items.Select(r => r.TotalTranslateMs)),
            Render: ComputeMetrics(items.Select(r => r.RenderMs)),
            EndToEnd: ComputeMetrics(items.Select(r => r.TotalEndToEndMs)));
    }

    public void Clear()
    {
        lock (_lock)
        {
            _records.Clear();
        }
    }

    private static StageMetrics ComputeMetrics(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return new StageMetrics(0, 0);

        var avg = sorted.Average();
        var p95Index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
        p95Index = Math.Clamp(p95Index, 0, sorted.Length - 1);
        return new StageMetrics(Math.Round(avg, 1), Math.Round(sorted[p95Index], 1));
    }
}
