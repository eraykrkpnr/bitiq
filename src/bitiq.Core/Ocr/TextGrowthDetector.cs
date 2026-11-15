namespace Bitiq.Core.Ocr;

/// <summary>
/// Daktilo (typewriter) efektiyle harf harf / kelime kelime büyüyen metinleri algılar
/// ve cümlenin bitip bitmediğini denetler.
/// </summary>
public static class TextGrowthDetector
{
    private const double MinPrefixSimilarityForGrowth = 0.85;

    /// <summary>
    /// <paramref name="currentNormalized"/> metninin <paramref name="previousNormalized"/> metninin
    /// daktilo tarzı devamı / büyümesi olup olmadığını döndürür.
    /// </summary>
    public static bool IsGrowth(string previousNormalized, string currentNormalized)
    {
        if (string.IsNullOrEmpty(previousNormalized) || string.IsNullOrEmpty(currentNormalized))
            return false;

        if (currentNormalized.Length <= previousNormalized.Length)
            return false;

        if (currentNormalized.StartsWith(previousNormalized, StringComparison.Ordinal))
            return true;

        if (previousNormalized.Length < 4)
            return false;

        // Son karakter imleç veya küçük OCR kaymasıysa sondan 1 karakteri esneterek kontrol et
        string trimmedTail = previousNormalized[..^1];
        if (trimmedTail.Length >= 3 && currentNormalized.StartsWith(trimmedTail, StringComparison.Ordinal))
            return true;

        // Önceki kısımda küçük bir OCR gürültüsü varsa bile aynı önek üzerinden uzama kontrolü
        if (previousNormalized.Length >= 5 && currentNormalized.Length >= previousNormalized.Length + 1)
        {
            string currentPrefix = currentNormalized[..previousNormalized.Length];
            if (SubtitleStabilizer.ComputeSimilarity(previousNormalized, currentPrefix) >= MinPrefixSimilarityForGrowth)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Metnin cümle sonu noktalama işaretiyle (., !, ?, …, 。, ！, ？) bitip bitmediğini kontrol eder.
    /// </summary>
    public static bool EndsSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.TrimEnd(' ', '\t', '\r', '\n', '"', '\'', '\u2019', '\u201D', ')', ']', '」', '』', '】');
        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or '\u2026' or '。' or '！' or '？';
    }
}
