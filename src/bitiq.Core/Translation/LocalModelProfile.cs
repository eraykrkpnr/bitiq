using Bitiq.Core.Models;

namespace Bitiq.Core.Translation;

public sealed record LocalSamplingConfig(
    double Temperature,
    int TopK,
    double TopP,
    double RepeatPenalty);

public interface ILocalModelProfile
{
    LocalModelKind ModelKind { get; }
    string EngineName { get; }
    int BosTokenId { get; }
    string BosProbeText { get; }
    IReadOnlyList<string> StopTokens { get; }
    LocalSamplingConfig Sampling { get; }
    bool SupportsContext { get; }

    string BuildPrompt(
        string text,
        string sourceLangCode,
        string targetLangCode,
        TranslationContextSnapshot? context,
        bool includeBosToken);

    int ComputeMaxTokens(string sourceText);

    string CleanModelOutput(string? raw, int sourceLineCount);
}

public sealed class TranslateGemmaModelProfile : ILocalModelProfile
{
    private static readonly string[] Stops = ["<end_of_turn>", "<eos>"];

    public static TranslateGemmaModelProfile Instance { get; } = new();

    public LocalModelKind ModelKind => LocalModelKind.TranslateGemma4B;
    public string EngineName => "Yerel (TranslateGemma 4B)";
    public int BosTokenId => 2;
    public string BosProbeText => "<bos>Hello";
    public IReadOnlyList<string> StopTokens => Stops;
    public LocalSamplingConfig Sampling { get; } = new(Temperature: 0.15, TopK: 20, TopP: 0.88, RepeatPenalty: 1.05);
    public bool SupportsContext => false;

    public string BuildPrompt(
        string text,
        string sourceLangCode,
        string targetLangCode,
        TranslationContextSnapshot? context,
        bool includeBosToken)
        => TranslateGemmaPromptBuilder.BuildPrompt(text, sourceLangCode, targetLangCode, includeBosToken);

    public int ComputeMaxTokens(string sourceText)
        => TranslateGemmaPromptBuilder.ComputeMaxTokens(sourceText);

    public string CleanModelOutput(string? raw, int sourceLineCount)
        => TranslateGemmaPromptBuilder.CleanModelOutput(raw);
}

public sealed class HyMt2ModelProfile : ILocalModelProfile
{
    public static HyMt2ModelProfile Instance { get; } = new();

    public LocalModelKind ModelKind => LocalModelKind.HyMt2_7B;
    public string EngineName => "Yerel (Hy-MT2 7B Q6_K)";
    public int BosTokenId => HyMt2PromptBuilder.BosTokenId;
    public string BosProbeText => HyMt2PromptBuilder.BosProbeText;
    public IReadOnlyList<string> StopTokens => HyMt2PromptBuilder.StopTokens;
    public LocalSamplingConfig Sampling { get; } = new(Temperature: 0.7, TopK: 20, TopP: 0.6, RepeatPenalty: 1.05);
    public bool SupportsContext => true;

    public string BuildPrompt(
        string text,
        string sourceLangCode,
        string targetLangCode,
        TranslationContextSnapshot? context,
        bool includeBosToken)
        => HyMt2PromptBuilder.BuildPrompt(text, targetLangCode, context, includeBosToken);

    public int ComputeMaxTokens(string sourceText)
        => TranslateGemmaPromptBuilder.ComputeMaxTokens(sourceText);

    public string CleanModelOutput(string? raw, int sourceLineCount)
        => HyMt2PromptBuilder.CleanModelOutput(raw, sourceLineCount);
}
