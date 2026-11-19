using Bitiq.Core.Translation;
using Xunit;

namespace Bitiq.Tests;

public sealed class ConversationContextTrackerTests
{
    [Fact]
    public void Commit_ReplacesOcrUpgradeAndContinuationInsteadOfDuplicating()
    {
        var tracker = new ConversationContextTracker();
        tracker.Configure(maxLines: 8, trimToLines: 4, idleResetSeconds: 90);

        // İlk parça (OCR gürültülü)
        Assert.Equal(1, tracker.Commit("We need to g3t out", "Buradan çıkmamız", nowMs: 1000));
        // Daha temiz OCR okuması (benzerlik >= 0.85) yeni satır eklemek yerine son satırı güncellemeli
        Assert.Equal(1, tracker.Commit("We need to get out!", "Buradan çıkmamız lazım!", nowMs: 1200));

        // Devam cümlesi geldiğinde: Snapshot önceki öneki dışarıda bırakmalı, Commit son satırı değiştirmeli
        var snap = tracker.Snapshot(profile: null, currentSource: "We need to get out! Before they find us.", nowMs: 1500);
        Assert.Empty(snap.History);

        Assert.Equal(1, tracker.Commit("We need to get out! Before they find us.", "Bizi bulmadan buradan çıkmamız lazım!", nowMs: 1600));
        var snapNext = tracker.Snapshot(profile: null, currentSource: "Follow me.", nowMs: 2000);
        Assert.Single(snapNext.History);
        Assert.Equal("Bizi bulmadan buradan çıkmamız lazım!", snapNext.History[0].Target);
    }

    [Fact]
    public void Commit_AppliesHysteresisTrimFrom8To4AndRejectsGarbage()
    {
        var tracker = new ConversationContextTracker();
        tracker.Configure(maxLines: 8, trimToLines: 4, idleResetSeconds: 90);

        // Çöp girdiler reddedilmeli
        Assert.Equal(0, tracker.Commit("...", "...", nowMs: 100));
        Assert.Equal(0, tracker.Commit("1234", "1234", nowMs: 200));
        Assert.Equal(0, tracker.Commit("A", "A", nowMs: 300));

        string[] lines =
        [
            "Who is out there in the dark?",
            "Keep your voice down right now.",
            "Did you hear footsteps upstairs?",
            "Grab the flashlight from the table.",
            "There is someone behind that door.",
            "Hold on, let me check the lock.",
            "Everything seems quiet for now.",
            "Stay close and watch my back.",
            "Let us head toward the main gate."
        ];

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i + 1, tracker.Commit(lines[i], $"Çeviri {i + 1}", nowMs: 1000 + i * 500));
        }

        // 9. satırda MaxLines (8) aşılır -> en eski 5 düşer, son 4 kalır (indeks 5, 6, 7, 8)
        int afterOverflow = tracker.Commit(lines[8], "Çeviri 9", nowMs: 6000);
        Assert.Equal(4, afterOverflow);

        var snap = tracker.Snapshot(profile: null, currentSource: "What happens next?", nowMs: 6200);
        Assert.Equal(4, snap.History.Count);
        Assert.Equal(lines[5], snap.History[0].Source);
        Assert.Equal(lines[8], snap.History[3].Source);
    }

    [Fact]
    public void Snapshot_ResetsHistoryAfterIdleTimeout()
    {
        var tracker = new ConversationContextTracker();
        tracker.Configure(maxLines: 8, trimToLines: 4, idleResetSeconds: 90);

        tracker.Commit("Hello there.", "Merhaba.", nowMs: 10_000);
        Assert.Single(tracker.Snapshot(null, "Next line", nowMs: 50_000).History);

        // 90 saniyeden uzun boşluk sonrası bağlam temizlenmeli
        var afterIdle = tracker.Snapshot(null, "New scene line", nowMs: 105_000);
        Assert.Empty(afterIdle.History);
        Assert.Equal(0, tracker.Count);
    }
}
