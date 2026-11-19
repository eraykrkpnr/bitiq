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
        var q6 = ModelCatalog.GetModelSpec(ModelQuantization.Q6_K);
        var q8 = ModelCatalog.GetModelSpec(ModelQuantization.Q8_0);
        Assert.Equal(64, q4.ExpectedSha256.Length);
        Assert.Equal(64, q6.ExpectedSha256.Length);
        Assert.Equal(64, q8.ExpectedSha256.Length);
        Assert.StartsWith("https://huggingface.co/mradermacher/translategemma-4b-it-GGUF/", q4.Url);
        Assert.StartsWith("https://huggingface.co/tencent/Hy-MT2-7B-GGUF/", q6.Url);
        Assert.StartsWith("https://huggingface.co/mradermacher/translategemma-4b-it-GGUF/", q8.Url);

        // Hy-MT2 7B sadece Q6_K, TranslateGemma 4B sadece Q8_0 / Q4_K_M kullanmalı
        Assert.Equal(ModelQuantization.Q6_K, ModelCatalog.ResolveQuantization(LocalModelKind.HyMt2_7B, ModelQuantization.Q8_0));
        Assert.Equal(ModelQuantization.Q6_K, ModelCatalog.ResolveQuantization(LocalModelKind.HyMt2_7B, ModelQuantization.Q4_K_M));
        Assert.Equal(ModelQuantization.Q8_0, ModelCatalog.ResolveQuantization(LocalModelKind.TranslateGemma4B, ModelQuantization.Q6_K));
        Assert.Equal(ModelQuantization.Q4_K_M, ModelCatalog.ResolveQuantization(LocalModelKind.TranslateGemma4B, ModelQuantization.Q4_K_M));
    }

    [Fact]
    public void SettingsService_PreservesTranslateGemmaForExistingUsersWithoutLocalModelField()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bitiqMigration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Eski sürüm settings.json simülasyonu (LocalModel alanı yok, FirstRunCompleted = true)
            File.WriteAllText(Path.Combine(tempDir, "settings.json"), """
            {
              "FirstRunCompleted": true,
              "TranslationEngine": "Local",
              "LocalModelQuantization": "Q8_0",
              "SmartOcrDefaultApplied": true
            }
            """);

            var svc = new SettingsService(tempDir);
            var migrated = svc.Load();
            Assert.Equal(LocalModelKind.TranslateGemma4B, migrated.LocalModel);
            Assert.Equal(ModelQuantization.Q8_0, migrated.LocalModelQuantization);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsService_FreshInstallDefaultsToHyMt2Q6KAndContextEnabled()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bitiqFresh_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new SettingsService(tempDir);
            var fresh = svc.Load();
            Assert.Equal(LocalModelKind.HyMt2_7B, fresh.LocalModel);
            Assert.Equal(ModelQuantization.Q6_K, fresh.LocalModelQuantization);
            Assert.True(fresh.ContextEnabled);
            Assert.Equal(8, fresh.ContextMaxLines);
            Assert.Equal(4, fresh.ContextTrimToLines);
            Assert.Equal(90, fresh.ContextIdleResetSeconds);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GameProfileService_SavesLoadsExportsImportsAndDeletesProfiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bitiqProfiles_" + Guid.NewGuid().ToString("N"));
        string exportPath = Path.Combine(tempDir, "exported.json");
        try
        {
            var svc = new GameProfileService(Path.Combine(tempDir, "profiles"));
            var profile = new GameProfile
            {
                Id = "elden_ring",
                Title = "Elden Ring",
                Synopsis = "Dark fantasy action RPG",
                ToneNote = "Archaic formal Turkish where appropriate",
                Characters = [new CharacterEntry("Melina", "female")],
                Glossary = [new GlossaryEntry("Tarnished", "Lekelenmiş")]
            };

            svc.Save(profile);
            var loaded = svc.LoadAll();
            Assert.Single(loaded);
            Assert.Equal("Elden Ring", loaded[0].Title);
            Assert.Equal("Tarnished", loaded[0].Glossary[0].Source);

            svc.ExportToFile(profile, exportPath);
            var imported = svc.ImportFromFile(exportPath);
            Assert.NotEqual(profile.Id, imported.Id);
            Assert.Equal(2, svc.LoadAll().Count);

            svc.Delete(imported.Id);
            Assert.Single(svc.LoadAll());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ComputeGpuOffloadPlan_Fits10GbRtx3080CleanAndShiftsToRamWhenVramIsOccupied()
    {
        // Senaryo 1: Temiz RTX 3080 10 GB (OBS + Windows ~1.8 GB kullanıyor, 8160 MB boş) -> Tam GPU (99)
        var clean3080 = new VramBudgetSnapshot(true, "RTX 3080", TotalMb: 10240, UsedMb: 1800, FreeMb: 8160);
        var planClean = LlamaServerManager.ComputeGpuOffloadPlan(LocalModelKind.HyMt2_7B, ModelQuantization.Q6_K, clean3080);
        Assert.Equal(99, planClean.GpuLayers);
        Assert.True(planClean.IsFullGpu);

        // Senaryo 2: RTX 3080 10 GB'da arka plan uygulamaları 4130 MB doldurmuş (5830 MB boş) -> 400 MB boş bırakarak maksimum katman GPU'ya alınır
        var busy3080 = new VramBudgetSnapshot(true, "RTX 3080", TotalMb: 10240, UsedMb: 4130, FreeMb: 5830);
        var planBusy = LlamaServerManager.ComputeGpuOffloadPlan(LocalModelKind.HyMt2_7B, ModelQuantization.Q6_K, busy3080);
        Assert.Equal(27, planBusy.GpuLayers);
        Assert.False(planBusy.IsFullGpu);
        Assert.True(planBusy.EstimatedVramMb <= busy3080.FreeMb - 400);

        // Senaryo 3: RTX 5070 12 GB'da 4130 MB dolu (7814 MB boş) -> 400 MB rezervle 7414 MB bütçe >= 6591 MB -> Tam GPU (99)
        var busy5070 = new VramBudgetSnapshot(true, "RTX 5070", TotalMb: 12227, UsedMb: 4130, FreeMb: 7814);
        var plan5070 = LlamaServerManager.ComputeGpuOffloadPlan(LocalModelKind.HyMt2_7B, ModelQuantization.Q6_K, busy5070);
        Assert.Equal(99, plan5070.GpuLayers);
        Assert.True(plan5070.IsFullGpu);

        // Senaryo 4: Boş VRAM 1000 MB -> 400 MB rezervle 600 MB bütçe (580 MB overhead + < 175 MB katman) -> Tam RAM/CPU (0 GPU katmanı)
        var fullGpu = new VramBudgetSnapshot(true, "RTX 3080", TotalMb: 10240, UsedMb: 9000, FreeMb: 1000);
        var planZero = LlamaServerManager.ComputeGpuOffloadPlan(LocalModelKind.HyMt2_7B, ModelQuantization.Q6_K, fullGpu);
        Assert.Equal(0, planZero.GpuLayers);
    }

    [Fact]
    public void ApplySimulatedVramLimit_CapsTotalAndShiftsOffloadWhilePersistingSettings()
    {
        var real5070 = new VramBudgetSnapshot(true, "NVIDIA GeForce RTX 5070", TotalMb: 12227, UsedMb: 4130, FreeMb: 7814);

        // Simülasyon kapalıyken gerçek kart korunur
        var unsimulated = PerformanceMonitorService.ApplySimulatedVramLimit(real5070, simulateEnabled: false, simulatedLimitGb: 10.0);
        Assert.Equal(12227, unsimulated.TotalMb);
        Assert.Equal(7814, unsimulated.FreeMb);

        // 10 GB simülasyon: gerçek dolu bellek (4130 MB) + sürücü rezervi (283 MB) korunur -> 5827 MB boş bütçe
        var simulated10 = PerformanceMonitorService.ApplySimulatedVramLimit(real5070, simulateEnabled: true, simulatedLimitGb: 10.0);
        Assert.Equal(10240, simulated10.TotalMb);
        Assert.Equal(4130, simulated10.UsedMb);
        Assert.Equal(5827, simulated10.FreeMb);
        Assert.Contains("Simüle 10 GB", simulated10.GpuName);

        var planReal = LlamaServerManager.ComputeGpuOffloadPlan(LocalModelKind.HyMt2_7B, ModelQuantization.Q6_K, unsimulated);
        var planSim = LlamaServerManager.ComputeGpuOffloadPlan(LocalModelKind.HyMt2_7B, ModelQuantization.Q6_K, simulated10);
        Assert.Equal(99, planReal.GpuLayers);
        Assert.Equal(27, planSim.GpuLayers);
        Assert.True(planSim.GpuLayers < planReal.TotalTransformerLayers);

        // SettingsService clamp & round-trip doğrulaması
        string tempDir = Path.Combine(Path.GetTempPath(), "bitiqSimVram_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new SettingsService(tempDir);
            var settings = svc.Load();
            settings.SimulateVramLimitEnabled = true;
            settings.SimulatedVramLimitGb = 99.0;
            svc.Save(settings);

            var reloaded = svc.Load();
            Assert.True(reloaded.SimulateVramLimitEnabled);
            Assert.Equal(24.0, reloaded.SimulatedVramLimitGb);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsService_DefaultUsesApplicationDirectoryAndNeverLocalAppData()
    {
        var svc = new SettingsService();
        string expectedDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(expectedDir, svc.SettingsDirectory);
        Assert.Equal(Path.Combine(expectedDir, "settings.json"), svc.SettingsFilePath);
        Assert.Equal(Path.Combine(expectedDir, "profiles"), svc.ProfilesDirectory);
        Assert.Equal(Path.Combine(expectedDir, "models"), svc.ModelsDirectory);
        Assert.Equal(Path.Combine(expectedDir, "llama"), svc.LlamaDirectory);
        Assert.DoesNotContain(localAppData, svc.SettingsDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
