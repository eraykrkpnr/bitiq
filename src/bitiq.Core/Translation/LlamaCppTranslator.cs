using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Bitiq.Core.Models;

namespace Bitiq.Core.Translation;

/// <summary>
/// llama-server.exe /completion uç noktası üzerinden TranslateGemma 4B ile düşük gecikmeli akışlı çeviri yapar.
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
    private readonly Func<ModelQuantization> _quantSelector;
    private readonly Func<LocalBackendKind> _backendSelector;
    private readonly Func<int> _portSelector;
    private readonly Func<int> _threadsSelector;

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
    {
        _serverManager = serverManager;
        _cache = cache;
        _quantSelector = quantSelector;
        _backendSelector = backendSelector;
        _portSelector = portSelector;
        _threadsSelector = threadsSelector;

        _serverManager.StatusChanged += msg => _statusText = msg;
    }

    public string EngineName => "Yerel (TranslateGemma 4B)";
    public bool IsReady => _isReady && _serverManager.IsRunning;
    public string StatusText => _statusText;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _isReady = false;
        _statusText = "Model yükleniyor…";

        await _serverManager.StartAsync(
            _quantSelector(),
            _backendSelector(),
            _portSelector(),
            _threadsSelector(),
            ct).ConfigureAwait(false);

        // GGUF BOS çiftleme davranışını kontrol et
        _includeBosInPrompt = await ShouldIncludeBosTokenAsync(ct).ConfigureAwait(false);

        // Açılışta prefix cache ısınma isteği (sabit talimat ön ekini KV önbelleğe alır)
        _statusText = "Model ısıtılıyor (ön ek önbelleği)…";
        await TranslateAsync("Ready.", "en", "tr", stream: false, onProgress: null, ct).ConfigureAwait(false);
        // "Ready." ısınma çevirisini LRU'da tutmakta sakınca yok hatta faydalı, ancak hazır durumunu koru
        _isReady = true;
        _statusText = $"Hazır (Yerel • {_serverManager.ActiveBackendKey})";
    }

    /// <summary>
    /// llama-server /tokenize çıktısını kontrol ederek model GGUF'unun add_bos_token=true durumunda çift BOS eklenmesini önler.
    /// </summary>
    private async Task<bool> ShouldIncludeBosTokenAsync(CancellationToken ct)
    {
        try
        {
            string baseUrl = _serverManager.BaseUrl;
            var payload = JsonSerializer.Serialize(new { content = "<bos>Hello", add_special = true });
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
                // Gemma BOS token ID = 2. İlk iki token [2, 2] ise sunucu zaten otomatik BOS ekliyor demektir.
                int t0 = tokens[0].GetInt32();
                int t1 = tokens[1].GetInt32();
                if (t0 == 2 && t1 == 2)
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
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult(string.Empty, 0, 0, false);

        if (_cache.TryGet(sourceLang, targetLang, text, out var cached))
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

        var sw = Stopwatch.StartNew();
        double firstTokenMs = -1;
        string prompt = TranslateGemmaPromptBuilder.BuildPrompt(text, sourceLang, targetLang, _includeBosInPrompt);
        int nPredict = TranslateGemmaPromptBuilder.ComputeMaxTokens(text);

        var requestBody = new
        {
            prompt,
            temperature = 0.0,
            top_k = 1,
            top_p = 1.0,
            n_predict = nPredict,
            cache_prompt = true,
            stream,
            stop = new[] { "<end_of_turn>", "<eos>" }
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

                sw.Stop();
                double totalMs = sw.Elapsed.TotalMilliseconds;
                string cleaned = TranslateGemmaPromptBuilder.CleanModelOutput(rawContent);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    _cache.Set(sourceLang, targetLang, text, cleaned);

                onProgress?.Invoke(new TranslationProgress(cleaned, true, totalMs, totalMs, false));
                return new TranslationResult(cleaned, totalMs, totalMs, false);
            }

            // Akışlı (SSE) okuma — iptal gelirse Stream kapatılır ve llama-server TCP kesilmesiyle slot üretimini derhal durdurur.
            await using var respStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(respStream, Encoding.UTF8);
            var accumulated = new StringBuilder();

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
                        string partialClean = TranslateGemmaPromptBuilder.CleanModelOutput(accumulated.ToString());
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

                if (root.TryGetProperty("stop", out var stopEl) && stopEl.ValueKind == JsonValueKind.True)
                    break;
            }

            ct.ThrowIfCancellationRequested();
            sw.Stop();

            double finalTotalMs = sw.Elapsed.TotalMilliseconds;
            if (firstTokenMs < 0)
                firstTokenMs = finalTotalMs;

            string finalClean = TranslateGemmaPromptBuilder.CleanModelOutput(accumulated.ToString());
            if (!string.IsNullOrWhiteSpace(finalClean))
                _cache.Set(sourceLang, targetLang, text, finalClean);

            onProgress?.Invoke(new TranslationProgress(finalClean, true, firstTokenMs, finalTotalMs, false));
            return new TranslationResult(finalClean, firstTokenMs, finalTotalMs, false);
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
