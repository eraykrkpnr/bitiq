using Bitiq.Core.Translation;
using Xunit;

namespace Bitiq.Tests;

public sealed class PromptBuilderTests
{
    [Fact]
    public void BuildPrompt_MatchesOfficialTranslateGemmaTemplate_Sample1()
    {
        const string expected =
            "<bos><start_of_turn>user\n" +
            "You are a professional English (en) to Turkish (tr) translator. Your goal is to accurately convey the meaning and nuances of the original English text while adhering to Turkish grammar, vocabulary, and cultural sensitivities.\n" +
            "Produce only the Turkish translation, without any additional explanations or commentary. Please translate the following English text into Turkish:\n\n\n" +
            "Wait! Don't open that door, it's a trap!<end_of_turn>\n" +
            "<start_of_turn>model\n";

        string actual = TranslateGemmaPromptBuilder.BuildPrompt("  Wait! Don't open that door, it's a trap!  ", "en", "tr");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildPrompt_MatchesOfficialTranslateGemmaTemplate_Sample2()
    {
        const string expected =
            "<bos><start_of_turn>user\n" +
            "You are a professional English (en) to Turkish (tr) translator. Your goal is to accurately convey the meaning and nuances of the original English text while adhering to Turkish grammar, vocabulary, and cultural sensitivities.\n" +
            "Produce only the Turkish translation, without any additional explanations or commentary. Please translate the following English text into Turkish:\n\n\n" +
            "You have died. Press X to continue.<end_of_turn>\n" +
            "<start_of_turn>model\n";

        string actual = TranslateGemmaPromptBuilder.BuildPrompt("You have died. Press X to continue.", "en", "tr");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildPrompt_MatchesOfficialTranslateGemmaTemplate_Sample3()
    {
        const string expected =
            "<bos><start_of_turn>user\n" +
            "You are a professional English (en) to Turkish (tr) translator. Your goal is to accurately convey the meaning and nuances of the original English text while adhering to Turkish grammar, vocabulary, and cultural sensitivities.\n" +
            "Produce only the Turkish translation, without any additional explanations or commentary. Please translate the following English text into Turkish:\n\n\n" +
            "The ancient relics are hidden beneath the temple of shadows.<end_of_turn>\n" +
            "<start_of_turn>model\n";

        string actual = TranslateGemmaPromptBuilder.BuildPrompt("The ancient relics are hidden beneath the temple of shadows.", "en", "tr");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CleanModelOutput_RemovesTokensPreamblesAndNotes()
    {
        string raw = "Turkish translation: Bekle! O kapıyı açma, bu bir tuzak!<end_of_turn>\nNote: Literal translation.";
        string cleaned = TranslateGemmaPromptBuilder.CleanModelOutput(raw);
        Assert.Equal("Bekle! O kapıyı açma, bu bir tuzak!", cleaned);
    }
}
