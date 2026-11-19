using Bitiq.Core.Translation;
using Xunit;

namespace Bitiq.Tests;

public sealed class CacheAndTranslatorTests
{
    [Fact]
    public void LruCache_NormalizesLookupAndEvictsOldest()
    {
        var cache = new TranslationLruCache(capacity: 16);
        for (int i = 0; i < 16; i++)
        {
            cache.Set("en", "tr", $"Sentence number {i}", $"Cümle {i}");
        }

        // 0. elemana erişerek LRU'da tazele
        Assert.True(cache.TryGet("en", "tr", "  sentence   NUMBER 0 ", out var hit0));
        Assert.Equal("Cümle 0", hit0);

        // Yeni eleman eklendiğinde artık en eski olan 1. eleman düşmeli
        cache.Set("en", "tr", "Brand new line", "Yepyeni satır");

        Assert.True(cache.TryGet("en", "tr", "Sentence number 0", out _));
        Assert.False(cache.TryGet("en", "tr", "Sentence number 1", out _));
        Assert.True(cache.TryGet("en", "tr", "Brand new line", out var hitNew));
        Assert.Equal("Yepyeni satır", hitNew);
    }

    [Fact]
    public void ParseGtxResponse_JoinsMultiSentenceSegments()
    {
        const string json = """
        [
          [
            ["Merhaba dünya. ", "Hello world. ", null, null, 10],
            ["Nasılsın?", "How are you?", null, null, 10]
          ],
          null,
          "en"
        ]
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        string parsed = GoogleGtxTranslator.ParseGtxResponse(doc.RootElement);
        Assert.Equal("Merhaba dünya. Nasılsın?", parsed);
    }

    [Fact]
    public void LruCache_DifferentiatesLongLinesByContextAndSharesShortUiAcrossHistory()
    {
        var cache = new TranslationLruCache(capacity: 32);
        var profileA = new GameProfile { Id = "gameA", Title = "Game A", ToneNote = "informal" };
        var profileB = new GameProfile { Id = "gameB", Title = "Game B", ToneNote = "formal" };

        var snapA1 = new TranslationContextSnapshot(profileA, [new ContextLine("Who goes there?", "Kim var orada?")]);
        var snapA2 = new TranslationContextSnapshot(profileA, [new ContextLine("Stop right now!", "Hemen dur!")]);
        var snapB1 = new TranslationContextSnapshot(profileB, [new ContextLine("Who goes there?", "Kim var orada?")]);

        const string longLine = "We should check the old warehouse first.";
        string? fpA1 = TranslationLruCache.ResolveFingerprint(snapA1, longLine);
        string? fpA2 = TranslationLruCache.ResolveFingerprint(snapA2, longLine);
        string? fpB1 = TranslationLruCache.ResolveFingerprint(snapB1, longLine);

        cache.Set("en", "tr", longLine, "Önce eski depoyu kontrol etmeliyiz.", fpA1);
        Assert.True(cache.TryGet("en", "tr", longLine, out var hitA1, fpA1));
        Assert.Equal("Önce eski depoyu kontrol etmeliyiz.", hitA1);
        Assert.False(cache.TryGet("en", "tr", longLine, out _, fpA2));
        Assert.False(cache.TryGet("en", "tr", longLine, out _, fpB1));

        // <=3 kelimelik kısa UI metni aynı profilde geçmiş değişse de önbellekten dönmeli, farklı profilde ayrılmalı
        const string shortUi = "Press X";
        string? sfpA1 = TranslationLruCache.ResolveFingerprint(snapA1, shortUi);
        string? sfpA2 = TranslationLruCache.ResolveFingerprint(snapA2, shortUi);
        string? sfpB1 = TranslationLruCache.ResolveFingerprint(snapB1, shortUi);

        cache.Set("en", "tr", shortUi, "X tuşuna bas", sfpA1);
        Assert.True(cache.TryGet("en", "tr", shortUi, out var shortHit, sfpA2));
        Assert.Equal("X tuşuna bas", shortHit);
        Assert.False(cache.TryGet("en", "tr", shortUi, out _, sfpB1));
    }
}
