using System.Diagnostics;
using System.Text.RegularExpressions;
using Bitiq.Core.Capture;
using Bitiq.Core.Models;
using Bitiq.Core.Ocr;
using Bitiq.Core.Services;
using Bitiq.Core.Translation;

namespace Bitiq.Core.Pipeline;

public enum PipelineState
{
    Stopped,
    Initializing,
    Running,
    Error
}

/// <summary>
/// Yakalama -> Değişim Tespiti -> OCR -> Kararlılık -> Çeviri -> Ekran/OBS Yayın akışını yönetir.
/// </summary>
public sealed partial class SubtitlePipeline : IDisposable
{
    private const int IncompleteSentenceTimeoutMs = 10000;
    private const int MaxMergedSentenceLength = 220;

    private readonly WgcRegionCaptureService _captureService;
    private readonly WindowsOcrService _ocrService;
    private readonly SubtitleStabilizer _stabilizer;
    private readonly DiagnosticsTracker _diagnostics;
    private readonly Func<ITranslator> _translatorProvider;
    private readonly Func<AppSettings> _settingsProvider;

    private readonly object _pipelineLock = new();
    private readonly object _mergeLock = new();
    private CancellationTokenSource? _activeTranslationCts;
    private CancellationTokenSource? _pendingClearCts;
    private Timer? _debouncePollTimer;

    private long _sequenceCounter;
    private long _lastShownTimestampMs;
    private long _lastWindowCheckMs;
    private string _currentlyDisplayedTurkish = string.Empty;
    private string _incompleteSentenceRaw = string.Empty;
    private string _incompleteSentenceNormalized = string.Empty;
    private long _incompleteSentenceTimestampMs;
    private string _lastMergedPrefixRaw = string.Empty;
    private string _lastMergedSecondNormalized = string.Empty;
    private double _lastCaptureMs;
    private double _lastChangeDetectMs;
    private long _lastFrameTimestampTicks;
    private PipelineState _state = PipelineState.Stopped;
    private bool _disposed;

    public PipelineState State => _state;
    public event Action<SubtitleUpdateEvent>? SubtitleUpdated;
    public event Action<PipelineState, string>? StateChanged;

    public SubtitlePipeline(
        WgcRegionCaptureService captureService,
        WindowsOcrService ocrService,
        DiagnosticsTracker diagnostics,
        Func<ITranslator> translatorProvider,
        Func<AppSettings> settingsProvider)
    {
        _captureService = captureService;
        _ocrService = ocrService;
        _diagnostics = diagnostics;
        _translatorProvider = translatorProvider;
        _settingsProvider = settingsProvider;
        _stabilizer = new SubtitleStabilizer();

        _captureService.FrameChanged += OnFrameChanged;
        _captureService.ErrorOccurred += err => SetState(PipelineState.Error, err);
        _ocrService.TextRecognized += OnOcrTextRecognized;
        _ocrService.ErrorOccurred += err => SetState(PipelineState.Error, err);
    }

    public async Task StartAsync(CancellationToken ct = default, IntPtr targetWindowHwnd = default)
    {
        var settings = _settingsProvider();
        if (settings.CaptureRegion is not { IsValid: true })
            throw new InvalidOperationException("Önce 'Çevrilecek alanı seç' ile ekranda bir altyazı bölgesi seçin.");

        _ocrService.SetSourceLanguage(settings.SourceLanguage);
        if (!_ocrService.IsAvailable)
            throw new InvalidOperationException(_ocrService.Status.ErrorMessage ?? "Windows OCR paketi hazır değil.");

        if (settings.CaptureRegion.IsWindowLocked && targetWindowHwnd == IntPtr.Zero)
        {
            targetWindowHwnd = WindowEnumerationService.ResolveTargetWindow(settings.CaptureRegion);
            if (targetWindowHwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Kilitlenen hedef pencere bulunamadı ([{settings.CaptureRegion.TargetWindowProcessName}] {settings.CaptureRegion.TargetWindowTitle}). Lütfen pencerenin açık olduğundan emin olun.");
            }
        }

        if (targetWindowHwnd != IntPtr.Zero)
        {
            WindowEnumerationService.EnsureWindowRenderableInBackground(targetWindowHwnd);
        }

        SetState(PipelineState.Initializing,
            settings.TranslationEngine == TranslationEngineKind.Local
                ? "Model yükleniyor…"
                : "Çeviri motoru hazırlanıyor…");

        _stabilizer.Configure(settings.StabilityFrames, settings.TypewriterDebounceMs);
        _stabilizer.Reset();
        _ocrService.SetPreprocessEnabled(settings.OcrPreprocessEnabled);
        _ocrService.ResetGlyphCache();
        ResetIncompleteBuffer();
        Volatile.Write(ref _currentlyDisplayedTurkish, string.Empty);

        var translator = _translatorProvider();
        await translator.InitializeAsync(ct).ConfigureAwait(false);

        _captureService.Start(settings.CaptureRegion, settings.FrameChangeThreshold, targetWindowHwnd);
        _debouncePollTimer = new Timer(_ => OnDebounceTick(), null, 50, 50);

        SetState(PipelineState.Running, $"Çalışıyor ({translator.EngineName})");
    }

    public void Stop()
    {
        _debouncePollTimer?.Dispose();
        _debouncePollTimer = null;

        _captureService.Stop();
        CancelActiveTranslation();
        CancelPendingClear();
        _stabilizer.Reset();
        _ocrService.ResetGlyphCache();
        ResetIncompleteBuffer();

        EmitClear();
        SetState(PipelineState.Stopped, "Hazır");
    }

    public void ApplyLiveSettings(AppSettings settings)
    {
        if (!string.Equals(_ocrService.ActiveSourceLanguageCode, settings.SourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            _ocrService.SetSourceLanguage(settings.SourceLanguage);
            _stabilizer.Reset();
            ResetIncompleteBuffer();
        }

        _stabilizer.Configure(settings.StabilityFrames, settings.TypewriterDebounceMs);
        _ocrService.SetPreprocessEnabled(settings.OcrPreprocessEnabled);
        _captureService.UpdateChangeThreshold(settings.FrameChangeThreshold);
        if (!settings.MergeIncompleteSentencesEnabled)
            ResetIncompleteBuffer();

        if (settings.CaptureRegion is { IsValid: true } region && _captureService.IsRunning)
        {
            _captureService.UpdateRegion(region);
        }
    }

    public void SubmitCapturedFrame(CapturedFrameData frame) => OnFrameChanged(frame);

    private void OnFrameChanged(CapturedFrameData frame)
    {
        if (_state != PipelineState.Running)
            return;

        _lastCaptureMs = frame.CaptureMs;
        _lastChangeDetectMs = frame.ChangeDetectMs;
        _lastFrameTimestampTicks = frame.CapturedAtTimestamp;
        _ocrService.EnqueueFrame(frame);
    }

    private void OnOcrTextRecognized(OcrFrameResult ocrResult)
    {
        if (_state != PipelineState.Running)
            return;

        long nowMs = Environment.TickCount64;
        var decision = _stabilizer.Process(ocrResult.Text, nowMs);
        HandleDecision(decision, ocrResult.OcrMs, ocrResult.CapturedAtTimestamp);
    }

    private void OnDebounceTick()
    {
        if (_state != PipelineState.Running)
            return;

        long nowMs = Environment.TickCount64;
        if (nowMs - _lastWindowCheckMs >= 1000)
        {
            _lastWindowCheckMs = nowMs;
            IntPtr targetHwnd = _captureService.ActiveTargetWindowHwnd;
            if (targetHwnd != IntPtr.Zero && WindowEnumerationService.IsWindowMinimized(targetHwnd))
            {
                WindowEnumerationService.EnsureWindowRenderableInBackground(targetHwnd);
            }
        }

        var settings = _settingsProvider();
        if (settings.TypewriterDebounceMs <= 0)
            return;

        var decision = _stabilizer.PollDebounce(nowMs);
        if (decision.Action != StabilizerAction.None)
        {
            HandleDecision(decision, 0, Stopwatch.GetTimestamp());
        }
    }

    private void HandleDecision(StabilizerDecision decision, double ocrMs, long capturedTicks)
    {
        if (decision.Action == StabilizerAction.None)
        {
            // Aynı altyazı okunmaya devam ediyorsa bekleyen linger temizlemesini iptal et
            if (!string.IsNullOrEmpty(decision.NormalizedText))
                CancelPendingClear();
            return;
        }

        if (decision.Action == StabilizerAction.Clear)
        {
            ScheduleClearWithLinger();
            return;
        }

        // Yeni metin geldi: bekleyen temizleme zamanlayıcısını ve sürmekte olan çeviriyi derhal iptal et
        CancelPendingClear();
        CancelActiveTranslation();

        var settings = _settingsProvider();
        string sourceToTranslate = ResolveTextForTranslation(
            decision.RawText,
            decision.NormalizedText,
            Environment.TickCount64,
            settings.MergeIncompleteSentencesEnabled);

        var cts = new CancellationTokenSource();
        long seqId = Interlocked.Increment(ref _sequenceCounter);

        lock (_pipelineLock)
        {
            _activeTranslationCts = cts;
        }

        _ = RunTranslationAsync(
            seqId,
            sourceToTranslate,
            ocrMs,
            _lastCaptureMs,
            _lastChangeDetectMs,
            capturedTicks,
            cts.Token);
    }

    private async Task RunTranslationAsync(
        long seqId,
        string sourceText,
        double ocrMs,
        double captureMs,
        double changeDetectMs,
        long capturedTicks,
        CancellationToken ct)
    {
        var settings = _settingsProvider();
        var translator = _translatorProvider();
        double renderMs = 0;

        if (string.Equals(settings.SourceLanguage, settings.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref _currentlyDisplayedTurkish, sourceText);
            _lastShownTimestampMs = Environment.TickCount64;
            SubtitleUpdated?.Invoke(new SubtitleUpdateEvent
            {
                SequenceId = seqId,
                EnglishText = sourceText,
                TurkishText = sourceText,
                IsPartial = false,
                IsCleared = false
            });
            return;
        }

        try
        {
            var result = await translator.TranslateAsync(
                sourceText,
                settings.SourceLanguage,
                settings.TargetLanguage,
                stream: true,
                progress =>
                {
                    if (ct.IsCancellationRequested)
                        return;

                    // Daktilo (akışlı) gösterim kapalıysa yalnızca tam (final) cümleyi tek seferde anında göster
                    if (!settings.StreamingEnabled && !progress.IsFinal)
                        return;

                    // Ekranda dolu bir altyazı varken ilk 1-2 harflik streaming parçasının eski cümleyi silmesini önle
                    string currentDisplayed = Volatile.Read(ref _currentlyDisplayedTurkish);
                    if (!progress.IsFinal
                        && !string.IsNullOrWhiteSpace(currentDisplayed)
                        && progress.PartialText.Trim().Length < 6)
                    {
                        return;
                    }

                    var swRender = Stopwatch.StartNew();
                    _lastShownTimestampMs = Environment.TickCount64;
                    Volatile.Write(ref _currentlyDisplayedTurkish, progress.PartialText);
                    SubtitleUpdated?.Invoke(new SubtitleUpdateEvent
                    {
                        SequenceId = seqId,
                        EnglishText = sourceText,
                        TurkishText = progress.PartialText,
                        IsPartial = !progress.IsFinal,
                        IsCleared = false
                    });
                    swRender.Stop();
                    renderMs = swRender.Elapsed.TotalMilliseconds;
                },
                ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || result.WasCancelled)
                return;

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                SetState(PipelineState.Running, result.ErrorMessage);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.TranslatedText))
                return;

            Volatile.Write(ref _currentlyDisplayedTurkish, result.TranslatedText);

            double endToEndMs = (Stopwatch.GetTimestamp() - capturedTicks) * 1000.0 / Stopwatch.Frequency;
            if (endToEndMs < 0 || endToEndMs > 60000)
                endToEndMs = captureMs + ocrMs + result.TotalMs + renderMs;

            _diagnostics.Add(new TelemetryRecord
            {
                Engine = translator.EngineName,
                SourceTextPreview = sourceText.Length > 48 ? sourceText[..48] + "…" : sourceText,
                CacheHit = result.FromCache,
                CaptureMs = Math.Round(captureMs, 1),
                ChangeDetectMs = Math.Round(changeDetectMs, 2),
                OcrMs = Math.Round(ocrMs, 1),
                FirstTokenMs = Math.Round(result.FirstTokenMs, 1),
                TotalTranslateMs = Math.Round(result.TotalMs, 1),
                RenderMs = Math.Round(renderMs, 1),
                TotalEndToEndMs = Math.Round(endToEndMs, 1)
            });

            SetState(PipelineState.Running, $"Çalışıyor ({translator.EngineName})");
        }
        catch (OperationCanceledException)
        {
            // Yeni altyazı geldiği için önceki istek iptal edildi
        }
        catch (Exception ex)
        {
            SetState(PipelineState.Running, $"Çeviri uyarısı: {ex.Message}");
        }
    }

    private void ScheduleClearWithLinger()
    {
        CancelPendingClear();

        var style = _settingsProvider().ActiveStyle;
        long elapsedSinceShown = Environment.TickCount64 - _lastShownTimestampMs;
        int minRemaining = Math.Max(0, style.MinDisplayDurationMs - (int)elapsedSinceShown);
        int delayMs = Math.Max(style.LingerAfterDisappearMs, minRemaining);

        if (delayMs <= 0)
        {
            EmitClear();
            return;
        }

        var clearCts = new CancellationTokenSource();
        lock (_pipelineLock)
        {
            _pendingClearCts = clearCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, clearCts.Token).ConfigureAwait(false);
                if (!clearCts.IsCancellationRequested)
                    EmitClear();
            }
            catch (OperationCanceledException) { }
        }, clearCts.Token);
    }

    private void EmitClear()
    {
        Volatile.Write(ref _currentlyDisplayedTurkish, string.Empty);
        long seqId = Interlocked.Increment(ref _sequenceCounter);
        SubtitleUpdated?.Invoke(new SubtitleUpdateEvent
        {
            SequenceId = seqId,
            EnglishText = string.Empty,
            TurkishText = string.Empty,
            IsPartial = false,
            IsCleared = true
        });
    }

    private void CancelActiveTranslation()
    {
        lock (_pipelineLock)
        {
            if (_activeTranslationCts is not null)
            {
                try { _activeTranslationCts.Cancel(); } catch { }
                _activeTranslationCts.Dispose();
                _activeTranslationCts = null;
            }
        }
    }

    private void CancelPendingClear()
    {
        lock (_pipelineLock)
        {
            if (_pendingClearCts is not null)
            {
                try { _pendingClearCts.Cancel(); } catch { }
                _pendingClearCts.Dispose();
                _pendingClearCts = null;
            }
        }
    }

    private void SetState(PipelineState newState, string statusMessage)
    {
        _state = newState;
        StateChanged?.Invoke(newState, statusMessage);
    }

    private void ResetIncompleteBuffer()
    {
        lock (_mergeLock)
        {
            _incompleteSentenceRaw = string.Empty;
            _incompleteSentenceNormalized = string.Empty;
            _incompleteSentenceTimestampMs = 0;
            _lastMergedPrefixRaw = string.Empty;
            _lastMergedSecondNormalized = string.Empty;
        }
    }

    private string ResolveTextForTranslation(string rawText, string normalizedText, long nowMs, bool mergeEnabled)
    {
        if (!mergeEnabled)
        {
            ResetIncompleteBuffer();
            return rawText;
        }

        lock (_mergeLock)
        {
            // Aynı ikinci parça OCR gürültüsü nedeniyle kısa süre içinde tekrar gelirse aynı önekle birleştir
            if (!string.IsNullOrEmpty(_lastMergedSecondNormalized)
                && !string.IsNullOrEmpty(_lastMergedPrefixRaw)
                && _stabilizer.IsSameSubtitle(_lastMergedSecondNormalized, normalizedText))
            {
                string nextPart = StripSameSpeakerIfRepeated(_lastMergedPrefixRaw, rawText).TrimStart();
                return $"{_lastMergedPrefixRaw.TrimEnd()} {nextPart}";
            }

            if (!string.IsNullOrEmpty(_incompleteSentenceRaw)
                && (nowMs - _incompleteSentenceTimestampMs) <= IncompleteSentenceTimeoutMs)
            {
                // İlk karede 1. satır okunduktan hemen sonra tüm çoklu-satır birlikte okunursa 1. satırı iki kez ekleme
                if (!string.IsNullOrEmpty(_incompleteSentenceNormalized)
                    && normalizedText.Length > _incompleteSentenceNormalized.Length
                    && SubtitleStabilizer.ComputeSimilarity(
                        normalizedText[.._incompleteSentenceNormalized.Length],
                        _incompleteSentenceNormalized) >= 0.78)
                {
                    _lastMergedPrefixRaw = string.Empty;
                    _lastMergedSecondNormalized = string.Empty;
                    if (IsSentenceComplete(rawText))
                    {
                        _incompleteSentenceRaw = string.Empty;
                        _incompleteSentenceNormalized = string.Empty;
                        _incompleteSentenceTimestampMs = 0;
                    }
                    else
                    {
                        _incompleteSentenceRaw = rawText;
                        _incompleteSentenceNormalized = normalizedText;
                        _incompleteSentenceTimestampMs = nowMs;
                    }
                    return rawText;
                }

                bool nextHasSpeaker = TryExtractSpeakerPrefix(rawText, out string nextSpeaker);
                bool prevHasSpeaker = TryExtractSpeakerPrefix(_incompleteSentenceRaw, out string prevSpeaker);
                bool allowMerge = !nextHasSpeaker
                    || (prevHasSpeaker && SubtitleStabilizer.ComputeSimilarity(
                            SubtitleStabilizer.NormalizeForComparison(prevSpeaker),
                            SubtitleStabilizer.NormalizeForComparison(nextSpeaker)) >= 0.70);

                if (allowMerge && !_stabilizer.IsSameSubtitle(_incompleteSentenceNormalized, normalizedText))
                {
                    string continuation = StripSameSpeakerIfRepeated(_incompleteSentenceRaw, rawText).TrimStart();
                    string combined = $"{_incompleteSentenceRaw.TrimEnd()} {continuation}";

                    if (IsSentenceComplete(rawText))
                    {
                        _lastMergedPrefixRaw = _incompleteSentenceRaw;
                        _lastMergedSecondNormalized = normalizedText;
                        _incompleteSentenceRaw = string.Empty;
                        _incompleteSentenceNormalized = string.Empty;
                        _incompleteSentenceTimestampMs = 0;
                    }
                    else if (combined.Length <= MaxMergedSentenceLength)
                    {
                        _incompleteSentenceRaw = combined;
                        _incompleteSentenceNormalized = SubtitleStabilizer.NormalizeForComparison(combined);
                        _incompleteSentenceTimestampMs = nowMs;
                        _lastMergedPrefixRaw = string.Empty;
                        _lastMergedSecondNormalized = string.Empty;
                    }
                    else
                    {
                        _incompleteSentenceRaw = rawText;
                        _incompleteSentenceNormalized = normalizedText;
                        _incompleteSentenceTimestampMs = nowMs;
                        _lastMergedPrefixRaw = string.Empty;
                        _lastMergedSecondNormalized = string.Empty;
                        return rawText;
                    }

                    return combined;
                }

                // Tamamlanmamış cümlenin temiz/yükseltilmiş okuması geldiyse tamponu güncelle
                if (_stabilizer.IsSameSubtitle(_incompleteSentenceNormalized, normalizedText))
                {
                    _incompleteSentenceRaw = rawText;
                    _incompleteSentenceNormalized = normalizedText;
                    _incompleteSentenceTimestampMs = nowMs;
                    return rawText;
                }
            }

            _lastMergedPrefixRaw = string.Empty;
            _lastMergedSecondNormalized = string.Empty;

            if (!IsSentenceComplete(rawText))
            {
                _incompleteSentenceRaw = rawText;
                _incompleteSentenceNormalized = normalizedText;
                _incompleteSentenceTimestampMs = nowMs;
            }
            else
            {
                _incompleteSentenceRaw = string.Empty;
                _incompleteSentenceNormalized = string.Empty;
                _incompleteSentenceTimestampMs = 0;
            }

            return rawText;
        }
    }

    public static bool IsSentenceComplete(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string trimmed = text.TrimEnd(' ', '\t', '\r', '\n', '\'', '"', '\u2019', '\u201D', ')');
        if (trimmed.Length == 0)
            return true;

        char last = trimmed[^1];
        if (last is '.' or '!' or '?' or '\u2026' or ':')
            return true;

        if (trimmed.EndsWith("--", StringComparison.Ordinal) || last is '\u2014' or '\u2013')
            return true;

        return false;
    }

    private static bool TryExtractSpeakerPrefix(string text, out string speaker)
    {
        var match = SpeakerPrefixRegex().Match(text.TrimStart());
        if (match.Success)
        {
            speaker = match.Groups[1].Value;
            return true;
        }

        speaker = string.Empty;
        return false;
    }

    private static string StripSameSpeakerIfRepeated(string prevRaw, string nextRaw)
    {
        if (!TryExtractSpeakerPrefix(prevRaw, out string prevSpeaker))
            return nextRaw;

        string trimmedNext = nextRaw.TrimStart();
        var match = SpeakerPrefixRegex().Match(trimmedNext);
        if (!match.Success)
            return nextRaw;

        string nextSpeaker = match.Groups[1].Value;
        double sim = SubtitleStabilizer.ComputeSimilarity(
            SubtitleStabilizer.NormalizeForComparison(prevSpeaker),
            SubtitleStabilizer.NormalizeForComparison(nextSpeaker));

        return sim >= 0.70
            ? trimmedNext[match.Length..].TrimStart()
            : nextRaw;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    [GeneratedRegex(@"^([A-Za-z\u00C0-\u00D6\u00D8-\u00FF]{2,16}):\s*")]
    private static partial Regex SpeakerPrefixRegex();
}
