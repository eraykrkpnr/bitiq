using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Bitiq.Core.Translation;

/// <summary>
/// Resmi olmayan, anahtarsız Google Translate gtx uç noktası istemcisi.
/// Tek HttpClient, keep-alive, açılışta ısınma isteği ve 3 sn zaman aşımı kullanır.
/// </summary>
public sealed class GoogleGtxTranslator : ITranslator
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    };

    private static readonly HttpClient SharedHttpClient = new(SharedHandler)
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly TranslationLruCache _cache;
    private volatile bool _isReady = true;
    private volatile string _statusText = "Hazır (Google Translate)";

    static GoogleGtxTranslator()
    {
        SharedHttpClient.DefaultRequestHeaders.ConnectionClose = false;
        SharedHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
    }

    public GoogleGtxTranslator(TranslationLruCache cache)
    {
        _cache = cache;
    }

    public string EngineName => "Google Translate";
    public bool IsReady => _isReady;
    public string StatusText => _statusText;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _statusText = "Google Translate bağlantısı ısıtılıyor…";
            using var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            warmupCts.CancelAfter(TimeSpan.FromSeconds(3));
            await SendRequestAsync("Hello", "en", "tr", warmupCts.Token).ConfigureAwait(false);
            _isReady = true;
            _statusText = "Hazır (Google Translate)";
        }
        catch
        {
            // Çevrimdışı veya geçici ağ hatasında uygulama çökmesin, ilk çeviride tekrar denensin
            _isReady = true;
            _statusText = "Hazır (Google Translate — ısınma yanıt vermedi, çeviride yeniden denenecek)";
        }
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

        if (_cache.TryGet(sourceLang, targetLang, text, out var cached))
        {
            onProgress?.Invoke(new TranslationProgress(cached, true, 0, 0, true));
            return new TranslationResult(cached, 0, 0, true);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            string translated = await SendRequestAsync(text, sourceLang, targetLang, timeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            double totalMs = sw.Elapsed.TotalMilliseconds;

            if (!string.IsNullOrWhiteSpace(translated))
            {
                _cache.Set(sourceLang, targetLang, text, translated);
            }

            _statusText = "Çalışıyor (Google Translate)";
            onProgress?.Invoke(new TranslationProgress(translated, true, totalMs, totalMs, false));
            return new TranslationResult(translated, totalMs, totalMs, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            return new TranslationResult(string.Empty, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds, false, WasCancelled: true);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            const string msg = "Google Translate yanıt vermedi (3 sn zaman aşımı). İnternet bağlantınızı kontrol edin.";
            _statusText = msg;
            return new TranslationResult(string.Empty, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds, false, ErrorMessage: msg);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            string msg = ex.StatusCode == HttpStatusCode.TooManyRequests
                ? "Google Translate geçici hız sınırına takıldı (429). Kısa süre bekleyin veya Yerel modele geçin."
                : "Google Translate'e ulaşılamadı. İnternet bağlantınızı kontrol edin.";
            _statusText = msg;
            return new TranslationResult(string.Empty, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds, false, ErrorMessage: msg);
        }
        catch (Exception)
        {
            sw.Stop();
            const string msg = "Google Translate yanıtı okunamadı. Bağlantı geçici olarak bozulmuş olabilir.";
            _statusText = msg;
            return new TranslationResult(string.Empty, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds, false, ErrorMessage: msg);
        }
    }

    private static async Task<string> SendRequestAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct)
    {
        string url =
            $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={Uri.EscapeDataString(sourceLang)}&tl={Uri.EscapeDataString(targetLang)}&dt=t&q={Uri.EscapeDataString(text)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SharedHttpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseGtxResponse(doc.RootElement);
    }

    public static string ParseGtxResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return string.Empty;

        var sentences = root[0];
        if (sentences.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var item in sentences.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0)
            {
                var part = item[0].GetString();
                if (!string.IsNullOrEmpty(part))
                    sb.Append(part);
            }
        }

        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        // SharedHttpClient ömür boyu keep-alive havuzu için paylaşılır, burada dispose edilmez.
    }
}
