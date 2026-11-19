using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bitiq.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleTextAlign
{
    Left,
    Center,
    Right
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleBackgroundMode
{
    None,
    PerLineBox,
    SingleBlock,
    FullWidthBar
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitlePositionPreset
{
    BottomCenter,
    TopCenter,
    Center,
    BottomLeft,
    BottomRight,
    TopLeft,
    TopRight
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleOverflowMode
{
    ShrinkToFit,
    Clip
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleAnimationType
{
    None,
    Fade,
    Slide
}

public sealed class StyleProfile
{
    public string Name { get; set; } = "Modern Koyu Kutu";
    public bool IsBuiltIn { get; set; }

    // Yazı
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 36;
    public int FontWeight { get; set; } = 700;
    public bool IsItalic { get; set; }
    public double LetterSpacing { get; set; } = 0.0;
    public double LineHeight { get; set; } = 1.25;
    public bool Uppercase { get; set; }
    public SubtitleTextAlign TextAlignment { get; set; } = SubtitleTextAlign.Center;

    // Renk ve Efektler
    public string TextColorHex { get; set; } = "#FFFFFF";
    public double TextOpacity { get; set; } = 100;
    public string OutlineColorHex { get; set; } = "#000000";
    public double OutlineThickness { get; set; } = 2.0;
    public string ShadowColorHex { get; set; } = "#000000";
    public double ShadowOpacity { get; set; } = 85;
    public double ShadowBlur { get; set; } = 6.0;
    public double ShadowOffsetX { get; set; } = 2.0;
    public double ShadowOffsetY { get; set; } = 2.0;

    // Arka Plan
    public SubtitleBackgroundMode BackgroundMode { get; set; } = SubtitleBackgroundMode.SingleBlock;
    public string BackgroundColorHex { get; set; } = "#0B0E14";
    public double BackgroundOpacity { get; set; } = 78;
    public double BorderRadius { get; set; } = 10;
    public double PaddingHorizontal { get; set; } = 20;
    public double PaddingVertical { get; set; } = 10;
    public string BorderColorHex { get; set; } = "#FFFFFF";
    public double BorderThickness { get; set; } = 0;

    // Yerleşim
    public SubtitlePositionPreset PositionPreset { get; set; } = SubtitlePositionPreset.BottomCenter;
    public double OffsetXPercent { get; set; } = 0;
    public double OffsetYPercent { get; set; } = 0;
    public double MaxWidthPercent { get; set; } = 84;
    public int MaxLines { get; set; } = 3;
    public SubtitleOverflowMode OverflowMode { get; set; } = SubtitleOverflowMode.ShrinkToFit;

    // Davranış
    public SubtitleAnimationType AnimationType { get; set; } = SubtitleAnimationType.Fade;
    public int AnimationDurationMs { get; set; } = 150;
    public int LingerAfterDisappearMs { get; set; } = 400;
    public int MinDisplayDurationMs { get; set; } = 500;

    // Orijinal metin (küçük ve ayrı stil)
    public bool ShowOriginalText { get; set; }
    public double OriginalFontSize { get; set; } = 19;
    public int OriginalFontWeight { get; set; } = 500;
    public bool OriginalIsItalic { get; set; } = true;
    public string OriginalTextColorHex { get; set; } = "#D6DCE5";
    public double OriginalTextOpacity { get; set; } = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public StyleProfile Clone(string? newName = null, bool? isBuiltIn = null)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var copy = JsonSerializer.Deserialize<StyleProfile>(json, JsonOptions) ?? new StyleProfile();
        if (newName is not null)
            copy.Name = newName;
        if (isBuiltIn.HasValue)
            copy.IsBuiltIn = isBuiltIn.Value;
        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static StyleProfile? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var profile = JsonSerializer.Deserialize<StyleProfile>(json, JsonOptions);
        if (profile is not null)
            profile.IsBuiltIn = false;
        return profile;
    }

    public static List<StyleProfile> CreateBuiltInPresets() =>
    [
        new StyleProfile
        {
            Name = "Modern Koyu Kutu",
            IsBuiltIn = true,
            FontFamily = "Segoe UI",
            FontSize = 36,
            FontWeight = 600,
            TextColorHex = "#FFFFFF",
            TextOpacity = 100,
            OutlineColorHex = "#000000",
            OutlineThickness = 1.5,
            ShadowColorHex = "#000000",
            ShadowOpacity = 70,
            ShadowBlur = 5,
            ShadowOffsetX = 1,
            ShadowOffsetY = 2,
            BackgroundMode = SubtitleBackgroundMode.SingleBlock,
            BackgroundColorHex = "#0B0E14",
            BackgroundOpacity = 78,
            BorderRadius = 10,
            PaddingHorizontal = 22,
            PaddingVertical = 10,
            BorderColorHex = "#334155",
            BorderThickness = 1,
            PositionPreset = SubtitlePositionPreset.BottomCenter
        },
        new StyleProfile
        {
            Name = "Klasik Sinema",
            IsBuiltIn = true,
            FontFamily = "Arial",
            FontSize = 38,
            FontWeight = 700,
            TextColorHex = "#FFF6D1",
            TextOpacity = 100,
            OutlineColorHex = "#000000",
            OutlineThickness = 3.0,
            ShadowColorHex = "#000000",
            ShadowOpacity = 95,
            ShadowBlur = 6,
            ShadowOffsetX = 2,
            ShadowOffsetY = 2,
            BackgroundMode = SubtitleBackgroundMode.None,
            BackgroundColorHex = "#000000",
            BackgroundOpacity = 0,
            BorderRadius = 0,
            PaddingHorizontal = 12,
            PaddingVertical = 6,
            PositionPreset = SubtitlePositionPreset.BottomCenter
        },
        new StyleProfile
        {
            Name = "Satır Başına Kutu (YouTube)",
            IsBuiltIn = true,
            FontFamily = "Segoe UI",
            FontSize = 34,
            FontWeight = 600,
            TextColorHex = "#FFFFFF",
            TextOpacity = 100,
            OutlineColorHex = "#000000",
            OutlineThickness = 0,
            ShadowColorHex = "#000000",
            ShadowOpacity = 60,
            ShadowBlur = 3,
            ShadowOffsetX = 1,
            ShadowOffsetY = 1,
            BackgroundMode = SubtitleBackgroundMode.PerLineBox,
            BackgroundColorHex = "#080808",
            BackgroundOpacity = 84,
            BorderRadius = 6,
            PaddingHorizontal = 14,
            PaddingVertical = 5,
            PositionPreset = SubtitlePositionPreset.BottomCenter
        },
        new StyleProfile
        {
            Name = "Yüksek Kontrast Yayın Şeridi",
            IsBuiltIn = true,
            FontFamily = "Segoe UI",
            FontSize = 38,
            FontWeight = 800,
            TextColorHex = "#FFFFFF",
            TextOpacity = 100,
            OutlineColorHex = "#000000",
            OutlineThickness = 2.0,
            ShadowColorHex = "#000000",
            ShadowOpacity = 85,
            ShadowBlur = 4,
            ShadowOffsetX = 2,
            ShadowOffsetY = 2,
            BackgroundMode = SubtitleBackgroundMode.FullWidthBar,
            BackgroundColorHex = "#090D16",
            BackgroundOpacity = 88,
            BorderRadius = 0,
            PaddingHorizontal = 32,
            PaddingVertical = 14,
            BorderColorHex = "#38BDF8",
            BorderThickness = 1.5,
            PositionPreset = SubtitlePositionPreset.BottomCenter,
            MaxWidthPercent = 100
        }
    ];
}
