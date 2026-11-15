using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bitiq.Core.Ocr;

/// <summary>
/// Windows 11 Ekran Alıntısı Aracı (OneOCR: oneocr.dll + oneocr.onemodel + onnxruntime.dll)
/// yerel ONNX OCR motoru sarmalayıcısı. Uygulama dizini altındaki oneocr\ klasöründen
/// veya kullanıcı yerel dizininden çalışma dosyalarını otomatik keşfeder.
/// </summary>
public sealed partial class OneOcrNativeEngine : IDisposable
{
    private const string OcrPipelineKey = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
    private const long MaxRecognitionLineCount = 1000;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(10);

    private static readonly object ResolverLock = new();
    private static string? s_resolvedRuntimeDir;
    private static bool s_resolverRegistered;
    private static IntPtr s_loadedOnnxRuntimeHandle;
    private static IntPtr s_loadedOneOcrHandle;

    private readonly object _initLock = new();
    private readonly object _runLock = new();

    private string? _runtimeDirectory;
    private long _context;
    private long _pipeline;
    private long _processOptions;
    private bool _isInitialized;
    private bool _disposed;
    private DateTime _lastFailedInitUtc = DateTime.MinValue;
    private string? _lastError;

    public bool IsInitialized => _isInitialized;
    public string? RuntimeDirectory => _runtimeDirectory;
    public string? LastError => _lastError;

    public static string LocalCustomDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "bitiq",
        "oneocr");

    public static OcrCheckResult CheckAvailability()
    {
        string? dir = DiscoverRuntimeDirectory();
        if (dir is not null)
        {
            return new OcrCheckResult(true, "oneocr-multi", null, null);
        }

        return new OcrCheckResult(
            false,
            null,
            "OneOCR dosyaları (oneocr.dll, oneocr.onemodel, onnxruntime.dll) uygulama veya yerel klasörde bulunamadı.",
            $"OneOCR'ı kullanmak için oneocr.dll, oneocr.onemodel ve onnxruntime.dll dosyalarını uygulama yanındaki 'oneocr' klasörüne ({Path.Combine(AppContext.BaseDirectory, "oneocr")}) veya {LocalCustomDirectory} klasörüne yerleştirin.");
    }

    public static string? DiscoverRuntimeDirectory()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "oneocr"),
            AppContext.BaseDirectory,
            LocalCustomDirectory
        ];

        foreach (string dir in candidates)
        {
            if (HasReadableOneOcrFiles(dir))
                return dir;
        }

        return null;
    }

    public bool EnsureInitialized(out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isInitialized)
        {
            errorMessage = null;
            return true;
        }

        lock (_initLock)
        {
            if (_isInitialized)
            {
                errorMessage = null;
                return true;
            }

            if (DateTime.UtcNow - _lastFailedInitUtc < RetryCooldown && _lastError is not null)
            {
                errorMessage = _lastError;
                return false;
            }

            string? dir = DiscoverRuntimeDirectory();
            if (dir is null)
            {
                _lastError = $"OneOCR dosyaları bulunamadı (aranan yerler: {Path.Combine(AppContext.BaseDirectory, "oneocr")} veya {LocalCustomDirectory}).";
                _lastFailedInitUtc = DateTime.UtcNow;
                errorMessage = _lastError;
                return false;
            }

            if (!EnsureRuntimeLibrariesLoaded(dir, out string? loadError))
            {
                _lastError = loadError;
                _lastFailedInitUtc = DateTime.UtcNow;
                errorMessage = _lastError;
                return false;
            }

            string previousDir = Environment.CurrentDirectory;
            bool switchDir = !string.Equals(
                Path.GetFullPath(previousDir).TrimEnd('\\'),
                Path.GetFullPath(dir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            try
            {
                long res = NativeMethods.CreateOcrInitOptions(out _context);
                if (res != 0)
                {
                    _lastError = $"OneOCR CreateOcrInitOptions başarısız (kod: {res}).";
                    _lastFailedInitUtc = DateTime.UtcNow;
                    errorMessage = _lastError;
                    return false;
                }

                res = NativeMethods.OcrInitOptionsSetUseModelDelayLoad(_context, 0);
                if (res != 0)
                {
                    _lastError = $"OneOCR OcrInitOptionsSetUseModelDelayLoad başarısız (kod: {res}).";
                    _lastFailedInitUtc = DateTime.UtcNow;
                    errorMessage = _lastError;
                    return false;
                }

                if (switchDir)
                    Environment.CurrentDirectory = dir;

                res = NativeMethods.CreateOcrPipeline("oneocr.onemodel", OcrPipelineKey, _context, out _pipeline);
                if (res != 0)
                {
                    _lastError = $"OneOCR CreateOcrPipeline başarısız (kod: {res}).";
                    _lastFailedInitUtc = DateTime.UtcNow;
                    errorMessage = _lastError;
                    return false;
                }

                res = NativeMethods.CreateOcrProcessOptions(out _processOptions);
                if (res != 0)
                {
                    _lastError = $"OneOCR CreateOcrProcessOptions başarısız (kod: {res}).";
                    _lastFailedInitUtc = DateTime.UtcNow;
                    errorMessage = _lastError;
                    return false;
                }

                res = NativeMethods.OcrProcessOptionsSetMaxRecognitionLineCount(_processOptions, MaxRecognitionLineCount);
                if (res != 0)
                {
                    _lastError = $"OneOCR OcrProcessOptionsSetMaxRecognitionLineCount başarısız (kod: {res}).";
                    _lastFailedInitUtc = DateTime.UtcNow;
                    errorMessage = _lastError;
                    return false;
                }

                _runtimeDirectory = dir;
                _lastError = null;
                _isInitialized = true;
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"OneOCR başlatma hatası: {ex.Message}";
                _lastFailedInitUtc = DateTime.UtcNow;
                errorMessage = _lastError;
                return false;
            }
            finally
            {
                if (switchDir)
                {
                    try { Environment.CurrentDirectory = previousDir; } catch { }
                }
            }
        }
    }

    public unsafe IReadOnlyList<OcrWordElement> RecognizeWords(byte[] bgraPixels, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (width <= 0 || height <= 0 || bgraPixels.Length < width * height * 4)
            return [];

        if (!EnsureInitialized(out string? initError))
            throw new InvalidOperationException(initError ?? "OneOCR başlatılamadı.");

        var handle = GCHandle.Alloc(bgraPixels, GCHandleType.Pinned);
        try
        {
            var img = new Img
            {
                t = 3, // BGRA32
                col = width,
                row = height,
                _unk = 0,
                step = width * 4L,
                data_ptr = handle.AddrOfPinnedObject()
            };

            lock (_runLock)
            {
                long instance = 0;
                try
                {
                    long res = NativeMethods.RunOcrPipeline(_pipeline, ref img, _processOptions, out instance);
                    if (res != 0 || instance == 0)
                        return [];

                    if (NativeMethods.GetOcrLineCount(instance, out long lineCount) != 0 || lineCount <= 0)
                        return [];

                    var words = new List<OcrWordElement>((int)Math.Min(lineCount * 6, 512));

                    for (long i = 0; i < lineCount; i++)
                    {
                        if (NativeMethods.GetOcrLine(instance, i, out long line) != 0 || line == 0)
                            continue;

                        long wordCount = 0;
                        bool hasWords = NativeMethods.GetOcrLineWordCount(line, out wordCount) == 0 && wordCount > 0;

                        if (!hasWords)
                        {
                            if (NativeMethods.GetOcrLineContent(line, out IntPtr lineTextPtr) == 0
                                && NativeMethods.GetOcrLineBoundingBox(line, out IntPtr lineBoxPtr) == 0
                                && lineBoxPtr != IntPtr.Zero)
                            {
                                string? lineText = Marshal.PtrToStringUTF8(lineTextPtr)?.Trim();
                                if (!string.IsNullOrEmpty(lineText))
                                {
                                    var box = *(NativeBoundingBox*)lineBoxPtr;
                                    words.Add(new OcrWordElement(lineText, ToBoundingBox(box), 1.0));
                                }
                            }
                            continue;
                        }

                        for (long j = 0; j < wordCount; j++)
                        {
                            if (NativeMethods.GetOcrWord(line, j, out long word) != 0 || word == 0)
                                continue;

                            if (NativeMethods.GetOcrWordContent(word, out IntPtr wordTextPtr) != 0)
                                continue;

                            string? wordText = Marshal.PtrToStringUTF8(wordTextPtr)?.Trim();
                            if (string.IsNullOrEmpty(wordText))
                                continue;

                            OcrBoundingBox bounds;
                            if (NativeMethods.GetOcrWordBoundingBox(word, out IntPtr wordBoxPtr) == 0
                                && wordBoxPtr != IntPtr.Zero)
                            {
                                bounds = ToBoundingBox(*(NativeBoundingBox*)wordBoxPtr);
                            }
                            else if (NativeMethods.GetOcrLineBoundingBox(line, out IntPtr lineBoxPtr) == 0
                                && lineBoxPtr != IntPtr.Zero)
                            {
                                bounds = ToBoundingBox(*(NativeBoundingBox*)lineBoxPtr);
                            }
                            else
                            {
                                continue;
                            }

                            double confidence = 1.0;
                            if (NativeMethods.GetOcrWordConfidence(word, out float rawConf) == 0 && !float.IsNaN(rawConf))
                            {
                                confidence = Math.Clamp(rawConf, 0.0, 1.0);
                            }

                            words.Add(new OcrWordElement(wordText, bounds, confidence));
                        }
                    }

                    return words;
                }
                finally
                {
                    if (instance != 0)
                        NativeMethods.ReleaseOcrResult(instance);
                }
            }
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    public string RecognizeText(
        byte[] bgraPixels,
        int width,
        int height,
        double minConfidence,
        string sourceLanguageCode)
    {
        var words = RecognizeWords(bgraPixels, width, height);
        if (words.Count == 0)
            return string.Empty;

        return OcrBlockGrouper.GroupAndFormat(
            words,
            new OcrGroupingOptions(
                MinWordConfidence: minConfidence,
                SourceLanguageCode: sourceLanguageCode,
                AutoDetectVerticalText: true));
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static bool HasReadableOneOcrFiles(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;

        string dll = Path.Combine(dir, "oneocr.dll");
        string model = Path.Combine(dir, "oneocr.onemodel");
        string ort = Path.Combine(dir, "onnxruntime.dll");
        if (!File.Exists(dll) || !File.Exists(model) || !File.Exists(ort))
            return false;

        try
        {
            using var _ = File.OpenRead(model);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureRuntimeLibrariesLoaded(string dir, out string? errorMessage)
    {
        lock (ResolverLock)
        {
            s_resolvedRuntimeDir = dir;

            if (!s_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(typeof(OneOcrNativeEngine).Assembly, ResolveOneOcrDll);
                s_resolverRegistered = true;
            }

            if (s_loadedOnnxRuntimeHandle == IntPtr.Zero)
            {
                string ortPath = Path.Combine(dir, "onnxruntime.dll");
                if (!NativeLibrary.TryLoad(ortPath, out s_loadedOnnxRuntimeHandle))
                {
                    errorMessage = $"onnxruntime.dll yüklenemedi: {ortPath}";
                    return false;
                }
            }

            if (s_loadedOneOcrHandle == IntPtr.Zero)
            {
                string oneOcrPath = Path.Combine(dir, "oneocr.dll");
                if (!NativeLibrary.TryLoad(oneOcrPath, out s_loadedOneOcrHandle))
                {
                    errorMessage = $"oneocr.dll yüklenemedi: {oneOcrPath}";
                    return false;
                }
            }

            errorMessage = null;
            return true;
        }
    }

    private static IntPtr ResolveOneOcrDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? dir = Volatile.Read(ref s_resolvedRuntimeDir);
        if (dir is null)
            return IntPtr.Zero;

        if (string.Equals(libraryName, "oneocr.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(libraryName, "oneocr", StringComparison.OrdinalIgnoreCase))
        {
            if (s_loadedOneOcrHandle != IntPtr.Zero)
                return s_loadedOneOcrHandle;

            string path = Path.Combine(dir, "oneocr.dll");
            return NativeLibrary.TryLoad(path, out IntPtr handle) ? handle : IntPtr.Zero;
        }

        if (string.Equals(libraryName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(libraryName, "onnxruntime", StringComparison.OrdinalIgnoreCase))
        {
            if (s_loadedOnnxRuntimeHandle != IntPtr.Zero)
                return s_loadedOnnxRuntimeHandle;

            string path = Path.Combine(dir, "onnxruntime.dll");
            return NativeLibrary.TryLoad(path, out IntPtr handle) ? handle : IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private static OcrBoundingBox ToBoundingBox(in NativeBoundingBox b)
    {
        double minX = Math.Min(Math.Min(b.x1, b.x2), Math.Min(b.x3, b.x4));
        double maxX = Math.Max(Math.Max(b.x1, b.x2), Math.Max(b.x3, b.x4));
        double minY = Math.Min(Math.Min(b.y1, b.y2), Math.Min(b.y3, b.y4));
        double maxY = Math.Max(Math.Max(b.y1, b.y2), Math.Max(b.y3, b.y4));
        return new OcrBoundingBox(minX, minY, Math.Max(1.0, maxX - minX), Math.Max(1.0, maxY - minY));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Img
    {
        public int t;
        public int col;
        public int row;
        public int _unk;
        public long step;
        public IntPtr data_ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBoundingBox
    {
        public float x1;
        public float y1;
        public float x2;
        public float y2;
        public float x3;
        public float y3;
        public float x4;
        public float y4;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrInitOptions(out long ctx);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long OcrInitOptionsSetUseModelDelayLoad(long ctx, byte flag);

        [LibraryImport("oneocr.dll", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrPipeline(string modelPath, string key, long ctx, out long pipeline);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrProcessOptions(out long opt);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long OcrProcessOptionsSetMaxRecognitionLineCount(long opt, long count);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long RunOcrPipeline(long pipeline, ref Img img, long opt, out long instance);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineCount(long instance, out long count);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLine(long instance, long index, out long line);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineContent(long line, out IntPtr content);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineBoundingBox(long line, out IntPtr boundingBoxPtr);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineWordCount(long line, out long count);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrWord(long line, long index, out long word);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrWordContent(long word, out IntPtr content);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrWordBoundingBox(long word, out IntPtr boundingBoxPtr);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrWordConfidence(long word, out float confidence);

        [LibraryImport("oneocr.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void ReleaseOcrResult(long instance);
    }
}
