using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Bitiq.Core.Models;
using Bitiq.Core.Styling;

namespace Bitiq.App.Controls;

public partial class StyledSubtitlePresenter : UserControl
{
    private StyleProfile _style = new();
    private SubtitleUpdateEvent _subtitle = new() { IsCleared = true };
    private bool _isCurrentlyVisible;

    public StyledSubtitlePresenter()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RenderAll(animateVisibility: false);
    }

    public void SetStyle(StyleProfile style)
    {
        _style = style.Clone();
        RenderAll(animateVisibility: false);
    }

    public void SetSubtitle(SubtitleUpdateEvent subtitle)
    {
        bool wasVisible = _isCurrentlyVisible;
        _subtitle = subtitle;
        bool willBeVisible = !subtitle.IsCleared && !string.IsNullOrWhiteSpace(subtitle.TurkishText);
        RenderAll(animateVisibility: wasVisible != willBeVisible);
    }

    private void RenderAll(bool animateVisibility)
    {
        double canvasW = ActualWidth > 20 ? ActualWidth : 1280;
        double canvasH = ActualHeight > 20 ? ActualHeight : 720;
        double scale = Math.Clamp(canvasH / 1080.0, 0.35, 3.0);

        bool hasText = !_subtitle.IsCleared && !string.IsNullOrWhiteSpace(_subtitle.TurkishText);
        _isCurrentlyVisible = hasText;

        if (!hasText)
        {
            ApplyVisibilityAnimation(visible: false, animate: animateVisibility);
            return;
        }

        // Yerleşim ön ayarı + % ince ayar
        bool isFullBar = _style.BackgroundMode == SubtitleBackgroundMode.FullWidthBar;
        var (hAlign, vAlign, baseMargin) = GetPresetAnchor(_style.PositionPreset, canvasW, canvasH, isFullBar);
        AnchorContainer.HorizontalAlignment = hAlign;
        AnchorContainer.VerticalAlignment = vAlign;

        double offsetPxX = canvasW * (_style.OffsetXPercent / 100.0);
        double offsetPxY = canvasH * (_style.OffsetYPercent / 100.0);
        AnchorContainer.Margin = new Thickness(
            baseMargin.Left + (hAlign == HorizontalAlignment.Right ? 0 : offsetPxX),
            baseMargin.Top + (vAlign == VerticalAlignment.Bottom ? 0 : offsetPxY),
            baseMargin.Right + (hAlign == HorizontalAlignment.Right ? -offsetPxX : 0),
            baseMargin.Bottom + (vAlign == VerticalAlignment.Bottom ? -offsetPxY : 0));

        // Dış kutu (SingleBlock veya FullWidthBar)
        Brush bgBrush = CreateColorBrush(_style.BackgroundColorHex, _style.BackgroundOpacity);
        Brush borderBrush = CreateColorBrush(_style.BorderColorHex, 100);
        double borderThick = _style.BackgroundMode != SubtitleBackgroundMode.None ? Math.Max(0, _style.BorderThickness * scale) : 0;
        double padH = Math.Max(0, _style.PaddingHorizontal * scale);
        double padV = Math.Max(0, _style.PaddingVertical * scale);
        double radius = Math.Max(0, _style.BorderRadius * scale);

        double maxAllowedByCanvas = Math.Max(120, canvasW - (baseMargin.Left + baseMargin.Right) - (Math.Abs(offsetPxX) * 2) - 16);
        double desiredMaxW = isFullBar ? canvasW : Math.Max(120, canvasW * Math.Clamp(_style.MaxWidthPercent, 10, 100) / 100.0);
        double maxBoxW = isFullBar ? canvasW : Math.Min(maxAllowedByCanvas, desiredMaxW);
        AnchorContainer.MaxWidth = maxBoxW;
        AnchorContainer.Width = isFullBar ? canvasW : double.NaN;

        double maxTextW = Math.Max(80, maxBoxW - (padH * 2) - (borderThick * 2));
        double maxCanvasTextH = Math.Max(36, canvasH - (baseMargin.Top + baseMargin.Bottom) - Math.Abs(offsetPxY) - (padV * 2) - (borderThick * 2) - 12);

        if (_style.BackgroundMode is SubtitleBackgroundMode.SingleBlock or SubtitleBackgroundMode.FullWidthBar)
        {
            OuterBox.Background = bgBrush;
            OuterBox.BorderBrush = borderThick > 0 ? borderBrush : null;
            OuterBox.BorderThickness = new Thickness(borderThick);
            OuterBox.CornerRadius = _style.BackgroundMode == SubtitleBackgroundMode.FullWidthBar
                ? new CornerRadius(0)
                : new CornerRadius(radius);
            OuterBox.Padding = new Thickness(padH, padV, padH, padV);
        }
        else
        {
            OuterBox.Background = Brushes.Transparent;
            OuterBox.BorderBrush = null;
            OuterBox.BorderThickness = new Thickness(0);
            OuterBox.CornerRadius = new CornerRadius(0);
            OuterBox.Padding = new Thickness(0);
        }

        // Yazı biçemleri
        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(_style.FontFamily) ? "Segoe UI" : _style.FontFamily);
        var fontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(_style.FontWeight, 100, 999));
        var fontStyle = _style.IsItalic ? FontStyles.Italic : FontStyles.Normal;
        var textAlign = _style.TextAlignment switch
        {
            SubtitleTextAlign.Left => TextAlignment.Left,
            SubtitleTextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        var itemHAlign = _style.TextAlignment switch
        {
            SubtitleTextAlign.Left => HorizontalAlignment.Left,
            SubtitleTextAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };

        Brush fillBrush = CreateColorBrush(_style.TextColorHex, _style.TextOpacity);
        Brush strokeBrush = CreateColorBrush(_style.OutlineColorHex, 100);
        Effect? shadowEffect = CreateShadowEffect(scale);

        string displayText = _style.Uppercase
            ? _subtitle.TurkishText.ToUpper(CultureInfo.CurrentUICulture)
            : _subtitle.TurkishText;

        double fontSize = Math.Max(8.0, _style.FontSize * scale);
        double minShrinkFontSize = Math.Max(8.0, Math.Min(fontSize, 14.0));
        double outlineThickness = Math.Max(0, _style.OutlineThickness * scale);
        double letterSpacing = _style.LetterSpacing * scale;
        int maxLines = Math.Clamp(_style.MaxLines, 1, 10);

        // Taşma kontrolü: satır sayısı maxLines'ı veya tuval yüksekliğini aşarsa
        var rawLines = displayText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (_style.OverflowMode == SubtitleOverflowMode.Clip && rawLines.Length > maxLines)
        {
            rawLines = rawLines.Take(maxLines).ToArray();
        }
        else if (_style.OverflowMode == SubtitleOverflowMode.ShrinkToFit)
        {
            string probeText = string.Join("\n", rawLines);
            double origReserve = (_style.ShowOriginalText && !string.IsNullOrWhiteSpace(_subtitle.EnglishText))
                ? Math.Max(22, (_style.OriginalFontSize * scale * 1.4) + 8)
                : 0;
            double availableTextH = Math.Max(30, maxCanvasTextH - origReserve);

            for (int iter = 0; iter < 6 && fontSize > minShrinkFontSize; iter++)
            {
                var probe = new OutlinedTextBlock
                {
                    Text = probeText,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    FontStyle = fontStyle,
                    StrokeThickness = outlineThickness,
                    TextAlignment = textAlign,
                    LineHeightMultiplier = _style.LineHeight,
                    LetterSpacing = letterSpacing,
                    MaxWidth = maxTextW
                };
                probe.Measure(new Size(maxTextW, double.PositiveInfinity));

                double lineH = Math.Max(8.0, fontSize * Math.Clamp(_style.LineHeight, 0.8, 2.5));
                double allowedByMaxLines = (lineH * maxLines) + (outlineThickness * 2) + 8;
                double limitH = Math.Min(allowedByMaxLines, availableTextH);

                if (probe.DesiredSize.Height > limitH)
                {
                    fontSize = Math.Max(minShrinkFontSize, fontSize * 0.90);
                }
                else
                {
                    break;
                }
            }
        }

        LinesStack.Children.Clear();
        LinesStack.HorizontalAlignment = isFullBar ? itemHAlign : HorizontalAlignment.Stretch;

        if (_style.BackgroundMode == SubtitleBackgroundMode.PerLineBox)
        {
            foreach (string line in rawLines)
            {
                var lineBlock = new OutlinedTextBlock
                {
                    Text = line,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    FontStyle = fontStyle,
                    Fill = fillBrush,
                    Stroke = strokeBrush,
                    StrokeThickness = outlineThickness,
                    TextAlignment = textAlign,
                    LineHeightMultiplier = _style.LineHeight,
                    LetterSpacing = letterSpacing,
                    MaxWidth = maxTextW,
                    Effect = shadowEffect
                };

                var lineBorder = new Border
                {
                    Background = bgBrush,
                    BorderBrush = borderThick > 0 ? borderBrush : null,
                    BorderThickness = new Thickness(borderThick),
                    CornerRadius = new CornerRadius(radius),
                    Padding = new Thickness(padH, padV, padH, padV),
                    Margin = new Thickness(0, 0, 0, Math.Max(2, 4 * scale)),
                    HorizontalAlignment = itemHAlign,
                    MaxWidth = maxBoxW,
                    Child = lineBlock
                };
                LinesStack.Children.Add(lineBorder);
            }
        }
        else
        {
            string joined = string.Join("\n", rawLines);
            var textBlock = new OutlinedTextBlock
            {
                Text = joined,
                FontFamily = fontFamily,
                FontSize = fontSize,
                FontWeight = fontWeight,
                FontStyle = fontStyle,
                Fill = fillBrush,
                Stroke = strokeBrush,
                StrokeThickness = outlineThickness,
                TextAlignment = textAlign,
                LineHeightMultiplier = _style.LineHeight,
                LetterSpacing = letterSpacing,
                MaxWidth = maxTextW,
                Effect = shadowEffect
            };
            if (_style.OverflowMode == SubtitleOverflowMode.Clip)
            {
                double lineH = Math.Max(8.0, fontSize * Math.Clamp(_style.LineHeight, 0.8, 2.5));
                textBlock.MaxHeight = Math.Min(maxCanvasTextH, (lineH * maxLines) + (outlineThickness * 2) + 6);
                textBlock.ClipToBounds = true;
            }
            LinesStack.Children.Add(textBlock);
        }

        // Orijinal İngilizce metin (küçük ve ayrı stil)
        if (_style.ShowOriginalText && !string.IsNullOrWhiteSpace(_subtitle.EnglishText))
        {
            OriginalHost.Visibility = Visibility.Visible;
            OriginalHost.HorizontalAlignment = isFullBar ? itemHAlign : HorizontalAlignment.Stretch;
            OriginalHost.Content = new OutlinedTextBlock
            {
                Text = _subtitle.EnglishText,
                FontFamily = fontFamily,
                FontSize = Math.Max(8.0, _style.OriginalFontSize * scale),
                FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(_style.OriginalFontWeight, 100, 999)),
                FontStyle = _style.OriginalIsItalic ? FontStyles.Italic : FontStyles.Normal,
                Fill = CreateColorBrush(_style.OriginalTextColorHex, _style.OriginalTextOpacity),
                Stroke = strokeBrush,
                StrokeThickness = Math.Max(0, _style.OutlineThickness * 0.65 * scale),
                TextAlignment = textAlign,
                LineHeightMultiplier = 1.2,
                MaxWidth = maxTextW,
                Effect = shadowEffect
            };
        }
        else
        {
            OriginalHost.Visibility = Visibility.Collapsed;
            OriginalHost.Content = null;
        }

        ApplyVisibilityAnimation(visible: true, animate: animateVisibility);
    }

    private void ApplyVisibilityAnimation(bool visible, bool animate)
    {
        double targetOpacity = visible ? 1.0 : 0.0;
        double targetSlideY = visible ? 0.0 : 12.0;
        int durationMs = Math.Clamp(_style.AnimationDurationMs, 0, 1500);

        if (!animate || _style.AnimationType == SubtitleAnimationType.None || durationMs == 0)
        {
            AnchorContainer.BeginAnimation(OpacityProperty, null);
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            AnchorContainer.Opacity = targetOpacity;
            SlideTransform.Y = 0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(durationMs);
        var fadeAnim = new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        AnchorContainer.BeginAnimation(OpacityProperty, fadeAnim);

        if (_style.AnimationType == SubtitleAnimationType.Slide)
        {
            if (visible)
                SlideTransform.Y = 12.0;
            var slideAnim = new DoubleAnimation(targetSlideY, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
        else
        {
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            SlideTransform.Y = 0;
        }
    }

    private Effect? CreateShadowEffect(double scale)
    {
        if (_style.ShadowOpacity <= 0)
            return null;

        var (r, g, b) = StyleCssMapper.ParseHexRgb(_style.ShadowColorHex);
        byte alpha = (byte)Math.Clamp((int)Math.Round(_style.ShadowOpacity / 100.0 * 255), 0, 255);
        double dx = _style.ShadowOffsetX * scale;
        double dy = _style.ShadowOffsetY * scale;
        double depth = Math.Sqrt((dx * dx) + (dy * dy));
        double direction = depth > 0.01 ? (Math.Atan2(-dy, dx) * 180.0 / Math.PI) : 315.0;

        var effect = new DropShadowEffect
        {
            Color = Color.FromArgb(alpha, r, g, b),
            BlurRadius = Math.Max(0, _style.ShadowBlur * scale),
            ShadowDepth = depth,
            Direction = direction,
            Opacity = 1.0
        };
        effect.Freeze();
        return effect;
    }

    private static Brush CreateColorBrush(string? hex, double opacityPercent)
    {
        var (r, g, b) = StyleCssMapper.ParseHexRgb(hex);
        byte a = (byte)Math.Clamp((int)Math.Round(opacityPercent / 100.0 * 255), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static (HorizontalAlignment H, VerticalAlignment V, Thickness Margin) GetPresetAnchor(
        SubtitlePositionPreset preset,
        double w,
        double h,
        bool isFullBar)
    {
        double marginX = isFullBar ? 0 : w * 0.04;
        double marginY = h * 0.05;

        return preset switch
        {
            SubtitlePositionPreset.TopCenter => (HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(0, marginY, 0, 0)),
            SubtitlePositionPreset.Center => (HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0)),
            SubtitlePositionPreset.BottomLeft => (isFullBar ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(marginX, 0, 0, marginY)),
            SubtitlePositionPreset.BottomRight => (isFullBar ? HorizontalAlignment.Stretch : HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, marginX, marginY)),
            SubtitlePositionPreset.TopLeft => (isFullBar ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(marginX, marginY, 0, 0)),
            SubtitlePositionPreset.TopRight => (isFullBar ? HorizontalAlignment.Stretch : HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, marginY, marginX, 0)),
            _ => (HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(0, 0, 0, marginY))
        };
    }
}
