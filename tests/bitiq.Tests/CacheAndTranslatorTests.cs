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
}
