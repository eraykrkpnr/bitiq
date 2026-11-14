namespace Bitiq.Core.Translation;

public sealed record LanguageDescriptor(
    string Code,
    string OcrCapabilityTag,
    string NameTr,
    string NameEn,
    string NativeName);

/// <summary>
/// Desteklenen kaynak (OCR) ve hedef (çeviri) dilleri kataloğu.
/// </summary>
public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageDescriptor> SupportedLanguages { get; } =
    [
        new("en", "en-US", "İngilizce", "English", "English"),
        new("tr", "tr-TR", "Türkçe", "Turkish", "Türkçe"),
        new("de", "de-DE", "Almanca", "German", "Deutsch"),
        new("fr", "fr-FR", "Fransızca", "French", "Français"),
        new("es", "es-ES", "İspanyolca", "Spanish", "Español"),
        new("it", "it-IT", "İtalyanca", "Italian", "Italiano"),
        new("pt", "pt-BR", "Portekizce", "Portuguese", "Português"),
        new("ru", "ru-RU", "Rusça", "Russian", "Русский"),
        new("ja", "ja-JP", "Japonca", "Japanese", "日本語"),
        new("ko", "ko-KR", "Korece", "Korean", "한국어"),
        new("zh", "zh-CN", "Çince", "Chinese", "中文"),
        new("ar", "ar-SA", "Arapça", "Arabic", "العربية")
    ];

    public static LanguageDescriptor GetByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return SupportedLanguages[0];

        string normalized = code.Replace('_', '-').Trim();
        foreach (var lang in SupportedLanguages)
        {
            if (string.Equals(lang.Code, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(lang.OcrCapabilityTag, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return lang;
            }
        }

        int dash = normalized.IndexOf('-');
        if (dash > 0)
        {
            string baseCode = normalized[..dash];
            foreach (var lang in SupportedLanguages)
            {
                if (string.Equals(lang.Code, baseCode, StringComparison.OrdinalIgnoreCase))
                    return lang;
            }
        }

        return SupportedLanguages[0];
    }

    public static string GetDisplayName(string? code, bool isEnglishUi)
    {
        var desc = GetByCode(code);
        string localized = isEnglishUi ? desc.NameEn : desc.NameTr;
        return string.Equals(localized, desc.NativeName, StringComparison.OrdinalIgnoreCase)
            ? localized
            : $"{localized} ({desc.NativeName})";
    }

    public static string GetShortName(string? code, bool isEnglishUi)
    {
        var desc = GetByCode(code);
        return isEnglishUi ? desc.NameEn : desc.NameTr;
    }
}
