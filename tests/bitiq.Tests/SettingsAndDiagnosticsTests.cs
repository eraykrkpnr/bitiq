using System.Net.Http;
using Bitiq.Core.Models;
using Bitiq.Core.Obs;
using Bitiq.Core.Services;
using Bitiq.Core.Translation;
using Xunit;

namespace Bitiq.Tests;

public sealed class SettingsAndDiagnosticsTests
{
    [Fact]
    public void SettingsService_SavesAndLoadsJsonInCustomDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bitiqTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new SettingsService(tempDir);
            var settings = svc.Load();
            settings.FirstRunCompleted = true;
            settings.TranslationEngine = TranslationEngineKind.Local;
            settings.StabilityFrames = 2;
            settings.TypewriterDebounceMs = 200;
            settings.ActiveStyle.FontSize = 44;
            svc.Save(settings);

            var reloaded = svc.Load();
            Assert.True(reloaded.FirstRunCompleted);
            Assert.Equal(TranslationEngineKind.Local, reloaded.TranslationEngine);
            Assert.Equal(2, reloaded.StabilityFrames);
            Assert.Equal(200, reloaded.TypewriterDebounceMs);
            Assert.Equal(44, reloaded.ActiveStyle.FontSize);
            Assert.True(reloaded.SavedProfiles.Count >= 4);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiagnosticsTracker_ComputesAvgAndP95OverLast50Records()
    {
        var tracker = new DiagnosticsTracker(50);
        // 60 kayıt ekle -> ilk 10 düşmeli, son 50 kalmalı (11..60)
        for (int i = 1; i <= 60; i++)
        {
            tracker.Add(new TelemetryRecord
            {
                Engine = "Test",
                CaptureMs = i,
                OcrMs = i * 0.5,
                FirstTokenMs = i * 2,
                TotalTranslateMs = i * 4,
                RenderMs = 1,
                TotalEndToEndMs = i * 5
            });
        }

        var summary = tracker.GetSummary();
        Assert.Equal(50, summary.SampleCount);
        Assert.Equal(35.5, summary.Capture.AverageMs); // avg(11..60) = 35.5
        Assert.Equal(58.0, summary.Capture.P95Ms);     // ceil(50 * 0.95) = 48. eleman -> 10+48 = 58
    }

    [Fact]
    public async Task ObsHttpServer_StreamsSseEventsToConnectedClients()
    {
        using var server = new ObsHttpServer(new StyleProfile());
        server.Start(preferredPort: 0);

        using var client = new HttpClient();
        using var resp = await client.GetAsync(server.Url + "events", HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // İlk anlık style ve subtitle SSE olaylarını oku
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (lines.Count < 6 && !cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is not null)
                lines.Add(line);
        }

        string initialEvents = string.Join("\n", lines);
        Assert.Contains("event: style", initialEvents);
        Assert.Contains("event: subtitle", initialEvents);

        // Canlı yeni altyazı gönder ve SSE akışından geldiğini doğrula
        server.PublishSubtitle(new SubtitleUpdateEvent
        {
            SequenceId = 42,
            EnglishText = "Test line",
            TurkishText = "Canlı SSE testi",
            IsCleared = false
        });

        string received = string.Empty;
        for (int i = 0; i < 10; i++)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            received += line + "\n";
            if (received.Contains("Canlı SSE testi"))
                break;
        }

        Assert.Contains("Canlı SSE testi", received);
    }

    [Fact]
    public void ModelCatalog_ProvidesValidNonEmptyUrlsAndSha256()
    {
        var q4 = ModelCatalog.GetModelSpec(ModelQuantization.Q4_K_M);
        var q8 = ModelCatalog.GetModelSpec(ModelQuantization.Q8_0);
        Assert.Equal(64, q4.ExpectedSha256.Length);
        Assert.Equal(64, q8.ExpectedSha256.Length);
        Assert.StartsWith("https://huggingface.co/mradermacher/translategemma-4b-it-GGUF/", q4.Url);
        Assert.StartsWith("https://huggingface.co/mradermacher/translategemma-4b-it-GGUF/", q8.Url);
    }
}
