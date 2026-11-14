using System.Diagnostics;
using Bitiq.Core.Capture;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace Bitiq.Core.Ocr;

public sealed record OcrCheckResult(bool IsAvailable, string? LanguageTag, string? ErrorMessage, string? FixInstructions);

public sealed record OcrFrameResult(
    string Text,
    double OcrMs,
    long CapturedAtTimestamp);

/// <summary>
/// Süreç içi Windows.Media.Ocr servisi.
/// Aynı anda yalnızca tek OCR işi yürütür; bekleyen eski kareleri atarak en yeni kareyi işler.
/// </summary>
public sealed class WindowsOcrService : IDisposable
{
    private readonly OcrEngine? _engine;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _glyphCacheLock = new();

    private CapturedFrameData? _pendingFrame;
    private int _isProcessing;
    private bool _preprocessEnabled;
    private bool _disposed;

    private byte[]? _lastGlyphGrid;
    private int _lastMaskWidth;
    private int _lastMaskHeight;
    private string _lastOcrResult = string.Empty;

    public bool IsAvailable => _engine is not null;
    public string? ActiveLanguageTag { get; }
    public OcrCheckResult Status { get; }

    public event Action<OcrFrameResult>? TextRecognized;
    public event Action<string>? ErrorOccurred;

    public WindowsOcrService(bool preprocessEnabled = false)
    {
        _preprocessEnabled = preprocessEnabled;
        Status = CheckEnglishOcrAvailability(out var engine);
        _engine = engine;
        ActiveLanguageTag = Status.LanguageTag;

        if (_engine is not null)
        {
            _ = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }
    }

    public void SetPreprocessEnabled(bool enabled)
    {
        if (_preprocessEnabled != enabled)
        {
            _preprocessEnabled = enabled;
            ResetGlyphCache();
        }
    }

    public void ResetGlyphCache()
    {
        lock (_glyphCacheLock)
        {
            _lastGlyphGrid = null;
            _lastMaskWidth = 0;
            _lastMaskHeight = 0;
            _lastOcrResult = string.Empty;
        }
    }

    public static OcrCheckResult CheckEnglishOcrAvailability(out OcrEngine? engine)
    {
        engine = null;
        try
        {
            var available = OcrEngine.AvailableRecognizerLanguages;
            var exactEnUs = available.FirstOrDefault(l =>
                l.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase));
            var anyEn = exactEnUs ?? available.FirstOrDefault(l =>
                l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));

            if (anyEn is not null)
            {
                engine = OcrEngine.TryCreateFromLanguage(anyEn);
                if (engine is not null)
                {
                    return new OcrCheckResult(true, anyEn.LanguageTag, null, null);
                }
            }

            // Yedek olarak kullanıcı profili dillerini dene
            var enLang = new Language("en-US");
            if (OcrEngine.IsLanguageSupported(enLang))
            {
                engine = OcrEngine.TryCreateFromLanguage(enLang);
                if (engine is not null)
                    return new OcrCheckResult(true, "en-US", null, null);
            }

            return new OcrCheckResult(
                false,
                null,
                "Windows İngilizce (en-US) OCR dil paketi yüklü değil.",
                "Çözüm: Windows Ayarları → Zaman ve Dil → Dil ve Bölge → 'Dil ekle' → English (United States) seçin ve 'Temel yazma / OCR' özelliğini yükleyin. Alternatif olarak yönetici PowerShell'de: Add-WindowsCapability -Online -Name Language.OCR~~~en-US~0.0.1.0");
        }
        catch (Exception ex)
        {
            return new OcrCheckResult(
                false,
                null,
                $"Windows OCR başlatılamadı: {ex.Message}",
                "Windows 10/11 Ayarlar → Dil ve Bölge bölümünden English (United States) dil paketinin yüklü olduğunu doğrulayın.");
        }
    }

    /// <summary>
    /// Yeni kareyi tek-yuvalı tampona koyar. Önceki işlenmemiş kare varsa otomatik atılır.
    /// </summary>
    public void EnqueueFrame(CapturedFrameData frame)
    {
        if (_disposed || _engine is null)
            return;

        Interlocked.Exchange(ref _pendingFrame, frame);
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var frame = Interlocked.Exchange(ref _pendingFrame, null);
            if (frame is null)
                continue;

            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
                continue;

            try
            {
                var sw = Stopwatch.StartNew();
                string text = await RecognizePixelsAsync(
                    frame.BgraPixels,
                    frame.Width,
                    frame.Height,
                    _preprocessEnabled).ConfigureAwait(false);
                sw.Stop();

                TextRecognized?.Invoke(new OcrFrameResult(
                    text,
                    sw.Elapsed.TotalMilliseconds,
                    frame.CapturedAtTimestamp));
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"OCR hatası: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessing, 0);
            }

            // İşlem sırasında yeni bir kare daha geldiyse döngünün tekrar tetiklenmesini sağla
            if (Volatile.Read(ref _pendingFrame) is not null && _signal.CurrentCount == 0)
            {
                try { _signal.Release(); } catch (SemaphoreFullException) { }
            }
        }
    }

    public async Task<string> RecognizePixelsAsync(
        byte[] bgraPixels,
        int width,
        int height,
        bool preprocess)
    {
        if (_engine is null)
            throw new InvalidOperationException("İngilizce OCR motoru hazır değil.");

        if (preprocess && width is >= 8 and <= 2560 && height is >= 8 and <= 600)
        {
            byte[] processedBytes = PreprocessSubtitleRegionCore(
                bgraPixels,
                width,
                height,
                out int targetW,
                out int targetH,
                out byte[] glyphGrid);

            if (TryReuseCachedOcrResult(glyphGrid, width, height, out string cachedText))
                return cachedText;

            string processedText = await RecognizeBufferAsync(processedBytes, targetW, targetH).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(processedText))
            {
                UpdateGlyphCache(glyphGrid, width, height, processedText);
                return processedText;
            }
        }

        // Ön işleme kapalıysa veya boş döndüyse (ör. kontursuz düz renk yazı) ham karede dene
        string rawText = await RecognizeBufferAsync(bgraPixels, width, height).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawText))
            ResetGlyphCache();
        return rawText;
    }

    private bool TryReuseCachedOcrResult(byte[] glyphGrid, int width, int height, out string cachedText)
    {
        cachedText = string.Empty;
        lock (_glyphCacheLock)
        {
            if (_lastGlyphGrid is null
                || _lastMaskWidth != width
                || _lastMaskHeight != height
                || string.IsNullOrEmpty(_lastOcrResult)
                || _lastGlyphGrid.Length != glyphGrid.Length)
            {
                return false;
            }

            int activeCells = 0;
            long sumDiff = 0;
            int changedCells = 0;

            for (int i = 0; i < glyphGrid.Length; i++)
            {
                byte cur = glyphGrid[i];
                byte prev = _lastGlyphGrid[i];
                if (cur > 4 || prev > 4)
                {
                    activeCells++;
                    int diff = Math.Abs(cur - prev);
                    sumDiff += diff;
                    if (diff > 38)
                        changedCells++;
                }
            }

            if (activeCells < 4)
                return false;

            double avgDiff = (double)sumDiff / activeCells;
            if (avgDiff < 8.0 && changedCells <= 6)
            {
                cachedText = _lastOcrResult;
                return true;
            }

            return false;
        }
    }

    private void UpdateGlyphCache(byte[] glyphGrid, int width, int height, string text)
    {
        lock (_glyphCacheLock)
        {
            _lastGlyphGrid = glyphGrid;
            _lastMaskWidth = width;
            _lastMaskHeight = height;
            _lastOcrResult = text;
        }
    }

    private async Task<string> RecognizeBufferAsync(byte[] bgraPixels, int width, int height)
    {
        if (_engine is null)
            return string.Empty;

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        CopyPixelsIntoSoftwareBitmap(bitmap, bgraPixels);

        var result = await _engine.RecognizeAsync(bitmap).AsTask().ConfigureAwait(false);
        if (result?.Lines is null || result.Lines.Count == 0)
            return string.Empty;

        return JoinOcrLines(result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList());
    }

    public static string JoinOcrLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return string.Empty;

        var validLines = new List<string>(lines.Count);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (HasAtLeastTwoLetters(trimmed) && !SubtitleStabilizer.IsLikelyGarbageLine(trimmed))
                validLines.Add(trimmed);
        }

        if (validLines.Count == 0)
            return string.Empty;

        if (validLines.Count == 1)
            return validLines[0];

        var sb = new System.Text.StringBuilder(validLines[0]);
        for (int i = 1; i < validLines.Count; i++)
        {
            string prev = validLines[i - 1];
            string curr = validLines[i];
            sb.Append(ShouldJoinWithNewline(prev, curr) ? '\n' : ' ');
            sb.Append(curr);
        }

        return sb.ToString();
    }

    private static bool ShouldJoinWithNewline(string prevLine, string currLine)
    {
        if (string.IsNullOrWhiteSpace(prevLine) || string.IsNullOrWhiteSpace(currLine))
            return false;

        char currStart = currLine[0];
        if (currStart is '-' or '\u2014' or '\u2013' or '\u2022' or '[' or '(')
            return true;

        if (HasSpeakerLabelPrefix(currLine))
            return true;

        // Önceki satırın sonundaki anlamlı karakteri bul (tırnak/boşluk hariç)
        string prevTrimmed = prevLine.TrimEnd(' ', '\t', '"', '\'', '\u2019', '\u201D', ')');
        if (prevTrimmed.Length == 0)
            return false;

        if (prevTrimmed.EndsWith("...", StringComparison.Ordinal)
            || prevTrimmed.EndsWith("..", StringComparison.Ordinal)
            || prevTrimmed[^1] is '\u2026' or ',' or ';' or '\u2014' or '\u2013'
            || prevTrimmed.EndsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        char? firstCurrLetter = null;
        foreach (char ch in currLine)
        {
            if (char.IsLetter(ch))
            {
                firstCurrLetter = ch;
                break;
            }
        }

        if (firstCurrLetter is { } l && char.IsLower(l))
            return false;

        char prevEnd = prevTrimmed[^1];
        if (prevEnd is '.' or '!' or '?' && firstCurrLetter is { } upper && char.IsUpper(upper))
            return true;

        return false;
    }

    private static bool HasSpeakerLabelPrefix(string line)
    {
        int colonIdx = line.IndexOf(':');
        if (colonIdx < 2 || colonIdx > 16)
            return false;

        for (int i = 0; i < colonIdx; i++)
        {
            char c = line[i];
            if (!char.IsLetter(c) && c != ' ')
                return false;
        }

        return char.IsLetter(line[0]);
    }

    private static bool HasAtLeastTwoLetters(string text)
    {
        int count = 0;
        foreach (char ch in text)
        {
            if (char.IsLetter(ch))
            {
                count++;
                if (count >= 2)
                    return true;
            }
        }
        return false;
    }

    private static unsafe void CopyPixelsIntoSoftwareBitmap(SoftwareBitmap bitmap, byte[] pixels)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = buffer.CreateReference();
        var memoryBuffer = reference.As<Capture.Direct3D11Interop.IMemoryBufferByteAccess>();
        memoryBuffer.GetBuffer(out byte* dst, out uint capacity);
        int bytesToCopy = Math.Min((int)capacity, pixels.Length);
        fixed (byte* src = pixels)
        {
            Buffer.MemoryCopy(src, dst, capacity, bytesToCopy);
        }
    }

    public static byte[] Upscale2xWithContrast(byte[] src, int w, int h, out int outW, out int outH)
        => PreprocessSubtitleRegion(src, w, h, out outW, out outH);

    public static byte[] PreprocessSubtitleRegion(byte[] src, int w, int h, out int outW, out int outH)
        => PreprocessSubtitleRegionCore(src, w, h, out outW, out outH, out _);

    /// <summary>
    /// Arka plansız altyazılar için kenar tohumlu flood-fill arka plan bastırma,
    /// renk doygunluğu temizleme, küçük boyutta bilinear 1.5x büyütme ve 24px koyu kenar boşluğu ekler.
    /// Harflerin siyah dış konturu baraj görevi görür, anti-aliasing harf pikselleri korunur.
    /// Ayrıca arka plan oynarken harflerin değişip değişmediğini anlamak için 64x16 harf maskesi üretir.
    /// </summary>
    private static unsafe byte[] PreprocessSubtitleRegionCore(
        byte[] src,
        int w,
        int h,
        out int outW,
        out int outH,
        out byte[] glyphGrid)
    {
        const int pad = 24;
        const byte darkBgValue = 20;

        int total = w * h;
        if (w <= 0 || h <= 0 || src.Length < total * 4)
        {
            outW = w;
            outH = h;
            glyphGrid = Array.Empty<byte>();
            return src;
        }

        var lum = new byte[total];
        var sat = new byte[total];
        fixed (byte* pSrc = src)
        fixed (byte* pLum = lum)
        fixed (byte* pSat = sat)
        {
            for (int i = 0; i < total; i++)
            {
                byte* sp = pSrc + (i * 4);
                byte b = sp[0];
                byte g = sp[1];
                byte r = sp[2];
                byte max = Math.Max(r, Math.Max(g, b));
                byte min = Math.Min(r, Math.Min(g, b));
                pLum[i] = (byte)((b * 29 + g * 150 + r * 77) >> 8);
                pSat[i] = (byte)(max - min);
            }
        }

        // 0. İnce gölgeli / hafif konturlu beyaz harfleri flood-fill sızmasına karşı koru
        var protectedGlyph = MarkProtectedWhiteGlyphs(lum, sat, w, h);

        var visitedBg = new bool[total];
        var queue = new int[total];
        int qHead = 0, qTail = 0;

        void SeedIndex(int idx)
        {
            if (!visitedBg[idx] && !protectedGlyph[idx])
            {
                visitedBg[idx] = true;
                queue[qTail++] = idx;
            }
        }

        const byte floodLumThreshold = 132;

        // 1. Üst ve alt kenarda yalnızca geniş (> 6 px) parlak şeritleri tohumla (kenara değen harfleri koru)
        SeedHorizontalRuns(lum, w, 0, minRunLen: 7, lumThreshold: floodLumThreshold, SeedIndex);
        if (h > 1)
            SeedHorizontalRuns(lum, w, h - 1, minRunLen: 7, lumThreshold: floodLumThreshold, SeedIndex);

        // 2. Sol ve sağ kenardaki geniş (> 6 px) parlak şeritleri tohumla
        SeedVerticalRuns(lum, w, h, 0, minRunLen: 7, lumThreshold: floodLumThreshold, SeedIndex);
        if (w > 1)
            SeedVerticalRuns(lum, w, h, w - 1, minRunLen: 7, lumThreshold: floodLumThreshold, SeedIndex);

        // 3. İçerideki çok geniş parlak arka plan şeritlerini tohumla (> max(42, h) px hiçbir tek harf kesiti olamaz)
        int maxGlyphRun = Math.Max(42, h);
        for (int y = 1; y < h - 1; y++)
        {
            SeedHorizontalRuns(lum, w, y, minRunLen: maxGlyphRun + 1, lumThreshold: 140, SeedIndex);
        }

        // 4. 4-yönlü Flood-Fill: koyu altyazı konturu (lum <= 132) ve korumalı beyaz harf pikselleri baraj görevi görür
        while (qHead < qTail)
        {
            int cur = queue[qHead++];
            int cx = cur % w;
            int cy = cur / w;

            if (cx > 0) TryEnqueue(cur - 1);
            if (cx + 1 < w) TryEnqueue(cur + 1);
            if (cy > 0) TryEnqueue(cur - w);
            if (cy + 1 < h) TryEnqueue(cur + w);
        }

        void TryEnqueue(int nIdx)
        {
            if (!visitedBg[nIdx] && !protectedGlyph[nIdx] && lum[nIdx] > floodLumThreshold)
            {
                visitedBg[nIdx] = true;
                queue[qTail++] = nIdx;
            }
        }

        var cleaned = new byte[total];
        for (int i = 0; i < total; i++)
        {
            byte l = lum[i];
            byte s = sat[i];
            if (visitedBg[i] || (!protectedGlyph[i] && s > 55 && l < 230))
            {
                cleaned[i] = darkBgValue;
            }
            else if (protectedGlyph[i])
            {
                cleaned[i] = l >= 175
                    ? (byte)255
                    : l >= 115
                        ? (byte)Math.Clamp(140 + ((l - 115) * 115 / 60), 140, 254)
                        : l;
            }
            else
            {
                cleaned[i] = l;
            }
        }

        glyphGrid = BuildGlyphGrid(cleaned, w, h);

        bool upscale = h < 96;
        double scale = upscale ? 1.5 : 1.0;
        int sw = upscale ? (int)Math.Round(w * scale) : w;
        int sh = upscale ? (int)Math.Round(h * scale) : h;
        outW = sw + (pad * 2);
        outH = sh + (pad * 2);

        var dst = new byte[outW * outH * 4];
        uint bgPixel = 0xFF000000u | ((uint)darkBgValue << 16) | ((uint)darkBgValue << 8) | darkBgValue;

        fixed (byte* pClean = cleaned)
        fixed (byte* pDst = dst)
        {
            uint* pDst32 = (uint*)pDst;
            int outTotal = outW * outH;
            for (int i = 0; i < outTotal; i++)
                pDst32[i] = bgPixel;

            if (!upscale)
            {
                for (int y = 0; y < sh; y++)
                {
                    byte* cRow = pClean + (y * w);
                    uint* dRow = pDst32 + ((y + pad) * outW) + pad;
                    for (int x = 0; x < sw; x++)
                    {
                        uint v = cRow[x];
                        dRow[x] = 0xFF000000u | (v << 16) | (v << 8) | v;
                    }
                }
            }
            else
            {
                double invScale = 1.0 / scale;
                for (int y = 0; y < sh; y++)
                {
                    double sy = Math.Min(h - 1, y * invScale);
                    int y0 = (int)sy;
                    int y1 = Math.Min(h - 1, y0 + 1);
                    double wy = sy - y0;
                    double wy0 = 1.0 - wy;

                    byte* row0 = pClean + (y0 * w);
                    byte* row1 = pClean + (y1 * w);
                    uint* dRow = pDst32 + ((y + pad) * outW) + pad;

                    for (int x = 0; x < sw; x++)
                    {
                        double sx = Math.Min(w - 1, x * invScale);
                        int x0 = (int)sx;
                        int x1 = Math.Min(w - 1, x0 + 1);
                        double wx = sx - x0;
                        double wx0 = 1.0 - wx;

                        double topVal = (row0[x0] * wx0) + (row0[x1] * wx);
                        double botVal = (row1[x0] * wx0) + (row1[x1] * wx);
                        uint v = (uint)Math.Clamp((int)Math.Round((topVal * wy0) + (botVal * wy)), 0, 255);
                        dRow[x] = 0xFF000000u | (v << 16) | (v << 8) | v;
                    }
                }
            }
        }

        return dst;
    }

    private static byte[] BuildGlyphGrid(byte[] cleaned, int w, int h)
    {
        const int gridW = 64;
        const int gridH = 16;
        var grid = new byte[gridW * gridH];

        for (int gy = 0; gy < gridH; gy++)
        {
            int y0 = (gy * h) / gridH;
            int y1 = Math.Min(h, Math.Max(y0 + 1, ((gy + 1) * h) / gridH));
            if (y0 >= h) y0 = h - 1;

            for (int gx = 0; gx < gridW; gx++)
            {
                int x0 = (gx * w) / gridW;
                int x1 = Math.Min(w, Math.Max(x0 + 1, ((gx + 1) * w) / gridW));
                if (x0 >= w) x0 = w - 1;

                int sum = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowOffset = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        byte v = cleaned[rowOffset + x];
                        if (v > 145)
                            sum += v - 145;
                        count++;
                    }
                }

                grid[(gy * gridW) + gx] = count > 0 ? (byte)Math.Min(255, (sum * 2) / count) : (byte)0;
            }
        }

        return grid;
    }

    private static bool[] MarkProtectedWhiteGlyphs(byte[] lum, byte[] sat, int w, int h)
    {
        var protectedGlyph = new bool[w * h];

        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * w;
            int runStart = -1;

            for (int x = 0; x <= w; x++)
            {
                bool isWhiteGlyphCandidate = x < w
                    && lum[rowOffset + x] >= 170
                    && sat[rowOffset + x] <= 46;

                if (isWhiteGlyphCandidate && runStart < 0)
                {
                    runStart = x;
                }
                else if (!isWhiteGlyphCandidate && runStart >= 0)
                {
                    int runLen = x - runStart;
                    if (runLen <= 44)
                    {
                        int yMin = Math.Max(0, y - 3);
                        int yMax = Math.Min(h - 1, y + 3);

                        for (int rx = runStart; rx < x; rx++)
                        {
                            int idx = rowOffset + rx;
                            byte centerLum = lum[idx];
                            int xMin = Math.Max(0, rx - 3);
                            int xMax = Math.Min(w - 1, rx + 3);
                            bool hasShadowOrEdge = false;

                            for (int ny = yMin; ny <= yMax && !hasShadowOrEdge; ny++)
                            {
                                int nRow = ny * w;
                                for (int nx = xMin; nx <= xMax; nx++)
                                {
                                    byte nLum = lum[nRow + nx];
                                    if (nLum <= 118 || (centerLum - nLum) >= 55)
                                    {
                                        hasShadowOrEdge = true;
                                        break;
                                    }
                                }
                            }

                            if (hasShadowOrEdge)
                                protectedGlyph[idx] = true;
                        }
                    }

                    runStart = -1;
                }
            }
        }

        // Anti-aliased gri harf kenarlarının flood-fill tarafından yenmesini önlemek için 1 piksellik genişletme (dilation)
        var dilated = (bool[])protectedGlyph.Clone();
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * w;
            int yMin = Math.Max(0, y - 1);
            int yMax = Math.Min(h - 1, y + 1);

            for (int x = 0; x < w; x++)
            {
                if (!protectedGlyph[rowOffset + x])
                    continue;

                int xMin = Math.Max(0, x - 1);
                int xMax = Math.Min(w - 1, x + 1);
                for (int ny = yMin; ny <= yMax; ny++)
                {
                    int nRow = ny * w;
                    for (int nx = xMin; nx <= xMax; nx++)
                    {
                        int nIdx = nRow + nx;
                        if (!dilated[nIdx] && lum[nIdx] >= 115 && sat[nIdx] <= 60)
                            dilated[nIdx] = true;
                    }
                }
            }
        }

        return dilated;
    }

    private static void SeedHorizontalRuns(byte[] lum, int w, int y, int minRunLen, byte lumThreshold, Action<int> seed)
    {
        int rowOffset = y * w;
        int runStart = -1;
        for (int x = 0; x <= w; x++)
        {
            bool bright = x < w && lum[rowOffset + x] > lumThreshold;
            if (bright && runStart < 0)
            {
                runStart = x;
            }
            else if (!bright && runStart >= 0)
            {
                if (x - runStart >= minRunLen)
                {
                    for (int rx = runStart; rx < x; rx++)
                        seed(rowOffset + rx);
                }
                runStart = -1;
            }
        }
    }

    private static void SeedVerticalRuns(byte[] lum, int w, int h, int x, int minRunLen, byte lumThreshold, Action<int> seed)
    {
        int runStart = -1;
        for (int y = 0; y <= h; y++)
        {
            bool bright = y < h && lum[(y * w) + x] > lumThreshold;
            if (bright && runStart < 0)
            {
                runStart = y;
            }
            else if (!bright && runStart >= 0)
            {
                if (y - runStart >= minRunLen)
                {
                    for (int ry = runStart; ry < y; ry++)
                        seed((ry * w) + x);
                }
                runStart = -1;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _signal.Dispose();
        _cts.Dispose();
    }
}
