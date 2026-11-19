using Bitiq.Core.Translation;
using Xunit;

namespace Bitiq.Tests;

public sealed class HyMt2PromptBuilderTests
{
    [Fact]
    public void BuildPrompt_ContextFree_MatchesOfficialDefaultTemplate()
    {
        const string expectedWithBos =
            "<|startoftext|>Translate the following text into Turkish. Note that you should only " +
            "output the translated result without any additional explanation:\n\n" +
            "We need to move, now.<|extra_0|>";

        string actual = HyMt2PromptBuilder.BuildPrompt("  We need to move, now.  ", "tr", context: null, includeBosToken: true);
        Assert.Equal(expectedWithBos, actual);

        string withoutBos = HyMt2PromptBuilder.BuildPrompt("We need to move, now.", "tr", context: null, includeBosToken: false);
        Assert.StartsWith("Translate the following text into Turkish.", withoutBos);
        Assert.DoesNotContain(HyMt2PromptBuilder.Bos, withoutBos);
        Assert.EndsWith("<|extra_0|>", withoutBos);
    }

    [Fact]
    public void BuildPrompt_WithProfileAndHistory_OrdersPrefixStaticBeforeDynamic()
    {
        var profile = new GameProfile
        {
            Id = "tlou2",
            Title = "The Last of Us Part II",
            Synopsis = "post-apocalyptic survival drama",
            ToneNote = "natural, colloquial spoken Turkish; close friends address each other with \"sen\".",
            Characters =
            [
                new CharacterEntry("Ellie", "female, 19"),
                new CharacterEntry("Dina", "female, 19")
            ],
            Glossary =
            [
                new GlossaryEntry("Clicker", "Tıkırtıcı"),
                new GlossaryEntry("WLF", "WLF")
            ]
        };

        var history = new List<ContextLine>
        {
            new("Dina: You okay?", "Dina: İyi misin?"),
            new("Ellie: Yeah. Just tired.", "Ellie: Evet. Sadece yorgunum.")
        };

        var snap = new TranslationContextSnapshot(profile, history);
        string actual = HyMt2PromptBuilder.BuildPrompt("Dina: Then let's find somewhere to rest.", "tr", snap, includeBosToken: true);

        const string expected =
            "<|startoftext|>[Background Information]\n" +
            "Game: The Last of Us Part II — post-apocalyptic survival drama.\n" +
            "Characters: Ellie (female, 19), Dina (female, 19).\n" +
            "Tone: natural, colloquial spoken Turkish; close friends address each other with \"sen\".\n" +
            "Clicker translates to Tıkırtıcı\n" +
            "WLF translates to WLF\n\n" +
            "Recent dialogue (original → Turkish):\n" +
            "Dina: You okay? → Dina: İyi misin?\n" +
            "Ellie: Yeah. Just tired. → Ellie: Evet. Sadece yorgunum.\n\n" +
            "Please translate the following text into Turkish, taking the provided background information into consideration.\n\n" +
            "[Source Text]\n" +
            "Dina: Then let's find somewhere to rest.<|extra_0|>";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CleanModelOutput_StripsControlTokensHeadersAndExcessLines()
    {
        string rawEcho =
            "[Background Information]\n" +
            "Dina: İyi misin?\n" +
            "[Source Text]\n" +
            "Turkish translation: Dina: O halde dinlenecek bir yer bulalım.<|eos|><|extra_5|>\n" +
            "Note: Informal tone used.";

        string cleaned = HyMt2PromptBuilder.CleanModelOutput(rawEcho, sourceLineCount: 1);
        Assert.Equal("Dina: O halde dinlenecek bir yer bulalım.", cleaned);

        string rawMulti = "Line 1 leaked\nLine 2 leaked\nLine 3 leaked\nGerçek çeviri satır 1\nGerçek çeviri satır 2<|eos|>";
        string cleanedMulti = HyMt2PromptBuilder.CleanModelOutput(rawMulti, sourceLineCount: 2);
        Assert.Equal("Gerçek çeviri satır 1\nGerçek çeviri satır 2", cleanedMulti);
    }

    [Fact]
    public void LocalModelProfiles_ExposeExpectedContextAndSamplingSettings()
    {
        ILocalModelProfile hymt2 = HyMt2ModelProfile.Instance;
        ILocalModelProfile tg = TranslateGemmaModelProfile.Instance;

        Assert.True(hymt2.SupportsContext);
        Assert.False(tg.SupportsContext);
        Assert.Equal(127958, hymt2.BosTokenId);
        Assert.Equal(2, tg.BosTokenId);
        Assert.Equal(0.7, hymt2.Sampling.Temperature);
        Assert.Equal(0.6, hymt2.Sampling.TopP);
        Assert.Contains("<|eos|>", hymt2.StopTokens);
        Assert.Contains("<|extra_5|>", hymt2.StopTokens);
    }
}
