using System.Text;
using System.Text.RegularExpressions;

namespace Bitiq.Core.Translation;

/// <summary>
/// Resmi tencent/Hy-MT2-7B sohbet ve bağlamlı çeviri şablonlarını uygular.
/// KV önbellek isabeti için sabit kısımlar (profil) başta, değişen kısımlar (geçmiş ve kaynak) sondadır.
/// </summary>
public static partial class HyMt2PromptBuilder
{
    public const string Bos = "<|startoftext|>";
    public const string UserEnd = "<|extra_0|>";
    public const int BosTokenId = 127958;
    public const string BosProbeText = "<|startoftext|>Hello";

    public static readonly string[] StopTokens =
    [
        "<|eos|>",
        "<|extra_5|>",
        "<|endoftext|>",
        "<|startoftext|>"
    ];

    public static string BuildPrompt(
        string text,
        string targetLangCode = "tr",
        TranslationContextSnapshot? context = null,
        bool includeBosToken = true)
    {
        string tgt = TranslateGemmaPromptBuilder.GetLanguageName(targetLangCode);
        var sb = new StringBuilder(1024);

        if (includeBosToken)
            sb.Append(Bos);

        if (context is { IsEmpty: false })
        {
            sb.Append("[Background Information]\n");
            AppendProfile(sb, context.Profile);
            AppendHistory(sb, context.History, tgt);
            sb.Append($"\nPlease translate the following text into {tgt}, " +
                      "taking the provided background information into consideration.\n\n");
            sb.Append("[Source Text]\n");
        }
        else
        {
            sb.Append($"Translate the following text into {tgt}. Note that you should only " +
                      "output the translated result without any additional explanation:\n\n");
        }

        sb.Append((text ?? string.Empty).Trim());
        sb.Append(UserEnd);
        return sb.ToString();
    }

    public static string BuildBackgroundPreview(GameProfile? profile, string targetLangCode = "tr")
    {
        if (profile is null || profile.IsEffectivelyEmpty)
            return string.Empty;

        var sb = new StringBuilder(512);
        sb.Append("[Background Information]\n");
        AppendProfile(sb, profile);
        string tgt = TranslateGemmaPromptBuilder.GetLanguageName(targetLangCode);
        sb.Append($"\n(Target language: {tgt})");
        return sb.ToString().TrimEnd();
    }

    private static void AppendProfile(StringBuilder sb, GameProfile? profile)
    {
        if (profile is null)
            return;

        if (!string.IsNullOrWhiteSpace(profile.Title))
        {
            sb.Append($"Game: {profile.Title.Trim()}");
            if (!string.IsNullOrWhiteSpace(profile.Synopsis))
                sb.Append($" — {profile.Synopsis.Trim()}");
            sb.Append(".\n");
        }
        else if (!string.IsNullOrWhiteSpace(profile.Synopsis))
        {
            sb.Append($"Context: {profile.Synopsis.Trim()}.\n");
        }

        var validChars = profile.Characters
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => string.IsNullOrWhiteSpace(c.Note)
                ? c.Name.Trim()
                : $"{c.Name.Trim()} ({c.Note.Trim()})")
            .ToList();

        if (validChars.Count > 0)
            sb.Append("Characters: ").Append(string.Join(", ", validChars)).Append(".\n");

        if (!string.IsNullOrWhiteSpace(profile.ToneNote))
            sb.Append($"Tone: {profile.ToneNote.Trim()}\n");

        foreach (var g in profile.Glossary)
        {
            if (string.IsNullOrWhiteSpace(g.Source) || string.IsNullOrWhiteSpace(g.Target))
                continue;
            sb.Append($"{g.Source.Trim()} translates to {g.Target.Trim()}\n");
        }
    }

    private static void AppendHistory(StringBuilder sb, IReadOnlyList<ContextLine> history, string targetLangName)
    {
        if (history.Count == 0)
            return;

        sb.Append($"\nRecent dialogue (original → {targetLangName}):\n");
        foreach (var line in history)
        {
            sb.Append(line.Source.Replace("\r\n", " ").Replace('\n', ' ').Trim())
              .Append(" → ")
              .Append(line.Target.Replace("\r\n", " ").Replace('\n', ' ').Trim())
              .Append('\n');
        }
    }

    public static int CountNonEmptyLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1;

        int count = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                count++;
        }
        return Math.Max(1, count);
    }

    public static string CleanModelOutput(string? raw, int sourceLineCount = 1)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string text = SpecialTokenRegex().Replace(raw, string.Empty)
            .Replace("\r\n", "\n")
            .Trim();

        // Model bağlam başlıklarını yankılarsa yalnızca [Source Text] sonrasını al
        int srcMarkerIdx = text.LastIndexOf("[Source Text]", StringComparison.OrdinalIgnoreCase);
        if (srcMarkerIdx >= 0)
            text = text[(srcMarkerIdx + "[Source Text]".Length)..].Trim();

        text = LeadingPreambleRegex().Replace(text, string.Empty).Trim();

        int noteIdx = text.IndexOf("\nNote:", StringComparison.OrdinalIgnoreCase);
        if (noteIdx >= 0) text = text[..noteIdx];

        int trNoteIdx = text.IndexOf("\nNot:", StringComparison.OrdinalIgnoreCase);
        if (trNoteIdx >= 0) text = text[..trNoteIdx];

        int explIdx = text.IndexOf("\nExplanation:", StringComparison.OrdinalIgnoreCase);
        if (explIdx >= 0) text = text[..explIdx];

        int hrIdx = text.IndexOf("\n---", StringComparison.Ordinal);
        if (hrIdx >= 0) text = text[..hrIdx];

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        int expectedLines = Math.Max(1, sourceLineCount);
        if (lines.Count > expectedLines + 1)
            lines = lines.Skip(lines.Count - expectedLines).ToList();

        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"<\|[a-zA-Z0-9_]+\|>")]
    private static partial Regex SpecialTokenRegex();

    [GeneratedRegex(@"^(?:Here\s+is\s+the\s+(?:[A-Za-z]+\s+)?translation\s*:|(?:[A-Za-z]+\s+)?translation\s*:|Çeviri\s*:)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingPreambleRegex();
}
