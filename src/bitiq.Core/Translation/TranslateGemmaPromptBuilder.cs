using System.Text.RegularExpressions;

namespace Bitiq.Core.Translation;

/// <summary>
/// Resmi google/translategemma-4b-it chat_template.jinja şablonunu birebir uygular.
/// </summary>
public static partial class TranslateGemmaPromptBuilder
{
    private static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["en-US"] = "English",
            ["en-GB"] = "English",
            ["tr"] = "Turkish",
            ["tr-TR"] = "Turkish",
            ["de"] = "German",
            ["fr"] = "French",
            ["es"] = "Spanish",
            ["ja"] = "Japanese",
            ["ko"] = "Korean",
            ["zh"] = "Chinese",
            ["ru"] = "Russian",
            ["ar"] = "Arabic",
            ["it"] = "Italian",
            ["pt"] = "Portuguese"
        };

    public static string GetLanguageName(string langCode)
    {
        string normalized = (langCode ?? string.Empty).Replace('_', '-').Trim();
        if (LanguageNames.TryGetValue(normalized, out var name))
            return name;

        int dash = normalized.IndexOf('-');
        if (dash > 0 && LanguageNames.TryGetValue(normalized[..dash], out var baseName))
            return baseName;

        throw new ArgumentException($"Desteklenmeyen dil kodu: '{langCode}'", nameof(langCode));
    }

    /// <summary>
    /// google/translategemma-4b-it deposundaki chat_template.jinja çıktısıyla birebir aynı ham prompt üretir.
    /// Sabit talimat baştadır (cache_prompt: true için ön ek önbelleklemesi), değişen metin sondadır.
    /// </summary>
    public static string BuildPrompt(
        string text,
        string sourceLangCode = "en",
        string targetLangCode = "tr",
        bool includeBosToken = true)
    {
        string srcCode = (sourceLangCode ?? "en").Replace('_', '-').Trim();
        string tgtCode = (targetLangCode ?? "tr").Replace('_', '-').Trim();
        string srcLang = GetLanguageName(srcCode);
        string tgtLang = GetLanguageName(tgtCode);
        string trimmedText = (text ?? string.Empty).Trim();

        string prefix = includeBosToken ? "<bos>" : string.Empty;

        // chat_template.jinja ile birebir eşleşir (üç \n\n\n dahil).
        return $"{prefix}<start_of_turn>user\n" +
               $"You are a professional {srcLang} ({srcCode}) to {tgtLang} ({tgtCode}) translator. " +
               $"Your goal is to accurately convey the meaning and nuances of the original {srcLang} text " +
               $"while adhering to {tgtLang} grammar, vocabulary, and cultural sensitivities.\n" +
               $"Produce only the {tgtLang} translation, without any additional explanations or commentary. " +
               $"Please translate the following {srcLang} text into {tgtLang}:\n\n\n" +
               $"{trimmedText}<end_of_turn>\n" +
               $"<start_of_turn>model\n";
    }

    public static int ComputeMaxTokens(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            return 32;

        // İngilizce -> Türkçe metinlerde güvenli üst sınır; gereksiz üretimi keser.
        return Math.Clamp((int)Math.Ceiling(sourceText.Length * 1.3) + 24, 32, 180);
    }

    /// <summary>
    /// Model çıktısındaki kontrol belirteçlerini ve olası ek açıklamaları temizler.
    /// </summary>
    public static string CleanModelOutput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string text = raw
            .Replace("<end_of_turn>", string.Empty, StringComparison.Ordinal)
            .Replace("<eos>", string.Empty, StringComparison.Ordinal)
            .Replace("<bos>", string.Empty, StringComparison.Ordinal)
            .Replace("<start_of_turn>model", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n")
            .Trim();

        // Model nadiren "Turkish translation:" veya "Çeviri:" gibi ön ek eklerse temizle
        text = LeadingPreambleRegex().Replace(text, string.Empty).Trim();

        // Ek not / açıklama satırı ayraçları ("---", "\nNote:", "\nNot:") gelirse kes
        int noteIdx = text.IndexOf("\nNote:", StringComparison.OrdinalIgnoreCase);
        if (noteIdx >= 0) text = text[..noteIdx];

        int trNoteIdx = text.IndexOf("\nNot:", StringComparison.OrdinalIgnoreCase);
        if (trNoteIdx >= 0) text = text[..trNoteIdx];

        int explIdx = text.IndexOf("\nExplanation:", StringComparison.OrdinalIgnoreCase);
        if (explIdx >= 0) text = text[..explIdx];

        int hrIdx = text.IndexOf("\n---", StringComparison.Ordinal);
        if (hrIdx >= 0) text = text[..hrIdx];

        text = text.Trim();

        // Çift tırnak sarmalaması sadece baştan ve sondan tek eşleşen çiftse (kaynak tırnaksızsa gibi durumlar)
        // dokunmayalım; yalnızca gereksiz boş satırları temizle.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"^(?:Here\s+is\s+the\s+(?:[A-Za-z]+\s+)?translation\s*:|(?:[A-Za-z]+\s+)?translation\s*:|Çeviri\s*:)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingPreambleRegex();
}
