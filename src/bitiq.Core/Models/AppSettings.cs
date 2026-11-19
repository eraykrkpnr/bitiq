using System.Text.Json.Serialization;

namespace Bitiq.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationEngineKind
{
    Local,
    Google
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalModelKind
{
    TranslateGemma4B,
    HyMt2_7B
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelQuantization
{
    Q4_K_M,
    Q6_K,
    Q8_0
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalBackendKind
{
    Auto,
    Cuda,
    Vulkan,
    Cpu
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrEngineKind
{
    WindowsOcr,
    OneOcr
}

public sealed class AppSettings
{
    public bool FirstRunCompleted { get; set; }
    public TranslationEngineKind TranslationEngine { get; set; } = TranslationEngineKind.Google;
    public LocalModelKind LocalModel { get; set; } = LocalModelKind.HyMt2_7B;
    public ModelQuantization LocalModelQuantization { get; set; } = ModelQuantization.Q6_K;
    public LocalBackendKind LocalBackend { get; set; } = LocalBackendKind.Auto;
    public bool SimulateVramLimitEnabled { get; set; } = false;
    public double SimulatedVramLimitGb { get; set; } = 10.0;

    // Bağlam (Context) Ayarları
    public bool ContextEnabled { get; set; } = true;
    public int ContextMaxLines { get; set; } = 8;
    public int ContextTrimToLines { get; set; } = 4;
    public int ContextIdleResetSeconds { get; set; } = 90;
    public string? ActiveGameProfileId { get; set; }

    public string UiLanguage { get; set; } = "tr";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "tr";

    public CaptureRegion? CaptureRegion { get; set; }
    public OverlayWindowBounds OverlayBounds { get; set; } = new();

    // Gecikme ve Kararlılık
    public int StabilityFrames { get; set; } = 1; // 1 (en hızlı), 2, 3
    public int TypewriterDebounceMs { get; set; } = 0; // 0 = kapalı
    public bool StreamingEnabled { get; set; } = true;
    public bool MergeIncompleteSentencesEnabled { get; set; } = true;
    public bool OcrPreprocessEnabled { get; set; } = true;
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.WindowsOcr;
    public double OcrMinConfidence { get; set; } = 0.45;
    public bool SmartOcrDefaultApplied { get; set; } = false;
    public double FrameChangeThreshold { get; set; } = 1.2; // 0..255 arası ortalama gri tonlama farkı
    public int LlamaThreads { get; set; } = 4;
    public int LlamaPort { get; set; } = 19556;

    // Gösterim ve OBS
    public bool ShowScreenOverlay { get; set; } = true;
    public bool ShowObsChromaWindow { get; set; } = false;
    public int ObsPort { get; set; } = 19555;
    public string ObsChromaColorHex { get; set; } = "#00FF00";

    // Stil
    public StyleProfile ActiveStyle { get; set; } = StyleProfile.CreateBuiltInPresets()[0];
    public List<StyleProfile> SavedProfiles { get; set; } = StyleProfile.CreateBuiltInPresets();
}
