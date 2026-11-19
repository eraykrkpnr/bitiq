using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Bitiq.Core.Models;
using Bitiq.Core.Ocr;

namespace Bitiq.Core.Translation;

/// <summary>
/// llama-server.exe /completion uç noktası üzerinden yerel model (Hy-MT2 7B Q6_K veya TranslateGemma 4B) ile
/// düşük gecikmeli, bağlam ve ön ek KV önbellek destekli akışlı çeviri yapar.
/// </summary>
public sealed class LlamaCppTranslator : ITranslator
{
    private static readonly SocketsHttpHandler Handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 4
    };

    private static readonly HttpClient Http = new(Handler)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly LlamaServerManager _serverManager;
    private readonly TranslationLruCache _cache;
    private readonly Func<LocalModelKind> _modelSelector;
    private readonly Func<ModelQuantization> _quantSelector;
    private readonly Func<LocalBackendKind> _backendSelector;
    private readonly Func<int> _portSelector;
    private readonly Func<int> _threadsSelector;
    private readonly Func<GameProfile?>? _warmupProfileProvider;
    private readonly Func<bool>? _simulateVramSelector;
    private readonly Func<double>? _simulatedVramGbSelector;

    private ILocalModelProfile _activeProfile = HyMt2ModelProfile.Instance;
    private bool _includeBosInPrompt = true;
    private volatile bool _isReady;
    private volatile string _statusText = "Model hazır değil";

    public LlamaCppTranslator(
        LlamaServerManager serverManager,
        TranslationLruCache cache,
        Func<ModelQuantization> quantSelector,
        Func<LocalBackendKind> backendSelector,
        Func<int> portSelector,
        Func<int> threadsSelector)
        : this(
            serverManager,
            cache,
            () => quantSelector() == ModelQuantization.Q6_K ? LocalModelKind.HyMt2_7B : LocalModelKind.TranslateGemma4B,
            quantSelector,
            backendSelector,
            portSelector,
            threadsSelector,
            null)
    {
    }

    public LlamaCppTranslator(
        LlamaServerManager serverManager,
        TranslationLruCache cache,
        Func<LocalModelKind> modelSelector,
        Func<ModelQuantization> quantSelector,
        Func<LocalBackendKind> backendSelector,
        Func<int> portSelector,
        Func<int> threadsSelector,
        Func<GameProfile?>? warmupProfileProvider = null,
        Func<bool>? simulateVramSelector = null,
        Func<double>? simulatedVramGbSelector = null)
    {
        _serverManager = serverManager;
        _cache = cache;
        _modelSelector = modelSelector;
        _quantSelector = quantSelector;
        _backendSelector = backendSelector;
        _portSelector = portSelector;
        _threadsSelector = threadsSelector;
        _warmupProfileProvider = warmupProfileProvider;
        _simulateVramSelector = simulateVramSelector;
        _simulatedVramGbSelector = simulatedVramGbSelector;

        _serverManager.StatusChanged += msg => _statusText = msg;
    }

    public string EngineName => _activeProfile.EngineName;
    public bool IsReady => _isReady && _serverManager.IsRunning;
    public string StatusText => _statusText;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _isReady = false;
        _statusText = "Model yükleniyor…";

        LocalModelKind model = _modelSelector();
        ModelQuantization quant = ModelCatalog.ResolveQuantization(model, _quantSelector());
        ILocalModelProfile profile = model == LocalModelKind.HyMt2_7B
            ? HyMt2ModelProfile.Instance
            : TranslateGemmaModelProfile.Instance;
        _activeProfile = profile;

        await _serverManager.StartAsync(
            model,
            quant,
            _backendSelector(),
            _portSelector(),
            _threadsSelector(),
            ct,
            _simulateVramSelector?.Invoke() ?? false,
            _simulatedVramGbSelector?.Invoke() ?? 10.0).ConfigureAwait(false);

        // GGUF BOS çiftleme davranışını kontrol et
        _includeBosInPrompt = await ShouldIncludeBosTokenAsync(profile, ct).ConfigureAwait(false);

        // Açılışta prefix cache ısınma isteği (sabit talimat ve aktif oyun profili ön ekini KV önbelleğe alır)
        _statusText = "Model ısıtılıyor (ön ek önbelleği)…";
        TranslationContextSnapshot? warmupCtx = null;
        if (profile.SupportsContext)
        {
            var gameProfile = _warmupProfileProvider?.Invoke();
            if (gameProfile is { IsEffectivelyEmpty: false })
                warmupCtx = new TranslationContextSnapshot(gameProfile.Clone(), []);
        }

        await ExecuteCompletionAsync(
            profile,
            "Ready.",
            "en",
            "tr",
            warmupCtx,
            stream: false,
            onProgress: null,
            ct).ConfigureAwait(false);

        _isReady = true;
        _statusText = $"Hazır ({profile.EngineName} • {_serverManager.ActiveBackendKey})";
    }

    /// <summary>
    /// llama-server /tokenize çıktısını kontrol ederek model GGUF'unun add_bos_token=true durumunda çift BOS eklenmesini önler.
    /// </summary>
    private async Task<bool> ShouldIncludeBosTokenAsync(ILocalModelProfile profile, CancellationToken ct)
    {
        try
        {
            string baseUrl = _serverManager.BaseUrl;
            var payload = JsonSerializer.Serialize(new { content = profile.BosProbeText, add_special = true });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/tokenize")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return true;

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("tokens", out var tokens) && tokens.GetArrayLength() >= 2)
            {
                int t0 = tokens[0].GetInt32();
                int t1 = tokens[1].GetInt32();
                if (t0 == profile.BosTokenId && t1 == profile.BosTokenId)
                    return false;
            }
        }
        catch { }

        return true;
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLang,
        string targetLang,
        bool stream,
        Action<TranslationProgress>? onProgress,
        CancellationToken ct,
        TranslationContextSnapshot? context = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult(string.Empty, 0, 0, false);

        ILocalModelProfile profile = _activeProfile;
        TranslationContextSnapshot? effectiveContext = profile.SupportsContext ? context : null;
        string? cacheFingerprint = TranslationLruCache.ResolveFingerprint(effectiveContext, text);

        if (_cache.TryGet(sourceLang, targetLang, text, out var cached, cacheFingerprint))
        {
            onProgress?.Invoke(new TranslationProgress(cached, true, 0, 0, true));
            return new TranslationResult(cached, 0, 0, true);
        }

        if (!_serverManager.IsRunning)
        {
            return new TranslationResult(
                string.Empty,
                0,
                0,
                false,
                ErrorMessage: "Yerel çeviri sunucusu çalışmıyor.");
        }

        var result = await ExecuteCompletionAsync(
            profile,
            text,
            sourceLang,
            targetLang,
            effectiveContext,
            stream,
            onProgress,
            ct).ConfigureAwait(false);

        if (result.WasCancelled || !string.IsNullOrEmpty(result.ErrorMessage))
            return result;

        // Çıktı koruması: bağlamlı istekte boş çıktı veya bağlam kopyası oluşursa bağlamsız tek seferlik tekrar dene
        if (effectiveContext is { IsEmpty: false }
            && (string.IsNullOrWhiteSpace(result.TranslatedText)
                || IsContextCopyHallucination(text, result.TranslatedText, effectiveContext.History)))
        {
            var fallback = await ExecuteCompletionAsync(
                profile,
                text,
                sourceLang,
                targetLang,
                context: null,
                stream: false,
                onProgress: null,
                ct).ConfigureAwait(false);

            if (!fallback.WasCancelled && string.IsNullOrEmpty(fallback.ErrorMessage) && !string.IsNullOrWhiteSpace(fallback.TranslatedText))
            {
                _cache.Set(sourceLang, targetLang, text, fallback.TranslatedText, cacheFingerprint);
                onProgress?.Invoke(new TranslationProgress(fallback.TranslatedText, true, fallback.FirstTokenMs, fallback.TotalMs, false));
                return fallback;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.TranslatedText))
        {
            _cache.Set(sourceLang, targetLang, text, result.TranslatedText, cacheFingerprint);
            onProgress?.Invoke(new TranslationProgress(result.TranslatedText, true, result.FirstTokenMs, result.TotalMs, false));
        }

        return result;
    }

    private async Task<TranslationResult> ExecuteCompletionAsync(
        ILocalModelProfile profile,
        string text,
        string sourceLang,
        string targetLang,
        TranslationContextSnapshot? context,
        bool stream,
        Action<TranslationProgress>? onProgress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double firstTokenMs = -1;
        string prompt = profile.BuildPrompt(text, sourceLang, targetLang, context, _includeBosInPrompt);
        int nPredict = profile.ComputeMaxTokens(text);
        int sourceLineCount = HyMt2PromptBuilder.CountNonEmptyLines(text);
        LocalSamplingConfig sampling = profile.Sampling;

        var requestBody = new
        {
            prompt,
            temperature = sampling.Temperature,
            top_k = sampling.TopK,
            top_p = sampling.TopP,
            repeat_penalty = sampling.RepeatPenalty,
            n_predict = nPredict,
            cache_prompt = true,
            stream,
            stop = profile.StopTokens
        };

        string jsonPayload = JsonSerializer.Serialize(requestBody);
        string url = $"{_serverManager.BaseUrl}/completion";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            using var resp = await Http.SendAsync(
                req,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                ct).ConfigureAwait(false);

            resp.EnsureSuccessStatusCode();

            if (!stream)
            {
                string rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(rawJson);
                string rawContent = doc.RootElement.TryGetProperty("content", out var cEl)
                    ? cEl.GetString() ?? string.Empty
                    : string.Empty;

                ExtractTimings(doc.RootElement, out int promptTokens, out int cachedTokens, out double tokPerSec);

                sw.Stop();
                double totalMs = sw.Elapsed.TotalMilliseconds;
                string cleaned = profile.CleanModelOutput(rawContent, sourceLineCount);
                return new TranslationResult(
                    cleaned,
                    totalMs,
                    totalMs,
                    false,
                    PromptTokens: promptTokens,
                    CachedTokens: cachedTokens,
                    TokensPerSecond: tokPerSec);
            }

            // Akışlı (SSE) okuma — iptal gelirse Stream kapatılır ve llama-server TCP kesilmesiyle slot üretimini derhal durdurur.
            await using var respStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(respStream, Encoding.UTF8);
            var accumulated = new StringBuilder();
            int finalPromptTokens = 0;
            int finalCachedTokens = 0;
            double finalTokPerSec = 0;

            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string dataJson = line.Length > 5 ? line[5..].Trim() : string.Empty;
                if (string.IsNullOrEmpty(dataJson))
                    continue;

                using var chunkDoc = JsonDocument.Parse(dataJson);
                var root = chunkDoc.RootElement;

                if (root.TryGetProperty("content", out var contentEl))
                {
                    string tokenText = contentEl.GetString() ?? string.Empty;
                    if (tokenText.Length > 0)
                    {
                        if (firstTokenMs < 0)
                            firstTokenMs = sw.Elapsed.TotalMilliseconds;

                        accumulated.Append(tokenText);
                        string partialClean = profile.CleanModelOutput(accumulated.ToString(), sourceLineCount);
                        if (!string.IsNullOrEmpty(partialClean))
                        {
                            onProgress?.Invoke(new TranslationProgress(
                                partialClean,
                                IsFinal: false,
                                FirstTokenMs: firstTokenMs,
                                TotalMs: sw.Elapsed.TotalMilliseconds,
                                FromCache: false));
                        }
                    }
                }

                if (root.TryGetProperty("timings", out _) || root.TryGetProperty("tokens_cached", out _))
                {
                    ExtractTimings(root, out int pt, out int ctok, out double tps);
                    if (pt > 0) finalPromptTokens = pt;
                    if (ctok > 0) finalCachedTokens = ctok;
                    if (tps > 0) finalTokPerSec = tps;
                }

                if (root.TryGetProperty("stop", out var stopEl) && stopEl.ValueKind == JsonValueKind.True)
                    break;
            }

            ct.ThrowIfCancellationRequested();
            sw.Stop();

            double finalTotalMs = sw.Elapsed.TotalMilliseconds;
            if (firstTokenMs < 0)
                firstTokenMs = finalTotalMs;

            string finalClean = profile.CleanModelOutput(accumulated.ToString(), sourceLineCount);
            return new TranslationResult(
                finalClean,
                firstTokenMs,
                finalTotalMs,
                false,
                PromptTokens: finalPromptTokens,
                CachedTokens: finalCachedTokens,
                TokensPerSecond: finalTokPerSec);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            return new TranslationResult(
                string.Empty,
                firstTokenMs < 0 ? sw.Elapsed.TotalMilliseconds : firstTokenMs,
                sw.Elapsed.TotalMilliseconds,
                false,
                WasCancelled: true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            string msg = $"Yerel çeviri hatası: {ex.Message}";
            _statusText = msg;
            return new TranslationResult(
                string.Empty,
                firstTokenMs < 0 ? sw.Elapsed.TotalMilliseconds : firstTokenMs,
                sw.Elapsed.TotalMilliseconds,
                false,
                ErrorMessage: msg);
        }
    }

    private static bool IsContextCopyHallucination(
        string currentSource,
        string outputText,
        IReadOnlyList<ContextLine> history)
    {
        if (history.Count == 0)
            return false;

        string outNorm = SubtitleStabilizer.NormalizeForComparison(outputText);
        string srcNorm = SubtitleStabilizer.NormalizeForComparison(currentSource);
        if (string.IsNullOrEmpty(outNorm) || string.IsNullOrEmpty(srcNorm))
            return false;

        foreach (var line in history)
        {
            string histTgtNorm = SubtitleStabilizer.NormalizeForComparison(line.Target);
            string histSrcNorm = SubtitleStabilizer.NormalizeForComparison(line.Source);
            if (!string.IsNullOrEmpty(histTgtNorm)
                && string.Equals(outNorm, histTgtNorm, StringComparison.Ordinal)
                && SubtitleStabilizer.ComputeSimilarity(srcNorm, histSrcNorm) < 0.70)
            {
                return true;
            }
        }

        return false;
    }

    private static void ExtractTimings(
        JsonElement root,
        out int promptTokens,
        out int cachedTokens,
        out double tokensPerSecond)
    {
        promptTokens = 0;
        cachedTokens = 0;
        tokensPerSecond = 0;

        if (root.TryGetProperty("tokens_cached", out var tcEl) && tcEl.TryGetInt32(out int tc))
            cachedTokens = tc;

        if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
        {
            if (timings.TryGetProperty("prompt_n", out var pnEl) && pnEl.TryGetInt32(out int pn))
                promptTokens = pn;
            if (cachedTokens == 0 && timings.TryGetProperty("cache_n", out var cnEl) && cnEl.TryGetInt32(out int cn))
                cachedTokens = cn;
            if (timings.TryGetProperty("predicted_per_second", out var tpsEl) && tpsEl.TryGetDouble(out double tps))
                tokensPerSecond = Math.Round(tps, 1);
        }
    }

    public async Task<bool> IsServerIdleAsync(CancellationToken ct = default)
    {
        if (!_serverManager.IsRunning)
            return true;

        try
        {
            using var resp = await Http.GetAsync($"{_serverManager.BaseUrl}/slots", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var slot in doc.RootElement.EnumerateArray())
            {
                if (slot.TryGetProperty("is_processing", out var procEl) && procEl.GetBoolean())
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        // LlamaServerManager yaşam döngüsü dışarıdan yönetilir (offload için).
    }
}
