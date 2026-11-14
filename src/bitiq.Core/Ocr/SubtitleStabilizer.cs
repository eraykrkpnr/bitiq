using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bitiq.Core.Ocr;

public enum StabilizerAction
{
    None,
    Translate,
    Clear
}

public readonly record struct StabilizerDecision(StabilizerAction Action, string NormalizedText, string RawText);

/// <summary>
/// OCR metnini saf karşılaştırma iskeletine indirger, arka plan hareketinden kaynaklanan
/// OCR gürültüsünü ve tek karelik boş okumaları bastırır; 1/2/3 kare kararlılık ve debounce uygular.
/// </summary>
public sealed partial class SubtitleStabilizer
{
    public static volatile bool IsEnglishSourceMode = true;

    private readonly double _similarityThreshold;
    private int _requiredFrames;
    private int _debounceMs;
    private int _requiredEmptyFrames;
    private int _minEmptyDurationMs;

    private string _candidateText = string.Empty;
    private string _candidateNormalized = string.Empty;
    private int _candidateCount;
    private long _candidateLastChangedMs;

    private string _lastCommittedText = string.Empty;
    private string _lastCommittedNormalized = string.Empty;
    private string[] _lastCommittedLinesNormalized = [];
    private string _lastDisplayedText = string.Empty;
    private string _lastDisplayedNormalized = string.Empty;
    private string[] _lastDisplayedLinesNormalized = [];
    private string _upgradeCandidateText = string.Empty;
    private int _upgradeCandidateCount;
    private long _lastClearedMs;
    private bool _hasActiveSubtitle;

    private int _emptyFrameCount;
    private long _emptySinceMs;

    public SubtitleStabilizer(
        int requiredFrames = 1,
        int debounceMs = 0,
        double similarityThreshold = 0.90,
        int requiredEmptyFrames = 1,
        int minEmptyDurationMs = 0)
    {
        _requiredFrames = Math.Clamp(requiredFrames, 1, 3);
        _debounceMs = Math.Clamp(debounceMs, 0, 2000);
        _similarityThreshold = Math.Clamp(similarityThreshold, 0.5, 1.0);
        _requiredEmptyFrames = Math.Max(1, requiredEmptyFrames);
        _minEmptyDurationMs = Math.Max(0, minEmptyDurationMs);
    }

    public void Configure(int requiredFrames, int debounceMs)
    {
        _requiredFrames = Math.Clamp(requiredFrames, 1, 3);
        _debounceMs = Math.Clamp(debounceMs, 0, 2000);
        _requiredEmptyFrames = Math.Max(2, _requiredFrames);
        _minEmptyDurationMs = 150;
    }

    public void Reset()
    {
        _candidateText = string.Empty;
        _candidateNormalized = string.Empty;
        _candidateCount = 0;
        _candidateLastChangedMs = 0;
        _lastCommittedText = string.Empty;
        _lastCommittedNormalized = string.Empty;
        _lastCommittedLinesNormalized = [];
        _lastDisplayedText = string.Empty;
        _lastDisplayedNormalized = string.Empty;
        _lastDisplayedLinesNormalized = [];
        _upgradeCandidateText = string.Empty;
        _upgradeCandidateCount = 0;
        _lastClearedMs = 0;
        _hasActiveSubtitle = false;
        _emptyFrameCount = 0;
        _emptySinceMs = 0;
    }

    public StabilizerDecision Process(string rawOcrText, long nowMs)
    {
        string cleaned = CleanRawText(rawOcrText);
        string normalized = NormalizeForComparison(cleaned);

        if (string.IsNullOrEmpty(normalized))
        {
            if (_hasActiveSubtitle)
            {
                if (_emptyFrameCount == 0)
                    _emptySinceMs = nowMs;
                _emptyFrameCount++;

                // Tek karelik boş okuma veya kısa sahne parlamasında mevcut altyazıyı unutma
                if (_emptyFrameCount < _requiredEmptyFrames || (nowMs - _emptySinceMs) < _minEmptyDurationMs)
                {
                    return new StabilizerDecision(StabilizerAction.None, string.Empty, string.Empty);
                }

                _lastDisplayedText = _lastCommittedText;
                _lastDisplayedNormalized = _lastCommittedNormalized;
                _lastDisplayedLinesNormalized = _lastCommittedLinesNormalized;
                _lastClearedMs = nowMs;
                _hasActiveSubtitle = false;
                _lastCommittedText = string.Empty;
                _lastCommittedNormalized = string.Empty;
                _lastCommittedLinesNormalized = [];
                _upgradeCandidateText = string.Empty;
                _upgradeCandidateCount = 0;
                _emptyFrameCount = 0;
                _emptySinceMs = 0;
                _candidateText = string.Empty;
                _candidateNormalized = string.Empty;
                _candidateCount = 0;
                _candidateLastChangedMs = nowMs;

                return new StabilizerDecision(StabilizerAction.Clear, string.Empty, string.Empty);
            }

            _candidateText = string.Empty;
            _candidateNormalized = string.Empty;
            _candidateCount = 0;
            _candidateLastChangedMs = nowMs;
            return new StabilizerDecision(StabilizerAction.None, string.Empty, string.Empty);
        }

        _emptyFrameCount = 0;
        _emptySinceMs = 0;

        // Aktif çok/tek satırlı altyazı ile aynıysa veya onun bir alt-satırıysa OCR gürültüsü nedeniyle yeniden çevirme
        if (_hasActiveSubtitle && IsCoveredByActiveSubtitle(_lastCommittedNormalized, _lastCommittedLinesNormalized, normalized))
        {
            // Eğer aktif altyazının belirgin şekilde daha temiz (daha az OCR bozulmalı) bir okuması geldiyse yükselt
            if (!string.Equals(normalized, _lastCommittedNormalized, StringComparison.Ordinal)
                && IsSameSubtitle(_lastCommittedNormalized, normalized)
                && IsCleanerReading(cleaned, _lastCommittedText))
            {
                if (string.Equals(_upgradeCandidateText, cleaned, StringComparison.Ordinal))
                    _upgradeCandidateCount++;
                else
                {
                    _upgradeCandidateText = cleaned;
                    _upgradeCandidateCount = 1;
                }

                ComputeWordQuality(cleaned, out int upClean, out int upGarbage, out _);
                int requiredUpgradeFrames = (upGarbage == 0 && upClean >= 2) ? 1 : 2;
                if (_upgradeCandidateCount >= requiredUpgradeFrames)
                {
                    _lastCommittedText = cleaned;
                    _lastCommittedNormalized = normalized;
                    _lastDisplayedNormalized = normalized;
                    _lastCommittedLinesNormalized = ExtractNormalizedLines(cleaned);
                    _lastDisplayedLinesNormalized = _lastCommittedLinesNormalized;
                    _upgradeCandidateText = string.Empty;
                    _upgradeCandidateCount = 0;
                    _candidateText = string.Empty;
                    _candidateNormalized = string.Empty;
                    _candidateCount = 0;
                    return new StabilizerDecision(StabilizerAction.Translate, normalized, cleaned);
                }

                return new StabilizerDecision(StabilizerAction.None, normalized, cleaned);
            }

            _upgradeCandidateText = string.Empty;
            _upgradeCandidateCount = 0;
            _candidateText = string.Empty;
            _candidateNormalized = string.Empty;
            _candidateCount = 0;
            return new StabilizerDecision(StabilizerAction.None, normalized, cleaned);
        }

        // Linger süresi içinde veya henüz temizlenmişken aynı cümle (veya alt satırı) geri geldiyse yeniden çevirme
        // Kısa ünlemler ("Wooo!", "No!") ardışık tekrarda yutulmasın diye 450 ms ile sınırla
        int reuseWindowMs = normalized.Length <= 6 ? 450 : 1500;
        if (!_hasActiveSubtitle
            && !string.IsNullOrEmpty(_lastDisplayedNormalized)
            && (nowMs - _lastClearedMs) <= reuseWindowMs
            && IsCoveredByActiveSubtitle(_lastDisplayedNormalized, _lastDisplayedLinesNormalized, normalized))
        {
            _hasActiveSubtitle = true;
            _lastCommittedText = _lastDisplayedText;
            _lastCommittedNormalized = _lastDisplayedNormalized;
            _lastCommittedLinesNormalized = _lastDisplayedLinesNormalized;
            _upgradeCandidateText = string.Empty;
            _upgradeCandidateCount = 0;
            _candidateText = string.Empty;
            _candidateNormalized = string.Empty;
            _candidateCount = 0;
            return new StabilizerDecision(StabilizerAction.None, normalized, cleaned);
        }

        // Aday metin ile aynı mı?
        if (_candidateCount > 0 && IsSameSubtitle(_candidateNormalized, normalized))
        {
            _candidateCount++;
            // Daha uzun / daha eksiksiz satırı tercih et
            if (cleaned.Length >= _candidateText.Length)
            {
                _candidateText = cleaned;
                _candidateNormalized = normalized;
            }
        }
        else
        {
            _candidateText = cleaned;
            _candidateNormalized = normalized;
            _candidateCount = 1;
            _candidateLastChangedMs = nowMs;
        }

        return TryCommit(nowMs);
    }

    /// <summary>
    /// Daktilo debounce süresi dolduysa zamanlayıcı tick'inde bekleyen adayı onaylar.
    /// </summary>
    public StabilizerDecision PollDebounce(long nowMs)
    {
        if (string.IsNullOrEmpty(_candidateNormalized))
            return new StabilizerDecision(StabilizerAction.None, string.Empty, string.Empty);

        if (_hasActiveSubtitle && IsCoveredByActiveSubtitle(_lastCommittedNormalized, _lastCommittedLinesNormalized, _candidateNormalized))
            return new StabilizerDecision(StabilizerAction.None, string.Empty, string.Empty);

        return TryCommit(nowMs);
    }

    private StabilizerDecision TryCommit(long nowMs)
    {
        int effectiveRequired = _requiredFrames;
        ComputeWordQuality(_candidateText, out int candClean, out int candGarbage, out _);
        bool hasTerminalPunctuation = EndsWithSentencePunctuation(_candidateText);

        // Tek karelik <=3 karakter noktalama işaretsiz parçacıkları ("Wi") doğrulanana kadar beklet;
        // temiz yeni cümleleri ise aktif altyazı varken bile geciktirme.
        if (candClean <= 1 && candGarbage == 0 && !hasTerminalPunctuation && _candidateNormalized.Length <= 3)
        {
            effectiveRequired = Math.Max(effectiveRequired, 2);
        }
        else if (_hasActiveSubtitle && _requiredFrames == 1)
        {
            bool isCleanNewSubtitle = candGarbage == 0 && (candClean >= 2 || (candClean == 1 && hasTerminalPunctuation));
            if (!isCleanNewSubtitle)
                effectiveRequired = 2;
        }

        if (_candidateCount < effectiveRequired)
            return new StabilizerDecision(StabilizerAction.None, string.Empty, string.Empty);

        if (_debounceMs > 0 && (nowMs - _candidateLastChangedMs) < _debounceMs)
            return new StabilizerDecision(StabilizerAction.None, string.Empty, string.Empty);

        _lastCommittedText = _candidateText;
        _lastCommittedNormalized = _candidateNormalized;
        _lastDisplayedText = _candidateText;
        _lastDisplayedNormalized = _candidateNormalized;
        _lastCommittedLinesNormalized = ExtractNormalizedLines(_candidateText);
        _lastDisplayedLinesNormalized = _lastCommittedLinesNormalized;
        _upgradeCandidateText = string.Empty;
        _upgradeCandidateCount = 0;
        _hasActiveSubtitle = true;
        _candidateCount = 0;
        return new StabilizerDecision(StabilizerAction.Translate, _candidateNormalized, _candidateText);
    }

    private bool IsCoveredByActiveSubtitle(string activeNormalized, string[] activeLinesNormalized, string candidateNormalized)
    {
        if (IsSameSubtitle(activeNormalized, candidateNormalized))
            return true;

        // Satır düşmesi / kısmi kapanma koruması: aktif altyazı daha uzunken yalnızca alt satır/parça okunmuşsa koru
        if (candidateNormalized.Length <= activeNormalized.Length - 3)
        {
            foreach (string lineNorm in activeLinesNormalized)
            {
                if (!string.IsNullOrEmpty(lineNorm) && IsSameSubtitle(lineNorm, candidateNormalized))
                    return true;
            }

            if (candidateNormalized.Length >= 8)
            {
                if (activeNormalized.Length >= candidateNormalized.Length + 6
                    && activeNormalized.Contains(candidateNormalized, StringComparison.Ordinal))
                {
                    return true;
                }

                string head = activeNormalized[..candidateNormalized.Length];
                string tail = activeNormalized[^candidateNormalized.Length..];
                if (ComputeSimilarity(head, candidateNormalized) >= 0.80
                    || ComputeSimilarity(tail, candidateNormalized) >= 0.80)
                {
                    return true;
                }
            }

            if (ComputeWordSubsetCoverage(candidateNormalized, activeNormalized, out int shortWordCount, out int matchedWords) >= 0.65
                && shortWordCount >= 2
                && matchedWords >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EndsWithSentencePunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.TrimEnd(' ', '\t', '"', '\'', '\u2019', '\u201D', ')');
        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or '\u2026';
    }

    private static string[] ExtractNormalizedLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var raw = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(raw.Length);
        foreach (string line in raw)
        {
            string n = NormalizeForComparison(line);
            if (!string.IsNullOrEmpty(n))
                result.Add(n);
        }
        return [.. result];
    }

    public bool IsSameSubtitle(string a, string b)
        => IsSameSubtitleStatic(a, b, _similarityThreshold);

    private static bool IsSameSubtitleStatic(string a, string b, double configuredThreshold)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        int maxLen = Math.Max(a.Length, b.Length);
        double sim = ComputeSimilarity(a, b);

        double adaptiveThreshold = maxLen < 18
            ? 0.74
            : maxLen <= 40
                ? 0.80
                : 0.82;

        double threshold = Math.Min(configuredThreshold, adaptiveThreshold);
        if (sim >= threshold)
            return true;

        int minLen = Math.Min(a.Length, b.Length);
        // Önek/sonek eşleşmesi yalnızca uzunluk farkı küçükken (%78+) çalışsın;
        // tek satırın altına yeni 2. satır eklendiğinde yeni satır yutulmasın.
        if (minLen >= 8 && minLen >= (int)(maxLen * 0.78))
        {
            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length > b.Length ? a : b;

            // Başlangıç / konuşmacı öneki kırpıntısı toleransı (ör. "ntön: Bien" ↔ "Anton: Bien")
            if (longer.EndsWith(shorter, StringComparison.Ordinal))
                return true;

            for (int skip = 1; skip <= 2 && longer.Length - skip >= 8; skip++)
            {
                if (ComputeSimilarity(longer[skip..], shorter) >= 0.86)
                    return true;
            }

            // Silah / nesne yüzünden kısmi kapanma (Fuzzy Önek / Sonek eşleşmesi)
            if (minLen >= 10)
            {
                if (ComputeSimilarity(longer[..shorter.Length], shorter) >= 0.80
                    || ComputeSimilarity(longer[^shorter.Length..], shorter) >= 0.80)
                {
                    return true;
                }
            }
        }

        // Uzun cümlede ortak başlangıç veya kelime örtüşmesi (kısmi kapanmada kelimelerden biri bozulsa bile eşleş)
        if (minLen >= 18)
        {
            int headLen = Math.Min(22, minLen);
            if (ComputeSimilarity(a[..headLen], b[..headLen]) >= 0.85
                && (maxLen - minLen) <= maxLen * 0.35)
            {
                return true;
            }

            if (ComputeWordSimilarity(a, b) >= 0.62)
                return true;
        }

        if (minLen >= 10)
        {
            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length > b.Length ? a : b;
            double subsetCov = ComputeWordSubsetCoverage(shorter, longer, out int shortWordCount, out int matchedWords);
            if (minLen >= (int)(maxLen * 0.50) && shortWordCount >= 3 && matchedWords >= 3 && subsetCov >= 0.72)
                return true;

            if (shortWordCount >= 3 && matchedWords == shortWordCount && subsetCov >= 0.99 && longer.Contains(shorter, StringComparison.Ordinal))
                return true;
        }

        // Üçlü harf (trigram) örtüşmesi ile kısa OCR bozulmalarını tolere et
        if (ComputeTrigramSimilarity(a, b) >= 0.74 && sim >= 0.72)
            return true;

        return false;
    }

    private static double ComputeWordSimilarity(string a, string b)
    {
        var wordsA = ExtractSignificantWords(a);
        var wordsB = ExtractSignificantWords(b);

        if (wordsA.Count < 2 || wordsB.Count < 2)
            return 0.0;

        int matched = CountMatchedWords(wordsA, wordsB);
        return (double)matched / Math.Max(wordsA.Count, wordsB.Count);
    }

    private static double ComputeWordSubsetCoverage(
        string shorterNorm,
        string longerNorm,
        out int shorterWordCount,
        out int matchedWords)
    {
        var shortWords = ExtractSignificantWords(shorterNorm);
        shorterWordCount = shortWords.Count;
        if (shorterWordCount == 0)
        {
            matchedWords = 0;
            return 0.0;
        }

        var longWords = ExtractSignificantWords(longerNorm);
        if (longWords.Count == 0)
        {
            matchedWords = 0;
            return 0.0;
        }

        matchedWords = CountMatchedWords(shortWords, longWords);
        return (double)matchedWords / shorterWordCount;
    }

    private static List<string> ExtractSignificantWords(string normalizedText)
        => normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .ToList();

    private static int CountMatchedWords(List<string> wordsA, List<string> wordsB)
    {
        var usedB = new bool[wordsB.Count];
        int matched = 0;

        foreach (string wa in wordsA)
        {
            for (int j = 0; j < wordsB.Count; j++)
            {
                if (usedB[j])
                    continue;

                string wb = wordsB[j];
                bool isMatch = string.Equals(wa, wb, StringComparison.Ordinal)
                    || (Math.Max(wa.Length, wb.Length) >= 4 && ComputeSimilarity(wa, wb) >= 0.75);

                if (isMatch)
                {
                    usedB[j] = true;
                    matched++;
                    break;
                }
            }
        }

        return matched;
    }

    public static string CleanRawText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var rawLines = input
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var keptLines = new List<string>(rawLines.Length);
        foreach (string rawLine in rawLines)
        {
            string line = rawLine.Replace("\u201E", ", ");
            line = InWordSymbolNoiseRegex().Replace(line, "$1 $2");
            line = MultiSpaceRegex().Replace(line.Trim(), " ");
            line = TrimEdgeNoise(line);
            if (line.Length == 0)
                continue;

            int letterCount = 0;
            foreach (char ch in line)
            {
                if (char.IsLetter(ch))
                    letterCount++;
            }

            // "-11.", ".", "t." gibi arka plan kırpıntısı çöp satırları ele
            if (letterCount < 2 && (IsEnglishSourceMode || !line.Any(c => c >= '\u2E80')))
                continue;

            line = InWordDigitNoiseRegex().Replace(line, m =>
            {
                string fixedDigit = m.Groups[2].Value switch
                {
                    "6" => "o",
                    "0" => "o",
                    _ => m.Groups[2].Value
                };
                return m.Groups[1].Value + fixedDigit + m.Groups[3].Value;
            });

            if (IsLikelyGarbageLine(line))
                continue;

            keptLines.Add(line);
        }

        return string.Join("\n", keptLines);
    }

    private static bool IsCleanerReading(string candidateText, string currentCommittedText)
    {
        if (string.IsNullOrWhiteSpace(currentCommittedText))
            return false;

        ComputeWordQuality(candidateText, out int candClean, out int candGarbage, out _);
        ComputeWordQuality(currentCommittedText, out int curClean, out int curGarbage, out _);

        if (candGarbage < curGarbage && candClean >= curClean)
            return true;

        return candGarbage == 0
            && curGarbage == 0
            && candClean > curClean
            && candidateText.Length >= currentCommittedText.Length + 3;
    }

    public static void ComputeWordQuality(string? text, out int cleanWords, out int garbageWords, out int totalTokens)
    {
        if (!IsEnglishSourceMode)
        {
            ComputeWordQualityMultilingual(text, out cleanWords, out garbageWords, out totalTokens);
            return;
        }

        cleanWords = 0;
        garbageWords = 0;
        totalTokens = 0;

        if (string.IsNullOrWhiteSpace(text))
            return;

        var rawTokens = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawToken in rawTokens)
        {
            string w = rawToken.Trim('.', ',', '!', '?', ':', ';', '\'', '"', '\u2018', '\u2019', '\u201C', '\u201D', '(', ')', '[', ']', '-', '\u2014', '\u2013', '\u2026');
            if (w.Length == 0)
            {
                // Yalnız gürültü sembollerinden oluşan bağımsız token (ör. "<", "|", "•", "*")
                if (rawToken.Any(c => c is not ('.' or '-' or '\u2014' or '\u2013' or '\u2026' or ',' or '!' or '?')))
                {
                    totalTokens++;
                    garbageWords++;
                }
                continue;
            }

            totalTokens++;

            int letters = 0;
            int digits = 0;
            bool hasInternalSymbol = false;
            bool hasVowel = false;
            bool seenLower = false;
            bool upperAfterLower = false;
            int leadingUpperCount = 0;
            bool leadingRunEnded = false;
            bool hasLowerAfterTwoLeadingUppers = false;

            foreach (char ch in w)
            {
                if (char.IsLetter(ch))
                {
                    letters++;
                    char lower = char.ToLowerInvariant(ch);
                    if (lower is 'a' or 'e' or 'i' or 'o' or 'u' or 'y' or 'ä' or 'ö' or 'ü' or 'å' or 'é' or 'á' or 'ó' or 'ú' or 'í' or 'ñ')
                        hasVowel = true;

                    if (char.IsUpper(ch))
                    {
                        if (!leadingRunEnded)
                            leadingUpperCount++;
                        if (seenLower)
                            upperAfterLower = true;
                    }
                    else
                    {
                        leadingRunEnded = true;
                        seenLower = true;
                        if (leadingUpperCount >= 2)
                            hasLowerAfterTwoLeadingUppers = true;
                    }
                }
                else if (char.IsDigit(ch))
                {
                    digits++;
                    leadingRunEnded = true;
                }
                else if (ch is not ('\'' or '\u2019' or '-'))
                {
                    hasInternalSymbol = true;
                    leadingRunEnded = true;
                }
            }

            bool isValidOrdinal = digits > 0 && letters > 0 && OrdinalTokenRegex().IsMatch(w);
            bool hasAlnumNoise = digits > 0 && letters > 0 && !isValidOrdinal;
            bool vowellessLongWord = digits == 0 && letters >= 4 && !hasVowel;

            if (hasAlnumNoise || hasInternalSymbol || upperAfterLower || hasLowerAfterTwoLeadingUppers || vowellessLongWord)
            {
                garbageWords++;
                continue;
            }

            if (digits > 0 && letters == 0)
            {
                cleanWords++;
                continue;
            }

            if (isValidOrdinal)
            {
                cleanWords++;
                continue;
            }

            if (letters == 1)
            {
                if (w[0] is 'a' or 'A' or 'I')
                    cleanWords++;
                else
                    garbageWords++;
                continue;
            }

            if (letters >= 2 && hasVowel)
            {
                cleanWords++;
            }
        }
    }

    public static bool IsLikelyGarbageLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        ComputeWordQuality(line, out int cleanWords, out int garbageWords, out int totalTokens);
        if (totalTokens == 0 || cleanWords == 0)
            return true;

        if (garbageWords >= 2 && cleanWords <= garbageWords)
            return true;

        if (totalTokens >= 3 && (cleanWords * 2) < totalTokens)
            return true;

        return false;
    }

    private static readonly char[] EdgeNoiseChars = [' ', '\t', '•', '·', '|', '_', '~', '—', '–'];

    private static string TrimEdgeNoise(string line)
    {
        return line.Trim(EdgeNoiseChars).Trim();
    }

    public static string NormalizeForComparison(string? input)
    {
        if (!IsEnglishSourceMode)
            return NormalizeForComparisonMultilingual(input);

        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Unicode aksanları ayrıştır (ö->o, ä->a, ü->u, ó->o, é->e vb.)
        string decomposed = input.Normalize(NormalizationForm.FormD);
        var rawTokens = new List<char>(decomposed.Length);
        bool prevSpace = false;

        foreach (char ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && rawTokens.Count > 0)
                {
                    rawTokens.Add(' ');
                    prevSpace = true;
                }
                continue;
            }

            char lower = char.ToLowerInvariant(ch);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                rawTokens.Add(lower);
                prevSpace = false;
            }
            // Noktalama ve sembolleri (•, —, -, [, ], (, ), ', ", :, ., ,) karşılaştırmadan tamamen çıkar
        }

        // Kelime içi OCR rakam gürültülerini harfe çevir (bağımsız sayılara dokunma)
        var sb = new StringBuilder(rawTokens.Count);
        for (int i = 0; i < rawTokens.Count; i++)
        {
            char c = rawTokens[i];
            if (c is '6' or '0' or '1' or '5' or '8')
            {
                bool prevLetter = i > 0 && rawTokens[i - 1] is >= 'a' and <= 'z';
                bool nextLetter = i + 1 < rawTokens.Count && rawTokens[i + 1] is >= 'a' and <= 'z';
                if (prevLetter || nextLetter)
                {
                    c = c switch
                    {
                        '6' => 'o',
                        '0' => 'o',
                        '1' => 'l',
                        '5' => 's',
                        '8' => 'b',
                        _ => c
                    };
                }
            }
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static void ComputeWordQualityMultilingual(string? text, out int cleanWords, out int garbageWords, out int totalTokens)
    {
        cleanWords = 0;
        garbageWords = 0;
        totalTokens = 0;

        if (string.IsNullOrWhiteSpace(text))
            return;

        var rawTokens = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawToken in rawTokens)
        {
            string w = rawToken.Trim('.', ',', '!', '?', ':', ';', '\'', '"', '\u2018', '\u2019', '\u201C', '\u201D', '(', ')', '[', ']', '-', '\u2014', '\u2013', '\u2026', '。', '、', '！', '？', '「', '」', '《', '》', '¿', '¡', '،', '؟');
            if (w.Length == 0)
            {
                if (rawToken.Any(c => c is not ('.' or '-' or '\u2014' or '\u2013' or '\u2026' or ',' or '!' or '?' or '。' or '、' or '！' or '？')))
                {
                    totalTokens++;
                    garbageWords++;
                }
                continue;
            }

            totalTokens++;
            int letters = 0;
            int digits = 0;
            bool hasInternalSymbol = false;
            bool hasVowel = false;
            bool hasNonLatin = false;
            bool hasCjk = false;

            foreach (char ch in w)
            {
                if (char.IsLetter(ch))
                {
                    letters++;
                    if (ch > '\u024F')
                    {
                        hasNonLatin = true;
                        if (ch >= '\u2E80')
                            hasCjk = true;
                    }
                    else
                    {
                        char lower = char.ToLowerInvariant(ch);
                        if (lower is 'a' or 'e' or 'i' or 'o' or 'u' or 'y' or '\u0131'
                            or 'ä' or 'ö' or 'ü' or 'å' or 'é' or 'á' or 'ó' or 'ú' or 'í'
                            or 'à' or 'è' or 'ì' or 'ò' or 'ù' or 'â' or 'ê' or 'î' or 'ô' or 'û' or 'ã' or 'õ')
                        {
                            hasVowel = true;
                        }
                    }
                }
                else if (char.IsDigit(ch))
                {
                    digits++;
                }
                else if (ch is not ('\'' or '\u2019' or '-'))
                {
                    hasInternalSymbol = true;
                }
            }

            if (hasInternalSymbol)
            {
                garbageWords++;
                continue;
            }

            if (hasNonLatin)
            {
                if (letters >= 2 || hasCjk)
                    cleanWords++;
                else
                    garbageWords++;
                continue;
            }

            if (digits > 0 && letters == 0)
            {
                cleanWords++;
                continue;
            }

            bool vowellessLongWord = digits == 0 && letters >= 5 && !hasVowel;
            if (vowellessLongWord || (digits > 0 && letters > 0 && !OrdinalTokenRegex().IsMatch(w)))
            {
                garbageWords++;
                continue;
            }

            if (letters >= 2 || (letters == 1 && digits == 0 && char.ToLowerInvariant(w[0]) is 'a' or 'i' or 'o' or 'e' or 'y' or 'à' or 'é'))
                cleanWords++;
            else
                garbageWords++;
        }
    }

    private static string NormalizeForComparisonMultilingual(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string decomposed = input.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(decomposed.Length);
        bool prevSpace = false;

        foreach (char ch in decomposed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
                continue;
            }

            char lower = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(lower))
            {
                sb.Append(lower);
                prevSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    public static double ComputeTrigramSimilarity(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3)
            return ComputeSimilarity(a, b);

        var trigramsA = new HashSet<string>(Math.Max(1, a.Length - 2));
        for (int i = 0; i <= a.Length - 3; i++)
            trigramsA.Add(a.Substring(i, 3));

        var trigramsB = new HashSet<string>(Math.Max(1, b.Length - 2));
        for (int i = 0; i <= b.Length - 3; i++)
            trigramsB.Add(b.Substring(i, 3));

        int intersection = 0;
        foreach (string t in trigramsA)
        {
            if (trigramsB.Contains(t))
                intersection++;
        }

        int union = trigramsA.Count + trigramsB.Count - intersection;
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    public static double ComputeSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 1.0;

        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0)
            return 1.0;

        int dist = LevenshteinDistance(a, b);
        return 1.0 - ((double)dist / maxLen);
    }

    public static int LevenshteinDistance(string s, string t)
    {
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;

        if (s.Length < t.Length)
            (s, t) = (t, s);

        int[] prev = new int[t.Length + 1];
        int[] curr = new int[t.Length + 1];

        for (int j = 0; j <= t.Length; j++)
            prev[j] = j;

        for (int i = 1; i <= s.Length; i++)
        {
            curr[0] = i;
            char sc = s[i - 1];

            for (int j = 1; j <= t.Length; j++)
            {
                int cost = sc == t[j - 1] ? 0 : 1;
                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                curr[j] = Math.Min(Math.Min(del, ins), sub);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[t.Length];
    }

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"([A-Za-z]{2,})([60])([A-Za-z]+)")]
    private static partial Regex InWordDigitNoiseRegex();

    [GeneratedRegex(@"([A-Za-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u00FF])[*@$%]+([A-Za-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u00FF])")]
    private static partial Regex InWordSymbolNoiseRegex();

    [GeneratedRegex(@"^\d+(st|nd|rd|th|s)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalTokenRegex();
}
