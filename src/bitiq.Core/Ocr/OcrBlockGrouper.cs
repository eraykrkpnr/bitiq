using System.Text;

namespace Bitiq.Core.Ocr;

public readonly record struct OcrBoundingBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + (Width * 0.5);
    public double CenterY => Y + (Height * 0.5);

    public static OcrBoundingBox Union(OcrBoundingBox a, OcrBoundingBox b)
    {
        double minX = Math.Min(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxR = Math.Max(a.Right, b.Right);
        double maxB = Math.Max(a.Bottom, b.Bottom);
        return new OcrBoundingBox(minX, minY, Math.Max(0, maxR - minX), Math.Max(0, maxB - minY));
    }
}

public readonly record struct OcrWordElement(string Text, OcrBoundingBox Bounds, double Confidence = 1.0);

public readonly record struct OcrGroupingOptions(
    double MinWordConfidence = 0.0,
    string SourceLanguageCode = "en",
    bool AutoDetectVerticalText = true);

public sealed record OcrGroupedLine(
    string Text,
    OcrBoundingBox Bounds,
    double AverageConfidence,
    bool IsVertical = false);

public sealed record OcrGroupedBlock(
    string Text,
    IReadOnlyList<OcrGroupedLine> Lines,
    OcrBoundingBox Bounds,
    double AverageConfidence);

/// <summary>
/// OCR kelime/karakter kutularını düşük güvenli gürültüden arındırır; yatay veya dikey (CJK tategaki)
/// okuma sırasına göre satır ve bloklara (paragraflara) gruplar.
/// </summary>
public static class OcrBlockGrouper
{
    public static IReadOnlyList<OcrGroupedBlock> GroupIntoBlocks(
        IReadOnlyList<OcrWordElement> words,
        OcrGroupingOptions? options = null)
    {
        if (words.Count == 0)
            return [];

        var opts = options ?? new OcrGroupingOptions();
        double minConf = Math.Clamp(opts.MinWordConfidence, 0.0, 1.0);

        var filtered = new List<OcrWordElement>(words.Count);
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text))
                continue;
            if (word.Confidence < minConf)
                continue;

            filtered.Add(word with { Text = word.Text.Trim() });
        }

        if (filtered.Count == 0)
            return [];

        if (opts.AutoDetectVerticalText && IsCjkLanguage(opts.SourceLanguageCode) && ShouldTreatAsVerticalLayout(filtered))
        {
            return GroupVerticalColumns(filtered);
        }

        var lines = GroupHorizontalLines(filtered, opts.SourceLanguageCode);
        if (lines.Count == 0)
            return [];

        return GroupLinesIntoBlocks(lines);
    }

    public static string FormatBlocksToText(IReadOnlyList<OcrGroupedBlock> blocks)
    {
        if (blocks.Count == 0)
            return string.Empty;

        // Birden fazla blok varsa ve en az biri gerçek diyalogsa, arka plan tabela/HUD paraziti blokları ayıkla
        IReadOnlyList<OcrGroupedBlock> activeBlocks = blocks;
        if (blocks.Count > 1)
        {
            var nonNoise = blocks.Where(b => !IsBackgroundArtifactBlock(b)).ToList();
            if (nonNoise.Count > 0 && nonNoise.Count < blocks.Count)
                activeBlocks = nonNoise;
        }

        var blockTexts = new List<string>(activeBlocks.Count);
        foreach (var block in activeBlocks)
        {
            if (block.Lines.Count == 0)
                continue;

            if (block.Lines[0].IsVertical)
            {
                if (!string.IsNullOrWhiteSpace(block.Text))
                    blockTexts.Add(block.Text);
                continue;
            }

            var lineStrings = new List<string>(block.Lines.Count);
            foreach (var line in block.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                    lineStrings.Add(line.Text);
            }

            string joined = WindowsOcrService.JoinOcrLines(lineStrings);
            if (!string.IsNullOrWhiteSpace(joined))
                blockTexts.Add(joined);
        }

        return blockTexts.Count switch
        {
            0 => string.Empty,
            1 => blockTexts[0],
            _ => string.Join('\n', blockTexts)
        };
    }

    private static bool IsBackgroundArtifactBlock(OcrGroupedBlock block)
    {
        if (block.Lines.Count > 1 || block.Lines.Count == 0 || block.Lines[0].IsVertical)
            return false;

        string text = block.Text.Trim();
        if (string.IsNullOrEmpty(text))
            return true;

        if (SubtitleStabilizer.IsIsolatedUppercaseNoiseLine(text))
            return true;

        bool hasSentencePunct = text.Any(c => c is '.' or '!' or '?' or '\u2026');
        return !hasSentencePunct && block.AverageConfidence < 0.72 && text.Length <= 8;
    }

    public static string GroupAndFormat(
        IReadOnlyList<OcrWordElement> words,
        OcrGroupingOptions? options = null)
        => FormatBlocksToText(GroupIntoBlocks(words, options));

    private static List<OcrGroupedLine> GroupHorizontalLines(
        List<OcrWordElement> words,
        string sourceLanguageCode)
    {
        // Satır adaylarını dikey merkez (CenterY) sırasına diz
        words.Sort(static (a, b) =>
        {
            int cmpY = a.Bounds.CenterY.CompareTo(b.Bounds.CenterY);
            return cmpY != 0 ? cmpY : a.Bounds.X.CompareTo(b.Bounds.X);
        });

        var rowBuckets = new List<(double AvgY, double AvgH, List<OcrWordElement> Items)>();
        foreach (var word in words)
        {
            double h = Math.Max(1.0, word.Bounds.Height);
            bool placed = false;

            for (int r = 0; r < rowBuckets.Count; r++)
            {
                var row = rowBuckets[r];
                double tolerance = Math.Max(8.0, Math.Max(row.AvgH, h) * 0.62);
                if (Math.Abs(word.Bounds.CenterY - row.AvgY) <= tolerance)
                {
                    int n = row.Items.Count;
                    double newAvgY = ((row.AvgY * n) + word.Bounds.CenterY) / (n + 1);
                    double newAvgH = ((row.AvgH * n) + h) / (n + 1);
                    row.Items.Add(word);
                    rowBuckets[r] = (newAvgY, newAvgH, row.Items);
                    placed = true;
                    break;
                }
            }

            if (!placed)
                rowBuckets.Add((word.Bounds.CenterY, h, [word]));
        }

        rowBuckets.Sort(static (a, b) => a.AvgY.CompareTo(b.AvgY));

        var lines = new List<OcrGroupedLine>(rowBuckets.Count);
        bool isCjk = IsCjkLanguage(sourceLanguageCode);

        foreach (var row in rowBuckets)
        {
            row.Items.Sort(static (a, b) => a.Bounds.X.CompareTo(b.Bounds.X));

            // Aynı satır yüksekliğindeki ayrı sütunları (ör. oyun HUD / 2 sütunlu menü) büyük yatay boşlukta böl
            var segmentStart = 0;
            for (int i = 1; i < row.Items.Count; i++)
            {
                var prev = row.Items[i - 1];
                var curr = row.Items[i];
                double refHeight = Math.Max(prev.Bounds.Height, curr.Bounds.Height);
                double largeGapThreshold = Math.Max(36.0, refHeight * 3.0);
                double gap = curr.Bounds.X - prev.Bounds.Right;

                if (gap > largeGapThreshold)
                {
                    lines.Add(BuildHorizontalLine(row.Items, segmentStart, i - segmentStart, isCjk));
                    segmentStart = i;
                }
            }

            lines.Add(BuildHorizontalLine(row.Items, segmentStart, row.Items.Count - segmentStart, isCjk));
        }

        return lines;
    }

    private static OcrGroupedLine BuildHorizontalLine(
        List<OcrWordElement> items,
        int startIndex,
        int count,
        bool isCjk)
    {
        var first = items[startIndex];
        if (count == 1)
            return new OcrGroupedLine(first.Text, first.Bounds, first.Confidence);

        var sb = new StringBuilder(first.Text);
        var bounds = first.Bounds;
        double confSum = first.Confidence;

        for (int i = 1; i < count; i++)
        {
            var prev = items[startIndex + i - 1];
            var curr = items[startIndex + i];
            double gap = curr.Bounds.X - prev.Bounds.Right;
            double refH = Math.Max(1.0, Math.Max(prev.Bounds.Height, curr.Bounds.Height));

            bool bothCjk = isCjk && IsCjkChar(prev.Text[^1]) && IsCjkChar(curr.Text[0]);
            if (!bothCjk || gap > refH * 0.55)
                sb.Append(' ');

            sb.Append(curr.Text);
            bounds = OcrBoundingBox.Union(bounds, curr.Bounds);
            confSum += curr.Confidence;
        }

        return new OcrGroupedLine(sb.ToString(), bounds, confSum / count);
    }

    private static List<OcrGroupedBlock> GroupLinesIntoBlocks(List<OcrGroupedLine> lines)
    {
        // Yukarıdan aşağıya, aynı dikey bantta soldan sağa sırala
        lines.Sort(static (a, b) =>
        {
            double bandTol = Math.Max(8.0, Math.Min(a.Bounds.Height, b.Bounds.Height) * 0.45);
            if (Math.Abs(a.Bounds.CenterY - b.Bounds.CenterY) <= bandTol)
                return a.Bounds.X.CompareTo(b.Bounds.X);
            return a.Bounds.Y.CompareTo(b.Bounds.Y);
        });

        var blocks = new List<(OcrBoundingBox Bounds, List<OcrGroupedLine> Lines)>();

        foreach (var line in lines)
        {
            int targetBlock = -1;
            double bestDistance = double.MaxValue;

            for (int b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];
                var lastLine = block.Lines[^1];

                double maxH = Math.Max(lastLine.Bounds.Height, line.Bounds.Height);
                double minH = Math.Max(1.0, Math.Min(lastLine.Bounds.Height, line.Bounds.Height));
                if (maxH / minH > 2.2)
                    continue;

                double verticalGap = line.Bounds.Y - lastLine.Bounds.Bottom;
                double maxAllowedVerticalGap = Math.Max(12.0, maxH * 1.45);
                if (verticalGap < -maxH * 0.5 || verticalGap > maxAllowedVerticalGap)
                    continue;

                // Yatay örtüşme veya merkez/sol hiza yakınlığı yoksa (ör. sağ/sol sütun) ayrı blok tut
                double horizontalOverlap = Math.Min(block.Bounds.Right, line.Bounds.Right) - Math.Max(block.Bounds.X, line.Bounds.X);
                double alignmentGap = Math.Abs(block.Bounds.CenterX - line.Bounds.CenterX);
                bool horizontallyRelated = horizontalOverlap >= -maxH * 1.5 || alignmentGap <= Math.Max(block.Bounds.Width, line.Bounds.Width) * 0.65;
                if (!horizontallyRelated)
                    continue;

                double score = Math.Max(0, verticalGap);
                if (score < bestDistance)
                {
                    bestDistance = score;
                    targetBlock = b;
                }
            }

            if (targetBlock >= 0)
            {
                var current = blocks[targetBlock];
                current.Lines.Add(line);
                blocks[targetBlock] = (OcrBoundingBox.Union(current.Bounds, line.Bounds), current.Lines);
            }
            else
            {
                blocks.Add((line.Bounds, [line]));
            }
        }

        // Blokları okuma sırasına göre sırala (üst -> alt, sol -> sağ)
        blocks.Sort(static (a, b) =>
        {
            double bandTol = Math.Max(10.0, Math.Min(a.Bounds.Height, b.Bounds.Height) * 0.35);
            if (Math.Abs(a.Bounds.Y - b.Bounds.Y) <= bandTol)
                return a.Bounds.X.CompareTo(b.Bounds.X);
            return a.Bounds.Y.CompareTo(b.Bounds.Y);
        });

        var result = new List<OcrGroupedBlock>(blocks.Count);
        foreach (var (bounds, blockLines) in blocks)
        {
            double confSum = 0;
            var texts = new List<string>(blockLines.Count);
            foreach (var l in blockLines)
            {
                confSum += l.AverageConfidence;
                texts.Add(l.Text);
            }

            string blockText = WindowsOcrService.JoinOcrLines(texts);
            result.Add(new OcrGroupedBlock(blockText, blockLines, bounds, confSum / blockLines.Count));
        }

        return result;
    }

    private static bool ShouldTreatAsVerticalLayout(List<OcrWordElement> words)
    {
        if (words.Count < 3)
            return false;

        int cjkWordCount = 0;
        int tallWordCount = 0;
        double minX = double.MaxValue;
        double maxR = double.MinValue;
        double minY = double.MaxValue;
        double maxB = double.MinValue;

        foreach (var w in words)
        {
            foreach (char ch in w.Text)
            {
                if (IsCjkChar(ch))
                {
                    cjkWordCount++;
                    break;
                }
            }

            if (w.Bounds.Height > w.Bounds.Width * 1.35 && w.Text.Length >= 2)
                tallWordCount++;

            minX = Math.Min(minX, w.Bounds.X);
            maxR = Math.Max(maxR, w.Bounds.Right);
            minY = Math.Min(minY, w.Bounds.Y);
            maxB = Math.Max(maxB, w.Bounds.Bottom);
        }

        if (cjkWordCount < Math.Max(2, words.Count / 2))
            return false;

        if (tallWordCount >= Math.Max(2, words.Count / 2))
            return true;

        // Küçük tek/az karakterli CJK kutuları dikey sütun halinde dizilmişse komşuluk oylamasıyla tespit et
        int verticalNeighbors = 0;
        int horizontalNeighbors = 0;
        for (int i = 0; i < words.Count; i++)
        {
            var a = words[i].Bounds;
            for (int j = i + 1; j < words.Count; j++)
            {
                var b = words[j].Bounds;
                double refW = Math.Max(a.Width, b.Width);
                double refH = Math.Max(a.Height, b.Height);

                double dxGap = Math.Max(b.X - a.Right, a.X - b.Right);
                double dyGap = Math.Max(b.Y - a.Bottom, a.Y - b.Bottom);

                if (Math.Abs(a.CenterX - b.CenterX) <= Math.Max(10.0, refW * 0.75)
                    && dyGap >= -refH * 0.25
                    && dyGap <= Math.Max(16.0, refH * 1.6))
                {
                    verticalNeighbors++;
                }

                if (Math.Abs(a.CenterY - b.CenterY) <= Math.Max(8.0, refH * 0.65)
                    && dxGap >= -refW * 0.25
                    && dxGap <= Math.Max(16.0, refW * 1.6))
                {
                    horizontalNeighbors++;
                }
            }
        }

        if (verticalNeighbors > horizontalNeighbors && verticalNeighbors >= 2)
            return true;

        double totalW = maxR - minX;
        double totalH = maxB - minY;
        return totalH > totalW * 1.4 && words.Count >= 4;
    }

    private static List<OcrGroupedBlock> GroupVerticalColumns(List<OcrWordElement> words)
    {
        words.Sort(static (a, b) =>
        {
            int cmpX = b.Bounds.CenterX.CompareTo(a.Bounds.CenterX); // sağdan sola sütun
            return cmpX != 0 ? cmpX : a.Bounds.Y.CompareTo(b.Bounds.Y);
        });

        var columns = new List<(double AvgX, double AvgW, List<OcrWordElement> Items)>();
        foreach (var word in words)
        {
            double w = Math.Max(1.0, word.Bounds.Width);
            bool placed = false;
            for (int c = 0; c < columns.Count; c++)
            {
                var col = columns[c];
                double tol = Math.Max(8.0, Math.Max(col.AvgW, w) * 0.85);
                if (Math.Abs(word.Bounds.CenterX - col.AvgX) <= tol)
                {
                    int n = col.Items.Count;
                    double newAvgX = ((col.AvgX * n) + word.Bounds.CenterX) / (n + 1);
                    double newAvgW = ((col.AvgW * n) + w) / (n + 1);
                    col.Items.Add(word);
                    columns[c] = (newAvgX, newAvgW, col.Items);
                    placed = true;
                    break;
                }
            }

            if (!placed)
                columns.Add((word.Bounds.CenterX, w, [word]));
        }

        // Geleneksel dikey CJK: sağdan sola sütun sırası
        columns.Sort(static (a, b) => b.AvgX.CompareTo(a.AvgX));

        var groupedLines = new List<OcrGroupedLine>(columns.Count);
        var totalBounds = words[0].Bounds;
        double confSum = 0;
        int confCount = 0;

        foreach (var col in columns)
        {
            col.Items.Sort(static (a, b) => a.Bounds.Y.CompareTo(b.Bounds.Y));
            var sb = new StringBuilder();
            var colBounds = col.Items[0].Bounds;
            double colConf = 0;

            foreach (var item in col.Items)
            {
                sb.Append(item.Text);
                colBounds = OcrBoundingBox.Union(colBounds, item.Bounds);
                colConf += item.Confidence;
                totalBounds = OcrBoundingBox.Union(totalBounds, item.Bounds);
                confSum += item.Confidence;
                confCount++;
            }

            groupedLines.Add(new OcrGroupedLine(
                sb.ToString(),
                colBounds,
                colConf / col.Items.Count,
                IsVertical: true));
        }

        string blockText = string.Join('\n', groupedLines.Select(l => l.Text));
        double avgConf = confCount == 0 ? 1.0 : confSum / confCount;
        return [new OcrGroupedBlock(blockText, groupedLines, totalBounds, avgConf)];
    }

    private static bool IsCjkLanguage(string code)
        => code.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("ko", StringComparison.OrdinalIgnoreCase);

    private static bool IsCjkChar(char ch)
        => (ch >= '\u3040' && ch <= '\u30FF') // Hiragana + Katakana
           || (ch >= '\u4E00' && ch <= '\u9FFF') // CJK Unified Ideographs
           || (ch >= '\u3400' && ch <= '\u4DBF')
           || (ch >= '\uAC00' && ch <= '\uD7AF'); // Hangul
}
