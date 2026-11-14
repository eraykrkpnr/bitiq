using System.Diagnostics;
using Bitiq.Core.Capture;
using Bitiq.Core.Translation;
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
    private volatile OcrEngine? _engine;
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
    public string? ActiveLanguageTag { get; private set; }
    public OcrCheckResult Status { get; private set; } = new(false, null, null, null);
    public string ActiveSourceLanguageCode { get; private set; } = "en";

    public event Action<OcrFrameResult>? TextRecognized;
    public event Action<string>? ErrorOccurred;

    public WindowsOcrService(bool preprocessEnabled = false, string sourceLanguageCode = "en")
    {
        _preprocessEnabled = preprocessEnabled;
        Status = SetSourceLanguage(sourceLanguageCode);
        _ = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public OcrCheckResult SetSourceLanguage(string langCode)
    {
        var descriptor = LanguageCatalog.GetByCode(langCode);
        bool isEnglish = string.Equals(descriptor.Code, "en", StringComparison.OrdinalIgnoreCase);
        SubtitleStabilizer.IsEnglishSourceMode = isEnglish;

        OcrEngine? newEngine;
        OcrCheckResult result;

        if (isEnglish)
        {
            result = CheckEnglishOcrAvailability(out newEngine);
        }
        else if (IsLanguageOcrInstalled(descriptor.Code, out var matchedLang) && matchedLang is not null)
        {
            newEngine = OcrEngine.TryCreateFromLanguage(matchedLang);
            result = newEngine is not null
                ? new OcrCheckResult(true, matchedLang.LanguageTag, null, null)
                : new OcrCheckResult(
                    false,
                    null,
                    $"Windows {descriptor.NameTr} ({descriptor.OcrCapabilityTag}) OCR motoru başlatılamadı.",
                    $"Add-WindowsCapability -Online -Name Language.OCR~~~{descriptor.OcrCapabilityTag}~0.0.1.0");
        }
        else
        {
            newEngine = null;
            result = new OcrCheckResult(
                false,
                null,
                $"Windows {descriptor.NameTr} ({descriptor.OcrCapabilityTag}) OCR dil paketi yüklü değil.",
                $"Add-WindowsCapability -Online -Name Language.OCR~~~{descriptor.OcrCapabilityTag}~0.0.1.0");
        }

        _engine = newEngine;
        ActiveSourceLanguageCode = descriptor.Code;
        ActiveLanguageTag = result.LanguageTag;
        Status = result;
        ResetGlyphCache();
        return result;
    }

    public static bool IsLanguageOcrInstalled(string langCode, out Language? matchedRecognizerLanguage)
    {
        matchedRecognizerLanguage = null;
        var descriptor = LanguageCatalog.GetByCode(langCode);
        try
        {
            var available = OcrEngine.AvailableRecognizerLanguages;
            var exact = available.FirstOrDefault(l =>
                l.LanguageTag.Equals(descriptor.OcrCapabilityTag, StringComparison.OrdinalIgnoreCase));
            var prefix = exact ?? available.FirstOrDefault(l =>
                l.LanguageTag.Equals(descriptor.Code, StringComparison.OrdinalIgnoreCase)
                || l.LanguageTag.StartsWith(descriptor.Code + "-", StringComparison.OrdinalIgnoreCase));

            if (prefix is not null && OcrEngine.TryCreateFromLanguage(prefix) is not null)
            {
                matchedRecognizerLanguage = prefix;
                return true;
            }

            var cand = new Language(descriptor.OcrCapabilityTag);
            if (OcrEngine.IsLanguageSupported(cand) && OcrEngine.TryCreateFromLanguage(cand) is not null)
            {
                matchedRecognizerLanguage = cand;
                return true;
            }
        }
        catch { }

        return false;
    }

    public static async Task<(bool Success, string Message)> InstallOcrLanguagePackAsync(
        string langCode,
        bool isEnglishUi,
        CancellationToken ct = default)
    {
        var descriptor = LanguageCatalog.GetByCode(langCode);
        if (IsLanguageOcrInstalled(descriptor.Code, out _))
        {
            return (true, isEnglishUi
                ? $"{descriptor.NameEn} OCR pack is already installed."
                : $"{descriptor.NameTr} OCR paketi zaten yüklü.");
        }

        string exactCapability = $"Language.OCR~~~{descriptor.OcrCapabilityTag}~0.0.1.0";
        string langPrefix = $"Language.OCR~~~{descriptor.Code}-";
        string psScript =
            $"$ErrorActionPreference='Stop'; " +
            $"try {{ Add-WindowsCapability -Online -Name '{exactCapability}' | Out-Null }} " +
            $"catch {{ " +
            $"$cap = Get-WindowsCapability -Online -Name 'Language.OCR*' | Where-Object {{ $_.Name -like '{langPrefix}*' }} | Select-Object -First 1; " +
            $"if ($null -eq $cap) {{ throw }}; " +
            $"Add-WindowsCapability -Online -Name $cap.Name | Out-Null " +
            $"}}";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, isEnglishUi
                    ? "Could not launch Windows OCR capability installer."
                    : "Windows OCR paket yükleyicisi başlatılamadı.");
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.Delay(250, ct).ConfigureAwait(false);

            if (IsLanguageOcrInstalled(descriptor.Code, out _))
            {
                return (true, isEnglishUi
                    ? $"{descriptor.NameEn} OCR language pack installed and activated."
                    : $"{descriptor.NameTr} OCR dil paketi kuruldu ve aktifleştirildi.");
            }

            return (false, isEnglishUi
                ? $"OCR installer exited (code {proc.ExitCode}), but {descriptor.NameEn} OCR is not active yet. You can also install it from Windows Settings → Time & language → Language & region."
                : $"OCR yükleyicisi tamamlandı (kod {proc.ExitCode}), ancak {descriptor.NameTr} OCR henüz aktif görünmüyor. Windows Ayarları → Zaman ve Dil → Dil ve Bölge kısmından da yükleyebilirsiniz.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, isEnglishUi
                ? "Administrator permission (UAC) was cancelled."
                : "Yönetici izni (UAC) iptal edildi.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, isEnglishUi
                ? $"OCR pack installation failed: {ex.Message}"
                : $"OCR paketi kurulamadı: {ex.Message}");
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
            int procScore = ScoreOcrCandidate(processedText, out int procClean, out int procGarbage);

            // Temiz okuma alındıysa ikinci OCR geçişine gerek kalmadan doğrudan önbellekle ve dön
            if (!string.IsNullOrWhiteSpace(processedText) && procGarbage == 0 && procClean > 0 && procScore > 0)
            {
                UpdateGlyphCache(glyphGrid, width, height, processedText);
                return processedText;
            }

            // Ön işleme boş döndüyse veya bozuk kelime ürettiyse, 2x büyütülmüş ham kare ile karşılaştırıp daha temizini seç
            string fallbackText = await RecognizeFallbackAsync(bgraPixels, width, height).ConfigureAwait(false);
            int fallScore = ScoreOcrCandidate(fallbackText, out int fallClean, out int fallGarbage);

            string chosen = ChooseBetterOcrResult(
                processedText, procScore,
                fallbackText, fallScore,
                out int chosenClean, out int chosenGarbage);

            if (!string.IsNullOrWhiteSpace(chosen) && chosenGarbage == 0 && chosenClean > 0)
                UpdateGlyphCache(glyphGrid, width, height, chosen);
            else if (string.IsNullOrWhiteSpace(chosen))
                ResetGlyphCache();

            return chosen;
        }

        // Ön işleme kapalıysa küçük karelerde 2x bicubic ile oku
        string rawText = await RecognizeFallbackAsync(bgraPixels, width, height).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawText))
            ResetGlyphCache();
        return rawText;
    }

    private Task<string> RecognizeFallbackAsync(byte[] bgraPixels, int width, int height)
    {
        if (height < 120 && width is >= 8 and <= 1920)
        {
            byte[] upscaled = UpscaleBgraBicubic2x(bgraPixels, width, height, out int upW, out int upH);
            return RecognizeBufferAsync(upscaled, upW, upH);
        }

        return RecognizeBufferAsync(bgraPixels, width, height);
    }

    private static int ScoreOcrCandidate(string text, out int cleanWords, out int garbageWords)
    {
        cleanWords = 0;
        garbageWords = 0;
        if (string.IsNullOrWhiteSpace(text))
            return -999;

        SubtitleStabilizer.ComputeWordQuality(text, out cleanWords, out garbageWords, out _);

        int extraPenalty = 0;
        if (SubtitleStabilizer.IsEnglishSourceMode)
        {
            // İngilizce OCR sırasında arka plan parazitinden doğan İskandinav/aksan halüsinasyonlarını ve eşleşmemiş tırnakları cezalandır
            int quoteCount = 0;
            foreach (char ch in text)
            {
                if (ch == '"')
                    quoteCount++;
                else if (ch is 'æ' or 'Æ' or 'ø' or 'Ø' or 'å' or 'Å' or 'ä' or 'Ä' or 'ö' or 'Ö' or 'ü' or 'Ü' or 'þ' or 'Þ' or 'ß' or '\u201E' or '•')
                    extraPenalty += 3;
            }
            if ((quoteCount & 1) == 1)
                extraPenalty += 2;
        }

        return (cleanWords * 3) - (garbageWords * 5) - extraPenalty;
    }

    private static string ChooseBetterOcrResult(
        string procText, int procScore,
        string fallText, int fallScore,
        out int chosenClean, out int chosenGarbage)
    {
        bool hasProc = !string.IsNullOrWhiteSpace(procText);
        bool hasFall = !string.IsNullOrWhiteSpace(fallText);

        string chosen;
        if (!hasProc && !hasFall)
        {
            chosen = string.Empty;
        }
        else if (!hasFall)
        {
            chosen = procText;
        }
        else if (!hasProc)
        {
            chosen = fallText;
        }
        else if (fallScore > procScore || (fallScore == procScore && fallText.Length > procText.Length))
        {
            chosen = fallText;
        }
        else
        {
            chosen = procText;
        }

        if (string.IsNullOrEmpty(chosen))
        {
            chosenClean = 0;
            chosenGarbage = 0;
        }
        else
        {
            SubtitleStabilizer.ComputeWordQuality(chosen, out chosenClean, out chosenGarbage, out _);
        }

        return chosen;
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
        var engine = _engine;
        if (engine is null)
            return string.Empty;

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        CopyPixelsIntoSoftwareBitmap(bitmap, bgraPixels);

        var result = await engine.RecognizeAsync(bitmap).AsTask().ConfigureAwait(false);
        if (result?.Lines is null || result.Lines.Count == 0)
            return string.Empty;

        return JoinOcrLines(OrderOcrLinesTopToBottomLeftToRight(result.Lines));
    }

    private static List<string> OrderOcrLinesTopToBottomLeftToRight(IReadOnlyList<OcrLine> ocrLines)
    {
        var fragments = new List<(double CenterY, double MinX, double Height, string Text)>(ocrLines.Count);
        foreach (var line in ocrLines)
        {
            string trimmed = line.Text.Trim();
            if (trimmed.Length == 0)
                continue;

            double minX = double.MaxValue;
            double sumCenterY = 0;
            double maxH = 0;
            int wordCount = 0;
            foreach (var word in line.Words)
            {
                var rect = word.BoundingRect;
                if (rect.X < minX)
                    minX = rect.X;
                sumCenterY += rect.Y + (rect.Height * 0.5);
                if (rect.Height > maxH)
                    maxH = rect.Height;
                wordCount++;
            }

            if (wordCount == 0)
                fragments.Add((0, 0, 20, trimmed));
            else
                fragments.Add((sumCenterY / wordCount, minX, maxH, trimmed));
        }

        if (fragments.Count <= 1)
            return fragments.Select(f => f.Text).ToList();

        fragments.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));

        var rows = new List<(double AvgY, double MaxH, List<(double MinX, string Text)> Items)>();
        foreach (var frag in fragments)
        {
            bool placed = false;
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                double tolerance = Math.Max(10.0, Math.Max(row.MaxH, frag.Height) * 0.65);
                if (Math.Abs(frag.CenterY - row.AvgY) <= tolerance)
                {
                    int n = row.Items.Count;
                    double newAvgY = ((row.AvgY * n) + frag.CenterY) / (n + 1);
                    row.Items.Add((frag.MinX, frag.Text));
                    rows[r] = (newAvgY, Math.Max(row.MaxH, frag.Height), row.Items);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                rows.Add((frag.CenterY, frag.Height, [(frag.MinX, frag.Text)]));
            }
        }

        rows.Sort((a, b) => a.AvgY.CompareTo(b.AvgY));
        var ordered = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Items.Count == 1)
            {
                ordered.Add(row.Items[0].Text);
            }
            else
            {
                row.Items.Sort((a, b) => a.MinX.CompareTo(b.MinX));
                ordered.Add(string.Join(" ", row.Items.Select(x => x.Text)));
            }
        }

        return ordered;
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
                if (!SubtitleStabilizer.IsEnglishSourceMode && ch >= '\u2E80')
                    return true;

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
    /// Küçük alt yazı bölgelerini Catmull-Rom bicubic ile 2x büyütür, yerel medyan arka plan tahmini
    /// ve koyu gölge ağırlığı ile arka plansız beyaz yazıları parlak/hareketli sahnelerden ayırır.
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

        int srcTotal = w * h;
        if (w <= 0 || h <= 0 || src.Length < srcTotal * 4)
        {
            outW = w;
            outH = h;
            glyphGrid = Array.Empty<byte>();
            return src;
        }

        bool upscale = h < 120 && w <= 1920;
        int sw, sh;
        byte[] workBgra;
        if (upscale)
        {
            workBgra = UpscaleBgraBicubic2x(src, w, h, out sw, out sh);
        }
        else
        {
            workBgra = src;
            sw = w;
            sh = h;
        }

        int ds = (upscale || h < 120) ? 2 : 3;
        int halfW = Math.Max(1, w / ds);
        int halfH = Math.Max(1, h / ds);

        byte[] wHalf = ComputeDownsampledWhiteness(src, w, h, ds, halfW, halfH);
        byte[] bgHalf = ApplyMedian7x7(wHalf, halfW, halfH);

        int workTotal = sw * sh;
        var whitenessWork = System.Buffers.ArrayPool<byte>.Shared.Rent(workTotal);
        var lumWork = System.Buffers.ArrayPool<byte>.Shared.Rent(workTotal);
        var localMin = System.Buffers.ArrayPool<byte>.Shared.Rent(workTotal);
        var cleaned = System.Buffers.ArrayPool<byte>.Shared.Rent(workTotal);

        try
        {
            fixed (byte* pWork = workBgra)
            fixed (byte* pWhite = whitenessWork)
            fixed (byte* pLum = lumWork)
            {
                for (int i = 0; i < workTotal; i++)
                {
                    byte* sp = pWork + (i * 4);
                    byte b = sp[0];
                    byte g = sp[1];
                    byte r = sp[2];
                    byte max = Math.Max(r, Math.Max(g, b));
                    byte min = Math.Min(r, Math.Min(g, b));
                    int wVal = min - (((max - min) * 3) >> 1);
                    pWhite[i] = wVal <= 0 ? (byte)0 : (byte)wVal;
                    pLum[i] = (byte)(((r * 299) + (g * 587) + (b * 114)) / 1000);
                }
            }

            ComputeSeparableMin(lumWork, localMin, sw, sh, radius: upscale ? 3 : 2);

            outW = sw + (pad * 2);
            outH = sh + (pad * 2);
            var dst = new byte[outW * outH * 4];
            uint bgPixel = 0xFF000000u | ((uint)darkBgValue << 16) | ((uint)darkBgValue << 8) | darkBgValue;

            var hx0Arr = new int[sw];
            var hx1Arr = new int[sw];
            var wxArr = new float[sw];
            double scaleX = (double)halfW / sw;
            double scaleY = (double)halfH / sh;

            for (int x = 0; x < sw; x++)
            {
                double hx = Math.Clamp(((x + 0.5) * scaleX) - 0.5, 0.0, halfW - 1);
                int ix0 = (int)hx;
                int ix1 = Math.Min(halfW - 1, ix0 + 1);
                hx0Arr[x] = ix0;
                hx1Arr[x] = ix1;
                wxArr[x] = (float)(hx - ix0);
            }

            fixed (byte* pBgHalf = bgHalf)
            fixed (byte* pWhite = whitenessWork)
            fixed (byte* pLum = lumWork)
            fixed (byte* pMin = localMin)
            fixed (byte* pClean = cleaned)
            fixed (byte* pDst = dst)
            fixed (int* pHx0 = hx0Arr)
            fixed (int* pHx1 = hx1Arr)
            fixed (float* pWx = wxArr)
            {
                uint* pDst32 = (uint*)pDst;
                int outTotal = outW * outH;
                for (int i = 0; i < outTotal; i++)
                    pDst32[i] = bgPixel;

                for (int y = 0; y < sh; y++)
                {
                    double hy = Math.Clamp(((y + 0.5) * scaleY) - 0.5, 0.0, halfH - 1);
                    int iy0 = (int)hy;
                    int iy1 = Math.Min(halfH - 1, iy0 + 1);
                    float wy = (float)(hy - iy0);
                    float wy0 = 1.0f - wy;

                    byte* bgRow0 = pBgHalf + (iy0 * halfW);
                    byte* bgRow1 = pBgHalf + (iy1 * halfW);
                    int rowBase = y * sw;
                    uint* dRow = pDst32 + ((y + pad) * outW) + pad;

                    for (int x = 0; x < sw; x++)
                    {
                        int ix0 = pHx0[x];
                        int ix1 = pHx1[x];
                        float wx = pWx[x];
                        float wx0 = 1.0f - wx;

                        float topBg = (bgRow0[ix0] * wx0) + (bgRow0[ix1] * wx);
                        float botBg = (bgRow1[ix0] * wx0) + (bgRow1[ix1] * wx);
                        float bg = (topBg * wy0) + (botBg * wy);

                        int idx = rowBase + x;
                        float bgFloor = bg > 60f ? bg : 60f;
                        float diffFromBg = pWhite[idx] - bgFloor;

                        byte val;
                        if (diffFromBg <= 0f)
                        {
                            val = darkBgValue;
                        }
                        else
                        {
                            float headroom = 255f - bgFloor;
                            if (headroom < 40f)
                                headroom = 40f;

                            float score = diffFromBg / headroom;
                            if (score > 1f)
                                score = 1f;

                            if (bg > 110f)
                            {
                                float shadowWeight = (pLum[idx] - pMin[idx] - 25f) * (1f / 40f);
                                if (shadowWeight <= 0f)
                                    score = 0f;
                                else if (shadowWeight < 1f)
                                    score *= shadowWeight;
                            }

                            int scaled = (int)(20.5f + (score * 235f));
                            val = (byte)Math.Clamp(scaled, darkBgValue, 255);
                        }

                        pClean[idx] = val;
                        uint v = val;
                        dRow[x] = 0xFF000000u | (v << 16) | (v << 8) | v;
                    }
                }
            }

            glyphGrid = BuildGlyphGrid(cleaned, sw, sh);
            return dst;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(whitenessWork);
            System.Buffers.ArrayPool<byte>.Shared.Return(lumWork);
            System.Buffers.ArrayPool<byte>.Shared.Return(localMin);
            System.Buffers.ArrayPool<byte>.Shared.Return(cleaned);
        }
    }

    private static unsafe byte[] UpscaleBgraBicubic2x(byte[] src, int w, int h, out int outW, out int outH)
    {
        outW = w * 2;
        outH = h * 2;
        var dst = new byte[outW * outH * 4];
        byte[] horiz = System.Buffers.ArrayPool<byte>.Shared.Rent(outW * h * 4);

        try
        {
            int srcStride = w * 4;
            int horizStride = outW * 4;

            fixed (byte* pSrc = src)
            fixed (byte* pHoriz = horiz)
            fixed (byte* pDst = dst)
            {
                // Yatay Catmull-Rom 2x geçişi: [-3, 29, 111, -9]/128 ve [-9, 111, 29, -3]/128
                for (int y = 0; y < h; y++)
                {
                    byte* sRow = pSrc + (y * srcStride);
                    byte* hRow = pHoriz + (y * horizStride);

                    int kEndSafe = w - 2;
                    int kHeadEnd = Math.Min(w, 2);
                    for (int k = 0; k < kHeadEnd; k++)
                    {
                        InterpolateHorizPair(sRow, hRow, k, w);
                    }

                    for (int k = 2; k < kEndSafe; k++)
                    {
                        byte* p0 = sRow + ((k - 2) * 4);
                        byte* p1 = p0 + 4;
                        byte* p2 = p0 + 8;
                        byte* p3 = p0 + 12;
                        byte* p4 = p0 + 16;
                        byte* d0 = hRow + (k * 8);
                        byte* d1 = d0 + 4;

                        d0[0] = ClampByte((-3 * p0[0] + 29 * p1[0] + 111 * p2[0] - 9 * p3[0] + 64) >> 7);
                        d0[1] = ClampByte((-3 * p0[1] + 29 * p1[1] + 111 * p2[1] - 9 * p3[1] + 64) >> 7);
                        d0[2] = ClampByte((-3 * p0[2] + 29 * p1[2] + 111 * p2[2] - 9 * p3[2] + 64) >> 7);
                        d0[3] = 255;

                        d1[0] = ClampByte((-9 * p1[0] + 111 * p2[0] + 29 * p3[0] - 3 * p4[0] + 64) >> 7);
                        d1[1] = ClampByte((-9 * p1[1] + 111 * p2[1] + 29 * p3[1] - 3 * p4[1] + 64) >> 7);
                        d1[2] = ClampByte((-9 * p1[2] + 111 * p2[2] + 29 * p3[2] - 3 * p4[2] + 64) >> 7);
                        d1[3] = 255;
                    }

                    for (int k = Math.Max(kHeadEnd, kEndSafe); k < w; k++)
                    {
                        InterpolateHorizPair(sRow, hRow, k, w);
                    }
                }

                // Dikey Catmull-Rom 2x geçişi
                for (int ky = 0; ky < h; ky++)
                {
                    byte* r0 = pHoriz + (Math.Clamp(ky - 2, 0, h - 1) * horizStride);
                    byte* r1 = pHoriz + (Math.Clamp(ky - 1, 0, h - 1) * horizStride);
                    byte* r2 = pHoriz + (ky * horizStride);
                    byte* r3 = pHoriz + (Math.Clamp(ky + 1, 0, h - 1) * horizStride);
                    byte* r4 = pHoriz + (Math.Clamp(ky + 2, 0, h - 1) * horizStride);

                    byte* dRow0 = pDst + ((ky * 2) * horizStride);
                    byte* dRow1 = dRow0 + horizStride;

                    for (int off = 0; off < horizStride; off += 4)
                    {
                        byte c0B = ClampByte((-3 * r0[off] + 29 * r1[off] + 111 * r2[off] - 9 * r3[off] + 64) >> 7);
                        byte c0G = ClampByte((-3 * r0[off + 1] + 29 * r1[off + 1] + 111 * r2[off + 1] - 9 * r3[off + 1] + 64) >> 7);
                        byte c0R = ClampByte((-3 * r0[off + 2] + 29 * r1[off + 2] + 111 * r2[off + 2] - 9 * r3[off + 2] + 64) >> 7);

                        dRow0[off] = c0B;
                        dRow0[off + 1] = c0G;
                        dRow0[off + 2] = c0R;
                        dRow0[off + 3] = 255;

                        byte c1B = ClampByte((-9 * r1[off] + 111 * r2[off] + 29 * r3[off] - 3 * r4[off] + 64) >> 7);
                        byte c1G = ClampByte((-9 * r1[off + 1] + 111 * r2[off + 1] + 29 * r3[off + 1] - 3 * r4[off + 1] + 64) >> 7);
                        byte c1R = ClampByte((-9 * r1[off + 2] + 111 * r2[off + 2] + 29 * r3[off + 2] - 3 * r4[off + 2] + 64) >> 7);

                        dRow1[off] = c1B;
                        dRow1[off + 1] = c1G;
                        dRow1[off + 2] = c1R;
                        dRow1[off + 3] = 255;
                    }
                }
            }

            return dst;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(horiz);
        }

        static void InterpolateHorizPair(byte* sRow, byte* hRow, int k, int w)
        {
            byte* p0 = sRow + (Math.Clamp(k - 2, 0, w - 1) * 4);
            byte* p1 = sRow + (Math.Clamp(k - 1, 0, w - 1) * 4);
            byte* p2 = sRow + (k * 4);
            byte* p3 = sRow + (Math.Clamp(k + 1, 0, w - 1) * 4);
            byte* p4 = sRow + (Math.Clamp(k + 2, 0, w - 1) * 4);
            byte* d0 = hRow + (k * 8);
            byte* d1 = d0 + 4;

            d0[0] = ClampByte((-3 * p0[0] + 29 * p1[0] + 111 * p2[0] - 9 * p3[0] + 64) >> 7);
            d0[1] = ClampByte((-3 * p0[1] + 29 * p1[1] + 111 * p2[1] - 9 * p3[1] + 64) >> 7);
            d0[2] = ClampByte((-3 * p0[2] + 29 * p1[2] + 111 * p2[2] - 9 * p3[2] + 64) >> 7);
            d0[3] = 255;

            d1[0] = ClampByte((-9 * p1[0] + 111 * p2[0] + 29 * p3[0] - 3 * p4[0] + 64) >> 7);
            d1[1] = ClampByte((-9 * p1[1] + 111 * p2[1] + 29 * p3[1] - 3 * p4[1] + 64) >> 7);
            d1[2] = ClampByte((-9 * p1[2] + 111 * p2[2] + 29 * p3[2] - 3 * p4[2] + 64) >> 7);
            d1[3] = 255;
        }

        static byte ClampByte(int v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
    }

    private static unsafe byte[] ComputeDownsampledWhiteness(byte[] src, int w, int h, int ds, int halfW, int halfH)
    {
        var result = new byte[halfW * halfH];
        fixed (byte* pSrc = src)
        fixed (byte* pRes = result)
        {
            for (int hy = 0; hy < halfH; hy++)
            {
                int y0 = hy * ds;
                int y1 = Math.Min(h, y0 + ds);
                for (int hx = 0; hx < halfW; hx++)
                {
                    int x0 = hx * ds;
                    int x1 = Math.Min(w, x0 + ds);
                    int sum = 0;
                    int count = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        byte* row = pSrc + (y * w * 4);
                        for (int x = x0; x < x1; x++)
                        {
                            byte* sp = row + (x * 4);
                            byte b = sp[0];
                            byte g = sp[1];
                            byte r = sp[2];
                            byte max = Math.Max(r, Math.Max(g, b));
                            byte min = Math.Min(r, Math.Min(g, b));
                            int wVal = min - (((max - min) * 3) >> 1);
                            if (wVal > 0)
                                sum += wVal;
                            count++;
                        }
                    }
                    pRes[(hy * halfW) + hx] = count > 0 ? (byte)(sum / count) : (byte)0;
                }
            }
        }
        return result;
    }

    private static unsafe byte[] ApplyMedian7x7(byte[] src, int w, int h)
    {
        var dst = new byte[w * h];
        int* hist = stackalloc int[256];

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            for (int y = 0; y < h; y++)
            {
                for (int i = 0; i < 256; i++)
                    hist[i] = 0;

                for (int dy = -3; dy <= 3; dy++)
                {
                    int ny = Math.Clamp(y + dy, 0, h - 1);
                    byte* row = pSrc + (ny * w);
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int nx = Math.Clamp(dx, 0, w - 1);
                        hist[row[nx]]++;
                    }
                }

                int median = 0;
                int lt = 0;
                while (lt + hist[median] <= 24)
                {
                    lt += hist[median];
                    median++;
                }

                int dstRow = y * w;
                pDst[dstRow] = (byte)median;

                for (int x = 1; x < w; x++)
                {
                    int remX = Math.Clamp(x - 4, 0, w - 1);
                    int addX = Math.Clamp(x + 3, 0, w - 1);
                    if (remX != addX)
                    {
                        for (int dy = -3; dy <= 3; dy++)
                        {
                            int ny = Math.Clamp(y + dy, 0, h - 1);
                            byte* row = pSrc + (ny * w);

                            byte rv = row[remX];
                            hist[rv]--;
                            if (rv < median)
                                lt--;

                            byte av = row[addX];
                            hist[av]++;
                            if (av < median)
                                lt++;
                        }

                        while (lt > 24)
                        {
                            median--;
                            lt -= hist[median];
                        }
                        while (lt + hist[median] <= 24)
                        {
                            lt += hist[median];
                            median++;
                        }
                    }

                    pDst[dstRow + x] = (byte)median;
                }
            }
        }

        return dst;
    }

    private static unsafe void ComputeSeparableMin(byte[] src, byte[] dst, int w, int h, int radius)
    {
        var rowMin = System.Buffers.ArrayPool<byte>.Shared.Rent(w * h);
        try
        {
            fixed (byte* pSrc = src)
            fixed (byte* pRowMin = rowMin)
            fixed (byte* pDst = dst)
            {
                for (int y = 0; y < h; y++)
                {
                    int rowOff = y * w;
                    byte* sRow = pSrc + rowOff;
                    byte* mRow = pRowMin + rowOff;

                    for (int x = 0; x < w; x++)
                    {
                        int x0 = x - radius;
                        if (x0 < 0) x0 = 0;
                        int x1 = x + radius;
                        if (x1 >= w) x1 = w - 1;

                        byte minVal = sRow[x0];
                        for (int nx = x0 + 1; nx <= x1; nx++)
                        {
                            if (sRow[nx] < minVal)
                                minVal = sRow[nx];
                        }
                        mRow[x] = minVal;
                    }
                }

                for (int y = 0; y < h; y++)
                {
                    int y0 = y - radius;
                    if (y0 < 0) y0 = 0;
                    int y1 = y + radius;
                    if (y1 >= h) y1 = h - 1;
                    byte* dRow = pDst + (y * w);

                    for (int x = 0; x < w; x++)
                    {
                        byte minVal = pRowMin[(y0 * w) + x];
                        for (int ny = y0 + 1; ny <= y1; ny++)
                        {
                            byte v = pRowMin[(ny * w) + x];
                            if (v < minVal)
                                minVal = v;
                        }
                        dRow[x] = minVal;
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rowMin);
        }
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
