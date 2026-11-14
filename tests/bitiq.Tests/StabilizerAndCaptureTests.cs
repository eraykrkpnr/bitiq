using System.Diagnostics;
using Bitiq.Core.Capture;
using Bitiq.Core.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Bitiq.Tests;

public sealed class StabilizerAndCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void Stabilizer_SuppressesOcrNoiseAbove90PercentSimilarity()
    {
        var stabilizer = new SubtitleStabilizer(requiredFrames: 1, debounceMs: 0, similarityThreshold: 0.90);

        var first = stabilizer.Process("Do not open the ancient gate right now.", 100);
        Assert.Equal(StabilizerAction.Translate, first.Action);

        // 1 karakter OCR gürültüsü ("r1ght") -> benzerlik > 0.97 -> tekrar çevrilmemeli
        var noisy = stabilizer.Process("Do not open the ancient gate r1ght now.", 120);
        Assert.Equal(StabilizerAction.None, noisy.Action);

        // Tamamen farklı yeni satır -> çevrilmeli
        var changed = stabilizer.Process("Run! The dragon is coming!", 200);
        Assert.Equal(StabilizerAction.Translate, changed.Action);
    }

    [Fact]
    public void Stabilizer_RespectsMultiFrameStabilityAndDebounce()
    {
        var stabilizer = new SubtitleStabilizer(requiredFrames: 2, debounceMs: 150, similarityThreshold: 0.90);

        var d1 = stabilizer.Process("Hello", 100);
        Assert.Equal(StabilizerAction.None, d1.Action);

        var d2 = stabilizer.Process("Hello", 130); // 2. kare ama debounce (150ms) dolmadı
        Assert.Equal(StabilizerAction.None, d2.Action);

        var d3 = stabilizer.PollDebounce(260); // 100 + 150 <= 260 -> artık onaylanmalı
        Assert.Equal(StabilizerAction.Translate, d3.Action);
        Assert.Equal("Hello", d3.RawText);

        // Boş geldiğinde Clear üretmeli
        var clear = stabilizer.Process("", 300);
        Assert.Equal(StabilizerAction.Clear, clear.Action);
    }

    [Fact]
    public void FrameChangeDetector_DetectsDifferenceOnlyWhenAboveThreshold()
    {
        var detector = new FrameChangeDetector();
        int w = 64, h = 16, stride = w * 4;
        var frameA = new byte[h * stride];
        Array.Fill<byte>(frameA, 20);

        Assert.True(detector.HasSignificantChange(frameA, w, h, stride, 2.0, out _));
        // Aynı kare: fark 0 -> false
        Assert.False(detector.HasSignificantChange(frameA, w, h, stride, 2.0, out double diffSame));
        Assert.Equal(0, diffSame);

        // Parlak piksel değişimi ekle
        var frameB = (byte[])frameA.Clone();
        Array.Fill<byte>(frameB, 200);
        Assert.True(detector.HasSignificantChange(frameB, w, h, stride, 2.0, out double diffChanged));
        Assert.True(diffChanged > 100);
    }

    [Fact]
    public void JoinOcrLines_MergesWrappedSentenceButPreservesSeparateDialogue()
    {
        string wrapped = WindowsOcrService.JoinOcrLines(["The ancient relics are hidden beneath the temple of", "shadows."]);
        Assert.Equal("The ancient relics are hidden beneath the temple of shadows.", wrapped);

        string dialogue = WindowsOcrService.JoinOcrLines(["- Wait! Don't move!", "- Why? What's wrong?"]);
        Assert.Equal("- Wait! Don't move!\n- Why? What's wrong?", dialogue);
    }

    [Fact]
    public async Task VideoOcrBenchmark_TestMp4_AchievesAtLeast90PercentOverlapWithGroundTruth()
    {
        string? repoRoot = FindRepoRootWithTestMedia();
        if (repoRoot is null)
        {
            output.WriteLine("Skipping benchmark: test.mp4 or test.txt not found.");
            return;
        }

        string videoPath = Path.Combine(repoRoot, "test.mp4");
        string truthPath = Path.Combine(repoRoot, "test.txt");
        string groundTruth = await File.ReadAllTextAsync(truthPath);

        const int frameW = 800;
        const int frameH = 60;
        const int cropX = 240;
        const int cropY = 654;
        const int frameBytes = frameW * frameH * 4;

        using var ocr = new WindowsOcrService(preprocessEnabled: true, sourceLanguageCode: "en");
        if (!ocr.IsAvailable)
        {
            output.WriteLine($"Skipping benchmark: OCR unavailable ({ocr.Status.ErrorMessage}).");
            return;
        }

        var stabilizer = new SubtitleStabilizer();
        stabilizer.Configure(requiredFrames: 1, debounceMs: 0);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-v error -i \"{videoPath}\" -vf \"fps=3,crop={frameW}:{frameH}:{cropX}:{cropY}\" -f rawvideo -pix_fmt bgra -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var frameBuf = new byte[frameBytes];
        var committed = new List<string>();
        bool segmentActive = false;
        int frameCount = 0;
        var ocrSw = Stopwatch.StartNew();

        Stream stdout = proc.StandardOutput.BaseStream;
        while (await ReadExactAsync(stdout, frameBuf) == frameBytes)
        {
            string rawText = await ocr.RecognizePixelsAsync(frameBuf, frameW, frameH, preprocess: true);
            long nowMs = frameCount * 333L;
            var decision = stabilizer.Process(rawText, nowMs);

            if (decision.Action == StabilizerAction.Clear)
            {
                segmentActive = false;
            }
            else if (decision.Action == StabilizerAction.Translate)
            {
                string flat = decision.RawText.Replace('\n', ' ').Trim();
                if (segmentActive
                    && committed.Count > 0
                    && stabilizer.IsSameSubtitle(
                        SubtitleStabilizer.NormalizeForComparison(committed[^1]),
                        decision.NormalizedText))
                {
                    // Aktif altyazı içi temizleme yükseltmesi: ekrandaki mevcut altyazıyı güncelle
                    committed[^1] = flat;
                }
                else
                {
                    committed.Add(flat);
                    segmentActive = true;
                }
            }

            frameCount++;
        }

        ocrSw.Stop();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.True(proc.ExitCode == 0, $"ffmpeg failed ({proc.ExitCode}): {stderr}");
        Assert.True(frameCount > 300, $"Expected > 300 frames, got {frameCount}");

        string recognized = string.Join(" ", committed);
        string normGt = NormalizeBenchmarkText(groundTruth);
        string normActual = NormalizeBenchmarkText(recognized);

        double charSimilarity = SubtitleStabilizer.ComputeSimilarity(normGt, normActual);
        var gtWords = normGt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var actualWords = normActual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matchedWords = ComputeWordLcsLength(gtWords, actualWords);
        double wordOverlap = gtWords.Length == 0 ? 1.0 : (double)matchedWords / gtWords.Length;
        double avgMsPerFrame = ocrSw.Elapsed.TotalMilliseconds / frameCount;

        output.WriteLine($"Frames: {frameCount}, Avg OCR+Pipeline/frame: {avgMsPerFrame:F2} ms");
        output.WriteLine($"Normalized Char Similarity: {charSimilarity:P2}");
        output.WriteLine($"Word LCS Overlap: {wordOverlap:P2} ({matchedWords}/{gtWords.Length})");
        output.WriteLine($"Recognized Text: {recognized}");

        Assert.True(charSimilarity >= 0.90, $"Char similarity {charSimilarity:P2} < 90%. Recognized: {recognized}");
        Assert.True(wordOverlap >= 0.90, $"Word overlap {wordOverlap:P2} < 90%. Recognized: {recognized}");
    }

    private static string? FindRepoRootWithTestMedia()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "test.mp4"))
                && File.Exists(Path.Combine(dir.FullName, "test.txt")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
            if (n == 0)
                break;
            totalRead += n;
        }
        return totalRead;
    }

    private static string NormalizeBenchmarkText(string text)
    {
        var chars = new List<char>(text.Length);
        bool prevSpace = false;
        foreach (char ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                chars.Add(ch);
                prevSpace = false;
            }
            else if (char.IsWhiteSpace(ch) && !prevSpace && chars.Count > 0)
            {
                chars.Add(' ');
                prevSpace = true;
            }
        }
        return new string(chars.ToArray()).Trim();
    }

    private static int ComputeWordLcsLength(string[] a, string[] b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                curr[j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], curr[j - 1]);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
