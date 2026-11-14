using System.Globalization;
using System.Text.Json;
using Bitiq.Core.Models;

namespace Bitiq.Core.Styling;

public sealed record MappedCssStyle(
    string FontFamilyCss,
    string FontSizeVh,
    int FontWeight,
    string FontStyle,
    string LetterSpacingVh,
    string LineHeight,
    string TextTransform,
    string TextAlign,
    string TextColorRgba,
    string OutlineWidthVh,
    string OutlineColorHex,
    string TextShadowCss,
    string BackgroundMode,
    string BackgroundRgba,
    string BorderRadiusVh,
    string PaddingVerticalVh,
    string PaddingHorizontalVh,
    string BorderCss,
    string PositionPreset,
    string OffsetXPercent,
    string OffsetYPercent,
    string MaxWidthPercent,
    int MaxLines,
    string OverflowMode,
    string AnimationType,
    int AnimationDurationMs,
    bool ShowOriginalText,
    string OriginalFontSizeVh,
    int OriginalFontWeight,
    string OriginalFontStyle,
    string OriginalTextColorRgba);

public static class StyleCssMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ToVh(double pxAt1080p) =>
        (pxAt1080p / 1080.0 * 100.0).ToString("0.###", CultureInfo.InvariantCulture) + "vh";

    public static string HexToRgba(string? hex, double opacityPercent)
    {
        double alpha = Math.Clamp(opacityPercent, 0, 100) / 100.0;
        var (r, g, b) = ParseHexRgb(hex);
        return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {alpha:0.###})");
    }

    public static (byte R, byte G, byte B) ParseHexRgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return (255, 255, 255);

        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 8)
            clean = clean[2..]; // AARRGGBB -> RRGGBB

        if (clean.Length == 6 &&
            byte.TryParse(clean[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(clean[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(clean[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return (r, g, b);
        }

        if (clean.Length == 3 &&
            byte.TryParse($"{clean[0]}{clean[0]}", NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r3) &&
            byte.TryParse($"{clean[1]}{clean[1]}", NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g3) &&
            byte.TryParse($"{clean[2]}{clean[2]}", NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b3))
        {
            return (r3, g3, b3);
        }

        return (255, 255, 255);
    }

    public static MappedCssStyle Map(StyleProfile profile)
    {
        string shadowRgba = HexToRgba(profile.ShadowColorHex, profile.ShadowOpacity);
        string textShadow = profile.ShadowOpacity > 0
            ? $"{ToVh(profile.ShadowOffsetX)} {ToVh(profile.ShadowOffsetY)} {ToVh(profile.ShadowBlur)} {shadowRgba}"
            : "none";

        string bgRgba = profile.BackgroundMode == SubtitleBackgroundMode.None
            ? "rgba(0, 0, 0, 0)"
            : HexToRgba(profile.BackgroundColorHex, profile.BackgroundOpacity);

        string borderCss = profile.BorderThickness > 0 && profile.BackgroundMode != SubtitleBackgroundMode.None
            ? $"{ToVh(profile.BorderThickness)} solid {profile.BorderColorHex}"
            : "none";

        string safeFont = string.IsNullOrWhiteSpace(profile.FontFamily)
            ? "'Segoe UI', sans-serif"
            : $"'{profile.FontFamily.Replace("'", "")}', 'Segoe UI', sans-serif";

        return new MappedCssStyle(
            FontFamilyCss: safeFont,
            FontSizeVh: ToVh(profile.FontSize),
            FontWeight: Math.Clamp(profile.FontWeight, 100, 900),
            FontStyle: profile.IsItalic ? "italic" : "normal",
            LetterSpacingVh: ToVh(profile.LetterSpacing),
            LineHeight: profile.LineHeight.ToString("0.##", CultureInfo.InvariantCulture),
            TextTransform: profile.Uppercase ? "uppercase" : "none",
            TextAlign: profile.TextAlignment.ToString().ToLowerInvariant(),
            TextColorRgba: HexToRgba(profile.TextColorHex, profile.TextOpacity),
            OutlineWidthVh: ToVh(profile.OutlineThickness * 2.0),
            OutlineColorHex: profile.OutlineColorHex,
            TextShadowCss: textShadow,
            BackgroundMode: profile.BackgroundMode.ToString(),
            BackgroundRgba: bgRgba,
            BorderRadiusVh: ToVh(profile.BorderRadius),
            PaddingVerticalVh: ToVh(profile.PaddingVertical),
            PaddingHorizontalVh: ToVh(profile.PaddingHorizontal),
            BorderCss: borderCss,
            PositionPreset: profile.PositionPreset.ToString(),
            OffsetXPercent: profile.OffsetXPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%",
            OffsetYPercent: profile.OffsetYPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%",
            MaxWidthPercent: Math.Clamp(profile.MaxWidthPercent, 10, 100).ToString("0.##", CultureInfo.InvariantCulture) + "%",
            MaxLines: Math.Clamp(profile.MaxLines, 1, 10),
            OverflowMode: profile.OverflowMode.ToString(),
            AnimationType: profile.AnimationType.ToString(),
            AnimationDurationMs: Math.Clamp(profile.AnimationDurationMs, 0, 1500),
            ShowOriginalText: profile.ShowOriginalText,
            OriginalFontSizeVh: ToVh(profile.OriginalFontSize),
            OriginalFontWeight: Math.Clamp(profile.OriginalFontWeight, 100, 900),
            OriginalFontStyle: profile.OriginalIsItalic ? "italic" : "normal",
            OriginalTextColorRgba: HexToRgba(profile.OriginalTextColorHex, profile.OriginalTextOpacity));
    }

    public static string SerializeStylePayload(StyleProfile profile)
    {
        return JsonSerializer.Serialize(Map(profile), JsonOptions);
    }
}
