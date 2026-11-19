namespace Bitiq.Core.Models;

public sealed record SubtitleUpdateEvent
{
    public long SequenceId { get; init; }
    public string EnglishText { get; init; } = string.Empty;
    public string TurkishText { get; init; } = string.Empty;
    public bool IsPartial { get; init; }
    public bool IsCleared { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TelemetryRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Engine { get; init; } = string.Empty;
    public string SourceTextPreview { get; init; } = string.Empty;
    public bool CacheHit { get; init; }
    public double CaptureMs { get; init; }
    public double ChangeDetectMs { get; init; }
    public double OcrMs { get; init; }
    public double FirstTokenMs { get; init; }
    public double TotalTranslateMs { get; init; }
    public double RenderMs { get; init; }
    public double TotalEndToEndMs { get; init; }
    public int PromptTokens { get; init; }
    public int CachedTokens { get; init; }
    public double TokensPerSecond { get; init; }
}

public sealed record StageMetrics(double AverageMs, double P95Ms);

public sealed record DiagnosticsSummary(
    int SampleCount,
    int CacheHits,
    StageMetrics Capture,
    StageMetrics Ocr,
    StageMetrics FirstToken,
    StageMetrics TotalTranslate,
    StageMetrics Render,
    StageMetrics EndToEnd);
