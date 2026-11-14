using Bitiq.Core.Capture;
using Bitiq.Core.Ocr;
using Xunit;

namespace Bitiq.Tests;

public sealed class StabilizerAndCaptureTests
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
}
