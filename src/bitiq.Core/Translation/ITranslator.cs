namespace Bitiq.Core.Translation;

public sealed record TranslationProgress(
    string PartialText,
    bool IsFinal,
    double FirstTokenMs,
    double TotalMs,
    bool FromCache);

public sealed record TranslationResult(
    string TranslatedText,
    double FirstTokenMs,
    double TotalMs,
    bool FromCache,
    bool WasCancelled = false,
    string? ErrorMessage = null,
    int PromptTokens = 0,
    int CachedTokens = 0,
    double TokensPerSecond = 0);

public interface ITranslator : IDisposable
{
    string EngineName { get; }
    bool IsReady { get; }
    string StatusText { get; }

    Task InitializeAsync(CancellationToken ct = default);
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLang,
        string targetLang,
        bool stream,
        Action<TranslationProgress>? onProgress,
        CancellationToken ct,
        TranslationContextSnapshot? context = null);
}
