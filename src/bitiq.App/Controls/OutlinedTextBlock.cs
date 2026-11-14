using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Bitiq.App.Controls;

/// <summary>
/// WPF üzerinde gerçek dış kontur (stroke-behind-fill), satır yüksekliği ve harf aralığı destekleyen metin bileşeni.
/// </summary>
public sealed class OutlinedTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(new FontFamily("Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontWeights.Bold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontStyleProperty = DependencyProperty.Register(
        nameof(FontStyle), typeof(FontStyle), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontStyles.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextAlignment.Center, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineHeightMultiplierProperty = DependencyProperty.Register(
        nameof(LineHeightMultiplier), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(1.25, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LetterSpacingProperty = DependencyProperty.Register(
        nameof(LetterSpacing), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontStyle FontStyle { get => (FontStyle)GetValue(FontStyleProperty); set => SetValue(FontStyleProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public double LineHeightMultiplier { get => (double)GetValue(LineHeightMultiplierProperty); set => SetValue(LineHeightMultiplierProperty, value); }
    public double LetterSpacing { get => (double)GetValue(LetterSpacingProperty); set => SetValue(LetterSpacingProperty, value); }

    private double _lastMeasuredTextWidth = 200;

    private double GetEffectiveMaxTextWidth(double availableWidth, double pad)
    {
        double limit = availableWidth;
        if (!double.IsInfinity(MaxWidth) && !double.IsNaN(MaxWidth) && MaxWidth > 0)
        {
            limit = double.IsInfinity(limit) ? MaxWidth : Math.Min(limit, MaxWidth);
        }

        return double.IsInfinity(limit)
            ? double.PositiveInfinity
            : Math.Max(10.0, limit - (pad * 2));
    }

    private double GetLineHeightPx() =>
        Math.Max(8.0, FontSize * Math.Clamp(LineHeightMultiplier, 0.8, 2.5));

    private FormattedText CreateFormattedText(string text, double maxWidth)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal);
        var ft = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(6.0, FontSize),
            Fill ?? Brushes.White,
            dpi)
        {
            TextAlignment = TextAlignment,
            LineHeight = GetLineHeightPx()
        };

        if (!double.IsInfinity(maxWidth) && maxWidth > 0)
            ft.MaxTextWidth = maxWidth;

        return ft;
    }

    private sealed record CharGlyph(FormattedText Ft, double Advance);
    private sealed record WrappedGlyphLine(List<CharGlyph> Glyphs, double Width);

    private List<CharGlyph> MeasureCharsWithSpacing(string text, out double width)
    {
        var list = new List<CharGlyph>(text.Length);
        double total = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var cft = CreateFormattedText(text[i].ToString(), double.PositiveInfinity);
            cft.TextAlignment = TextAlignment.Left;
            double adv = cft.WidthIncludingTrailingWhitespace;
            list.Add(new CharGlyph(cft, adv));
            total += adv + (i + 1 < text.Length ? LetterSpacing : 0);
        }

        width = Math.Max(0, total);
        return list;
    }

    private (List<WrappedGlyphLine> Lines, double MaxWidth) BuildWrappedLinesWithLetterSpacing(string text, double maxTextW)
    {
        var result = new List<WrappedGlyphLine>();
        double maxLineW = 0;
        var spaceFt = CreateFormattedText(" ", double.PositiveInfinity);
        spaceFt.TextAlignment = TextAlignment.Left;
        double spaceAdv = spaceFt.WidthIncludingTrailingWhitespace;

        var paragraphs = text.Replace("\r\n", "\n").Split('\n');
        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                result.Add(new WrappedGlyphLine([], 0));
                continue;
            }

            var words = paragraph.Split(' ');
            var curGlyphs = new List<CharGlyph>();
            double curW = 0;

            void FlushCurrentLine()
            {
                result.Add(new WrappedGlyphLine(curGlyphs, curW));
                if (curW > maxLineW)
                    maxLineW = curW;
                curGlyphs = new List<CharGlyph>();
                curW = 0;
            }

            foreach (string word in words)
            {
                var wordGlyphs = MeasureCharsWithSpacing(word, out double wordW);
                if (wordGlyphs.Count == 0)
                    continue;

                double sepW = curGlyphs.Count > 0 ? spaceAdv + (LetterSpacing * 2) : 0;
                if (curGlyphs.Count > 0 && !double.IsInfinity(maxTextW) && (curW + sepW + wordW) > maxTextW)
                {
                    FlushCurrentLine();
                    sepW = 0;
                }

                // Tek kelime bile maxTextW'den uzunsa karakter bazında sararak taşmayı önle
                if (!double.IsInfinity(maxTextW) && curGlyphs.Count == 0 && wordW > maxTextW && wordGlyphs.Count > 1)
                {
                    foreach (var g in wordGlyphs)
                    {
                        double gSep = curGlyphs.Count > 0 ? LetterSpacing : 0;
                        if (curGlyphs.Count > 0 && (curW + gSep + g.Advance) > maxTextW)
                        {
                            FlushCurrentLine();
                            gSep = 0;
                        }
                        curGlyphs.Add(g);
                        curW += gSep + g.Advance;
                    }
                    continue;
                }

                if (curGlyphs.Count > 0)
                {
                    curGlyphs.Add(new CharGlyph(spaceFt, spaceAdv));
                    curW += sepW;
                }

                curGlyphs.AddRange(wordGlyphs);
                curW += wordW;
            }

            if (curGlyphs.Count > 0 || result.Count == 0)
            {
                FlushCurrentLine();
            }
        }

        return (result, maxLineW);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text))
            return new Size(0, 0);

        double pad = Math.Max(0, StrokeThickness);
        double maxTextW = GetEffectiveMaxTextWidth(availableSize.Width, pad);

        if (Math.Abs(LetterSpacing) <= 0.05)
        {
            var ft = CreateFormattedText(Text, maxTextW);
            double naturalW = Math.Max(ft.Width, ft.WidthIncludingTrailingWhitespace);
            double contentW = double.IsInfinity(maxTextW)
                ? Math.Ceiling(naturalW)
                : Math.Ceiling(Math.Min(maxTextW, naturalW));

            _lastMeasuredTextWidth = Math.Max(16, contentW + 2);
            double desiredW = double.IsInfinity(availableSize.Width)
                ? _lastMeasuredTextWidth + (pad * 2)
                : Math.Min(availableSize.Width, _lastMeasuredTextWidth + (pad * 2));

            return new Size(desiredW, Math.Ceiling(ft.Height + (pad * 2) + 4));
        }

        var (wrappedLines, maxLineW) = BuildWrappedLinesWithLetterSpacing(Text, maxTextW);
        double spacedContentW = double.IsInfinity(maxTextW)
            ? Math.Ceiling(maxLineW)
            : Math.Ceiling(Math.Min(maxTextW, maxLineW));

        _lastMeasuredTextWidth = Math.Max(16, spacedContentW + 2);
        double desiredSpacedW = double.IsInfinity(availableSize.Width)
            ? _lastMeasuredTextWidth + (pad * 2)
            : Math.Min(availableSize.Width, _lastMeasuredTextWidth + (pad * 2));

        double totalHeight = (wrappedLines.Count * GetLineHeightPx()) + (pad * 2) + 4;
        return new Size(desiredSpacedW, Math.Ceiling(totalHeight));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text))
            return;

        double pad = Math.Max(0, StrokeThickness);
        double renderMaxW = ActualWidth > (pad * 2)
            ? Math.Max(ActualWidth - (pad * 2), _lastMeasuredTextWidth)
            : _lastMeasuredTextWidth;

        if (Math.Abs(LetterSpacing) <= 0.05)
        {
            var ft = CreateFormattedText(Text, renderMaxW);
            var geom = ft.BuildGeometry(new Point(pad, pad));

            if (StrokeThickness > 0 && Stroke is not null)
            {
                var pen = new Pen(Stroke, StrokeThickness * 2.0)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geom);
            }

            dc.DrawGeometry(Fill, null, geom);
            return;
        }

        // Harf aralığı etkinse sarılmış satırları karakter karakter hizalayarak çiz
        var (wrappedLines, _) = BuildWrappedLinesWithLetterSpacing(Text, renderMaxW);
        double lineHeight = GetLineHeightPx();
        Pen? strokePen = null;
        if (StrokeThickness > 0 && Stroke is not null)
        {
            strokePen = new Pen(Stroke, StrokeThickness * 2.0)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            strokePen.Freeze();
        }

        for (int li = 0; li < wrappedLines.Count; li++)
        {
            var line = wrappedLines[li];
            double startX = TextAlignment switch
            {
                TextAlignment.Center => Math.Max(pad, (ActualWidth - line.Width) / 2.0),
                TextAlignment.Right => Math.Max(pad, ActualWidth - pad - line.Width),
                _ => pad
            };

            double y = pad + (li * lineHeight);
            double curX = startX;
            for (int gi = 0; gi < line.Glyphs.Count; gi++)
            {
                var glyph = line.Glyphs[gi];
                var geom = glyph.Ft.BuildGeometry(new Point(curX, y));
                if (strokePen is not null)
                    dc.DrawGeometry(null, strokePen, geom);
                dc.DrawGeometry(Fill, null, geom);
                curX += glyph.Advance + (gi + 1 < line.Glyphs.Count ? LetterSpacing : 0);
            }
        }
    }
}
