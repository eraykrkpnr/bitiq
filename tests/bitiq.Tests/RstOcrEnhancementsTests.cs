using System.Diagnostics;
using Bitiq.Core.Models;
using Bitiq.Core.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Bitiq.Tests;

public sealed class RstOcrEnhancementsTests(ITestOutputHelper output)
{
    [Fact]
    public void TextGrowthDetector_DetectsTypingGrowthAndSentenceCompletion()
    {
        Assert.True(TextGrowthDetector.IsGrowth("wait", "wait dont"));
        Assert.True(TextGrowthDetector.IsGrowth("wait dont", "wait dont open the door"));
        Assert.True(TextGrowthDetector.IsGrowth("hello worl_", "hello world!")); // son karakter imleç/kayma toleransı
        Assert.True(TextGrowthDetector.IsGrowth("hello world", "hell0 world!")); // önek içi 1 karakter OCR gürültüsü

        Assert.False(TextGrowthDetector.IsGrowth("hello", "hello"));
        Assert.False(TextGrowthDetector.IsGrowth("hello world", "hello"));
        Assert.False(TextGrowthDetector.IsGrowth("abc", "xyz123"));

        Assert.True(TextGrowthDetector.EndsSentence("Don't move."));
        Assert.True(TextGrowthDetector.EndsSentence("What's happening?!"));
        Assert.True(TextGrowthDetector.EndsSentence("\"Stay back!\""));
        Assert.True(TextGrowthDetector.EndsSentence("大丈夫ですか。"));
        Assert.False(TextGrowthDetector.EndsSentence("Wait, don't open"));
    }

    [Fact]
    public void SubtitleStabilizer_TypewriterGrowthWaitsDuringTypingAndCommitsOnCompletion()
    {
        var stabilizer = new SubtitleStabilizer(requiredFrames: 1, debounceMs: 150, similarityThreshold: 0.90);

        var step1 = stabilizer.Process("Wait", 100);
        Assert.Equal(StabilizerAction.None, step1.Action);

        // Metin büyüdüğü için zamanlayıcı 220ms'e sıfırlanmalı
        var step2 = stabilizer.Process("Wait! Don't", 220);
        Assert.Equal(StabilizerAction.None, step2.Action);

        // 220 + 80 = 300ms: ilk kareye göre 200ms geçse bile son büyümeden henüz 80ms geçti -> beklemeli
        var pollTooSoon = stabilizer.PollDebounce(300);
        Assert.Equal(StabilizerAction.None, pollTooSoon.Action);

        // Tekrar büyüme -> zamanlayıcı 340ms'e sıfırlanır
        var step3 = stabilizer.Process("Wait! Don't open", 340);
        Assert.Equal(StabilizerAction.None, step3.Action);

        // Duraksama 160ms (>= 150ms) -> aday onaylanır
        var settled = stabilizer.PollDebounce(500);
        Assert.Equal(StabilizerAction.Translate, settled.Action);
        Assert.Equal("Wait! Don't open", settled.RawText);

        // Aktif altyazı varken aynı cümle tamamlanıp nokta ile geldiğinde bastırılmamalı ve hemen çevrilmeli
        var completed = stabilizer.Process("Wait! Don't open that door.", 540);
        Assert.Equal(StabilizerAction.Translate, completed.Action);
        Assert.Equal("Wait! Don't open that door.", completed.RawText);
    }

    [Fact]
    public void OcrBlockGrouper_FiltersLowConfidenceWordsAndSplitsWideColumns()
    {
        var words = new List<OcrWordElement>
        {
            new("blurry", new OcrBoundingBox(10, 5, 40, 20), Confidence: 0.22),
            new("The", new OcrBoundingBox(50, 40, 40, 22), Confidence: 0.95),
            new("portal", new OcrBoundingBox(96, 40, 62, 22), Confidence: 0.91),
            new("opens.", new OcrBoundingBox(164, 40, 64, 22), Confidence: 0.93),
            // Sağ tarafta geniş boşlukla ayrılmış ayrı HUD/sütun
            new("HP", new OcrBoundingBox(520, 40, 32, 22), Confidence: 0.97),
            new("100", new OcrBoundingBox(558, 40, 42, 22), Confidence: 0.98)
        };

        var blocks = OcrBlockGrouper.GroupIntoBlocks(
            words,
            new OcrGroupingOptions(MinWordConfidence: 0.45, SourceLanguageCode: "en"));

        Assert.Equal(2, blocks.Count);
        Assert.Equal("The portal opens.", blocks[0].Text);
        Assert.Equal("HP 100", blocks[1].Text);
    }

    [Fact]
    public void OcrBlockGrouper_OrdersVerticalJapaneseColumnsRightToLeft()
    {
        // Geleneksel dikey Japonca (tategaki): önce sağdaki sütun, sonra soldaki sütun
        var words = new List<OcrWordElement>
        {
            new("世", new OcrBoundingBox(200, 20, 24, 24), 0.95),
            new("界", new OcrBoundingBox(200, 48, 24, 24), 0.94),
            new("を", new OcrBoundingBox(200, 76, 24, 24), 0.92),
            new("救", new OcrBoundingBox(140, 20, 24, 24), 0.96),
            new("え", new OcrBoundingBox(140, 48, 24, 24), 0.95),
        };

        var formatted = OcrBlockGrouper.GroupAndFormat(
            words,
            new OcrGroupingOptions(MinWordConfidence: 0.45, SourceLanguageCode: "ja", AutoDetectVerticalText: true));

        Assert.Equal("世界を\n救え", formatted);
    }

    [Fact]
    public void AppSettings_DefaultsAndSerializationSupportOcrEngineKind()
    {
        var settings = new AppSettings();
        Assert.Equal(OcrEngineKind.WindowsOcr, settings.OcrEngine);
        Assert.InRange(settings.OcrMinConfidence, 0.3, 0.7);
    }

    [Fact]
    public void WindowsOcrService_HandlesOneOcrUnavailableGracefullyAndSwitchesBack()
    {
        using var service = new WindowsOcrService(
            preprocessEnabled: true,
            sourceLanguageCode: "en",
            engineKind: OcrEngineKind.OneOcr,
            minWordConfidence: 0.45);

        Assert.Equal(OcrEngineKind.OneOcr, service.ActiveEngineKind);
        if (!OneOcrNativeEngine.CheckAvailability().IsAvailable)
        {
            Assert.False(service.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(service.Status.ErrorMessage));
            Assert.False(string.IsNullOrWhiteSpace(service.Status.FixInstructions));
        }

        // Windows OCR'a canlı geri dönüş her zaman temiz çalışmalı
        var winStatus = service.ConfigureEngine(OcrEngineKind.WindowsOcr, 0.45, "en");
        Assert.Equal(OcrEngineKind.WindowsOcr, service.ActiveEngineKind);
        Assert.Equal(winStatus.IsAvailable, service.IsAvailable);
    }

    [Fact]
    public void SubtitleStabilizer_CalmScenePromotesCandidateAndClearsEmptyOnPollDebounce()
    {
        var stabilizer = new SubtitleStabilizer(
            requiredFrames: 2,
            debounceMs: 0,
            similarityThreshold: 0.90,
            requiredEmptyFrames: 2,
            minEmptyDurationMs: 150);

        // Durağan sahnede sadece 1 kare geldi (sonrasında değişim olmadığı için yeni kare akmıyor)
        var first = stabilizer.Process("Yeah? Damn.", 100);
        Assert.Equal(StabilizerAction.None, first.Action);

        // Kısa titreşim süresi henüz dolmadı
        Assert.Equal(StabilizerAction.None, stabilizer.PollDebounce(200).Action);

        // 220 ms boyunca değişmeden kaldığı için zamanlayıcı kararı onaylamalı
        var promoted = stabilizer.PollDebounce(330);
        Assert.Equal(StabilizerAction.Translate, promoted.Action);
        Assert.Equal("Yeah? Damn.", promoted.RawText);

        // Tek kare boş ekran gelip sahne sessiz kaldığında da zamanlayıcı ekranı temizlemeli
        Assert.Equal(StabilizerAction.None, stabilizer.Process(string.Empty, 1000).Action);
        var cleared = stabilizer.PollDebounce(1250);
        Assert.Equal(StabilizerAction.Clear, cleared.Action);
    }

    [Fact]
    public async Task OneOcrNativeEngine_LoadsAndRecognizesRenderedTextWhenAvailable()
    {
        var availability = OneOcrNativeEngine.CheckAvailability();
        if (!availability.IsAvailable)
        {
            output.WriteLine($"Skipping OneOCR smoke test: {availability.ErrorMessage}");
            return;
        }

        const int width = 460;
        const int height = 100;
        byte[] bgra = RenderSyntheticTextBgra("HELLO WORLD", width, height);

        using var engine = new OneOcrNativeEngine();
        bool ok = engine.EnsureInitialized(out string? initError);
        output.WriteLine($"OneOCR Runtime Dir: {engine.RuntimeDirectory}");
        Assert.True(ok, initError);

        var sw = Stopwatch.StartNew();
        var words = engine.RecognizeWords(bgra, width, height);
        sw.Stop();

        string joined = OcrBlockGrouper.GroupAndFormat(
            words,
            new OcrGroupingOptions(MinWordConfidence: 0.30, SourceLanguageCode: "en"));

        output.WriteLine($"OneOCR Elapsed: {sw.Elapsed.TotalMilliseconds:F2} ms, Words: {words.Count}, Result: '{joined}'");
        foreach (var w in words)
        {
            output.WriteLine($"  word='{w.Text}' conf={w.Confidence:F3} box=({w.Bounds.X:F0},{w.Bounds.Y:F0},{w.Bounds.Width:F0}x{w.Bounds.Height:F0})");
        }

        Assert.NotEmpty(words);
        Assert.All(words, w => Assert.InRange(w.Confidence, 0.01, 1.0));
        Assert.Contains("HELLO", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WORLD", joined, StringComparison.OrdinalIgnoreCase);

        // WindowsOcrService üzerinden uçtan uca ön işleme + OneOCR entegrasyonu doğrulaması
        using var service = new WindowsOcrService(
            preprocessEnabled: false,
            sourceLanguageCode: "en",
            engineKind: OcrEngineKind.OneOcr,
            minWordConfidence: 0.30);

        Assert.True(service.IsAvailable, service.Status.ErrorMessage);
        string serviceText = await service.RecognizePixelsAsync(bgra, width, height, preprocess: false);
        Assert.Contains("HELLO", serviceText, StringComparison.OrdinalIgnoreCase);

        // EnqueueFrame yolunun OneOCR modunda (_engine == null iken) kareleri düşürmediğini doğrula
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TextRecognized += r => tcs.TrySetResult(r.Text);
        service.EnqueueFrame(new Bitiq.Core.Capture.CapturedFrameData
        {
            BgraPixels = bgra,
            Width = width,
            Height = height,
            CaptureMs = 1,
            ChangeDetectMs = 0.5,
            CapturedAtTimestamp = Stopwatch.GetTimestamp()
        });

        string enqueuedResult = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("HELLO", enqueuedResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubtitleStabilizer_And_OcrBlockGrouper_FilterBackgroundUppercaseNoiseWithoutHurtingDialogue()
    {
        bool previousEnglishMode = SubtitleStabilizer.IsEnglishSourceMode;
        SubtitleStabilizer.IsEnglishSourceMode = true;
        try
        {
            // Kullanıcının gerçek OneOCR loglarından bildirilen tüm arka plan gürültü vakaları
            Assert.Equal("Yeah? Damn.\nWhat else is new?", SubtitleStabilizer.CleanRawText("INMA Yeah? Damn.\nWhat else is new?"));
            Assert.Equal("They're late.\nYeah? Damn.", SubtitleStabilizer.CleanRawText("INM\nThey're late.\nYeah? Damn."));
            Assert.Equal("You can look out there all day, girl.", SubtitleStabilizer.CleanRawText("ATE ATE\nYou can look out there all day, girl."));
            Assert.Equal(string.Empty, SubtitleStabilizer.CleanRawText("ATE"));
            Assert.Equal("I don't like to do this, but my boss told me …", SubtitleStabilizer.CleanRawText("SA I don't like to do this, but my boss told me …"));
            Assert.Equal(string.Empty, SubtitleStabilizer.CleanRawText("SA"));
            Assert.Equal("Just water.", SubtitleStabilizer.CleanRawText("SA Just water."));

            // Geçerli diyaloglar ve bilinen kısa kelimeler/nida ifadeleri korunmalı
            Assert.Equal("HELLO WORLD", SubtitleStabilizer.CleanRawText("HELLO WORLD"));
            Assert.Equal("I don't know.", SubtitleStabilizer.CleanRawText("I don't know."));
            Assert.Equal("OK Let's go.", SubtitleStabilizer.CleanRawText("OK Let's go."));
            Assert.Equal("STOP! Don't move!", SubtitleStabilizer.CleanRawText("STOP! Don't move!"));

            // Uzaysal olarak ayrı arka plan tabelası ('ATE') + altyazı bloğu birleştiğinde gürültü bloğu atılmalı
            var words = new List<OcrWordElement>
            {
                new("ATE", new OcrBoundingBox(40, 20, 48, 22), Confidence: 0.88),
                new("You", new OcrBoundingBox(320, 180, 40, 24), Confidence: 0.96),
                new("can", new OcrBoundingBox(366, 180, 38, 24), Confidence: 0.96),
                new("look", new OcrBoundingBox(410, 180, 46, 24), Confidence: 0.96),
                new("out.", new OcrBoundingBox(462, 180, 42, 24), Confidence: 0.96),
            };
            string formatted = OcrBlockGrouper.GroupAndFormat(
                words,
                new OcrGroupingOptions(MinWordConfidence: 0.45, SourceLanguageCode: "en"));
            Assert.Equal("You can look out.", formatted);
        }
        finally
        {
            SubtitleStabilizer.IsEnglishSourceMode = previousEnglishMode;
        }
    }

    private static byte[] RenderSyntheticTextBgra(string text, int width, int height)
    {
        byte[] buf = new byte[width * height * 4];
        // Koyu arka plan (#141820)
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = 32;     // B
            buf[i + 1] = 24; // G
            buf[i + 2] = 20; // R
            buf[i + 3] = 255;
        }

        const int scale = 5;
        int cursorX = 30;
        int originY = 30;

        foreach (char ch in text)
        {
            if (ch == ' ')
            {
                cursorX += 4 * scale;
                continue;
            }

            if (!BitmapFont5x7.TryGetValue(ch, out string[]? glyph))
                continue;

            for (int row = 0; row < 7; row++)
            {
                string bits = glyph[row];
                for (int col = 0; col < 5; col++)
                {
                    if (bits[col] != '#')
                        continue;

                    for (int dy = 0; dy < scale; dy++)
                    {
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int px = cursorX + (col * scale) + dx;
                            int py = originY + (row * scale) + dy;
                            if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                                continue;

                            int idx = ((py * width) + px) * 4;
                            buf[idx] = 250;
                            buf[idx + 1] = 250;
                            buf[idx + 2] = 250;
                            buf[idx + 3] = 255;
                        }
                    }
                }
            }

            cursorX += 6 * scale;
        }

        return buf;
    }

    private static readonly Dictionary<char, string[]> BitmapFont5x7 = new()
    {
        ['H'] = ["##.##", "##.##", "##.##", "#####", "##.##", "##.##", "##.##"],
        ['E'] = ["#####", "##...", "##...", "####.", "##...", "##...", "#####"],
        ['L'] = ["##...", "##...", "##...", "##...", "##...", "##...", "#####"],
        ['O'] = [".###.", "##.##", "##.##", "##.##", "##.##", "##.##", ".###."],
        ['W'] = ["#...#", "#...#", "#...#", "#.#.#", "##.##", "##.##", "#...#"],
        ['R'] = ["####.", "##.##", "##.##", "####.", "###..", "##.#.", "##..#"],
        ['D'] = ["####.", "##.##", "##.##", "##.##", "##.##", "##.##", "####."]
    };
}
