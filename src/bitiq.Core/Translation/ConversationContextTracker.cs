using Bitiq.Core.Ocr;

namespace Bitiq.Core.Translation;

/// <summary>
/// Çevrilen ardışık replikleri histerezisli kayan pencerede tutar;
/// OCR düzeltmeleri ile birleşmiş cümleleri tekrarlamadan günceller ve sahne boşluğunda sıfırlar.
/// </summary>
public sealed class ConversationContextTracker
{
    private readonly object _lock = new();
    private readonly List<ContextLine> _lines = [];
    private long _lastCommitMs;

    public int MaxLines { get; private set; } = 8;
    public int TrimToLines { get; private set; } = 4;
    public int MaxChars { get; private set; } = 1200;
    public int IdleResetMs { get; private set; } = 90_000;

    public int Count
    {
        get
        {
            lock (_lock)
                return _lines.Count;
        }
    }

    public void Configure(int maxLines, int trimToLines, int idleResetSeconds, int maxChars = 1200)
    {
        lock (_lock)
        {
            MaxLines = Math.Clamp(maxLines, 2, 16);
            TrimToLines = Math.Clamp(trimToLines, 1, MaxLines);
            IdleResetMs = Math.Clamp(idleResetSeconds, 15, 600) * 1000;
            MaxChars = Math.Clamp(maxChars, 300, 4000);
            TrimIfNeeded();
        }
    }

    public TranslationContextSnapshot Snapshot(GameProfile? profile, string currentSource, long nowMs)
    {
        GameProfile? clonedProfile = profile is null || profile.IsEffectivelyEmpty ? null : profile.Clone();

        lock (_lock)
        {
            if (_lines.Count > 0 && _lastCommitMs > 0 && (nowMs - _lastCommitMs) > IdleResetMs)
                _lines.Clear();

            var list = _lines.ToList();

            // Birleşik cümle veya önceki gürültülü OCR okumasının yükseltilmesi durumunda son satırı bağlamdan çıkar
            if (list.Count > 0 && IsSameOrContinuation(currentSource, list[^1].Source))
                list.RemoveAt(list.Count - 1);

            return new TranslationContextSnapshot(clonedProfile, list);
        }
    }

    public int Commit(string source, string target, long nowMs)
    {
        string trimmedSrc = (source ?? string.Empty).Trim();
        string trimmedTgt = (target ?? string.Empty).Trim();

        if (!IsWorthKeeping(trimmedSrc) || string.IsNullOrWhiteSpace(trimmedTgt))
        {
            lock (_lock)
                return _lines.Count;
        }

        lock (_lock)
        {
            if (_lines.Count > 0)
            {
                string lastSource = _lines[^1].Source;
                if (IsSameOrContinuation(trimmedSrc, lastSource))
                {
                    _lines[^1] = new ContextLine(trimmedSrc, trimmedTgt);
                    _lastCommitMs = nowMs;
                    TrimIfNeeded();
                    return _lines.Count;
                }
            }

            _lines.Add(new ContextLine(trimmedSrc, trimmedTgt));
            _lastCommitMs = nowMs;
            TrimIfNeeded();
            return _lines.Count;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lines.Clear();
            _lastCommitMs = 0;
        }
    }

    private void TrimIfNeeded()
    {
        int totalChars = _lines.Sum(l => l.Source.Length + l.Target.Length);
        if (_lines.Count <= MaxLines && totalChars <= MaxChars)
            return;

        int targetCount = Math.Min(TrimToLines, _lines.Count);
        while (_lines.Count > targetCount)
            _lines.RemoveAt(0);

        while (_lines.Count > 1 && _lines.Sum(l => l.Source.Length + l.Target.Length) > MaxChars)
            _lines.RemoveAt(0);
    }

    private static bool IsSameOrContinuation(string candidateSource, string lastSource)
    {
        string candNorm = SubtitleStabilizer.NormalizeForComparison(candidateSource);
        string lastNorm = SubtitleStabilizer.NormalizeForComparison(lastSource);
        if (candNorm.Length == 0 || lastNorm.Length == 0)
            return false;

        if (SubtitleStabilizer.ComputeSimilarity(candNorm, lastNorm) >= 0.85)
            return true;

        return StartsWithSimilar(candNorm, lastNorm);
    }

    private static bool StartsWithSimilar(string textNorm, string prefixNorm)
    {
        return prefixNorm.Length > 0
            && textNorm.Length > prefixNorm.Length
            && SubtitleStabilizer.ComputeSimilarity(textNorm[..prefixNorm.Length], prefixNorm) >= 0.78;
    }

    private static bool IsWorthKeeping(string text)
    {
        int letters = 0;
        foreach (char ch in text)
        {
            if (char.IsLetter(ch))
            {
                letters++;
                if (letters >= 2)
                    return true;
            }
        }
        return false;
    }
}
