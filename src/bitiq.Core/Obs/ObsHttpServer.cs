using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bitiq.Core.Models;
using Bitiq.Core.Styling;

namespace Bitiq.Core.Obs;

/// <summary>
/// Yalnızca 127.0.0.1 üzerinde çalışan, yönetici izni gerektirmeyen hafif OBS Tarayıcı Kaynağı HTTP + SSE sunucusu.
/// </summary>
public sealed class ObsHttpServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ConcurrentDictionary<Guid, StreamWriter> _sseClients = new();
    private readonly CancellationTokenSource _cts = new();

    private HttpListener? _listener;
    private StyleProfile _currentStyle;
    private SubtitleUpdateEvent _currentSubtitle = new() { IsCleared = true };
    private bool _disposed;

    public int Port { get; private set; }
    public string Url => Port > 0 ? $"http://127.0.0.1:{Port}/" : string.Empty;
    public bool IsRunning => _listener is { IsListening: true };
    public string? LastError { get; private set; }

    public ObsHttpServer(StyleProfile initialStyle)
    {
        _currentStyle = initialStyle.Clone();
    }

    public void Start(int preferredPort = 19555)
    {
        Stop();

        int port = FindAvailablePort(preferredPort);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            // Çakışma olursa işletim sisteminin verdiği boş portu dene
            listener.Close();
            port = FindAvailablePort(0);
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
        }

        _listener = listener;
        Port = port;
        LastError = null;

        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        foreach (var kv in _sseClients)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        _sseClients.Clear();

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;
    }

    public void PublishSubtitle(SubtitleUpdateEvent subtitle)
    {
        _currentSubtitle = subtitle;
        string json = JsonSerializer.Serialize(new
        {
            sequenceId = subtitle.SequenceId,
            englishText = subtitle.EnglishText,
            turkishText = subtitle.TurkishText,
            isPartial = subtitle.IsPartial,
            isCleared = subtitle.IsCleared
        }, JsonOptions);
        BroadcastSseEvent("subtitle", json);
    }

    public void PublishStyle(StyleProfile style)
    {
        _currentStyle = style.Clone();
        string json = StyleCssMapper.SerializeStylePayload(_currentStyle);
        BroadcastSseEvent("style", json);
    }

    private void BroadcastSseEvent(string eventName, string jsonData)
    {
        if (_sseClients.IsEmpty)
            return;

        string frame = $"event: {eventName}\ndata: {jsonData}\n\n";
        foreach (var kv in _sseClients)
        {
            try
            {
                lock (kv.Value)
                {
                    kv.Value.Write(frame);
                    kv.Value.Flush();
                }
            }
            catch
            {
                if (_sseClients.TryRemove(kv.Key, out var writer))
                {
                    try { writer.Dispose(); } catch { }
                }
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (_sseClients.IsEmpty)
                    continue;

                foreach (var kv in _sseClients)
                {
                    try
                    {
                        lock (kv.Value)
                        {
                            kv.Value.Write(": ping\n\n");
                            kv.Value.Flush();
                        }
                    }
                    catch
                    {
                        if (_sseClients.TryRemove(kv.Key, out var writer))
                        {
                            try { writer.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx, ct), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path == "/events")
            {
                await HandleSseClientAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/health")
            {
                await WriteTextAsync(ctx.Response, 200, "application/json", "{\"ok\":true}").ConfigureAwait(false);
                return;
            }

            if (path == "/state")
            {
                string stateJson = JsonSerializer.Serialize(new
                {
                    style = StyleCssMapper.Map(_currentStyle),
                    subtitle = new
                    {
                        sequenceId = _currentSubtitle.SequenceId,
                        englishText = _currentSubtitle.EnglishText,
                        turkishText = _currentSubtitle.TurkishText,
                        isPartial = _currentSubtitle.IsPartial,
                        isCleared = _currentSubtitle.IsCleared
                    }
                }, JsonOptions);
                await WriteTextAsync(ctx.Response, 200, "application/json", stateJson).ConfigureAwait(false);
                return;
            }

            // Ana OBS Tarayıcı Kaynağı HTML sayfası
            await WriteTextAsync(ctx.Response, 200, "text/html; charset=utf-8", BuildObsHtmlPage()).ConfigureAwait(false);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private async Task HandleSseClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var resp = ctx.Response;
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["Connection"] = "keep-alive";
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.SendChunked = true;

        var id = Guid.NewGuid();
        var writer = new StreamWriter(resp.OutputStream, new UTF8Encoding(false), leaveOpen: false)
        {
            AutoFlush = true
        };

        try
        {
            string styleJson = StyleCssMapper.SerializeStylePayload(_currentStyle);
            string subJson = JsonSerializer.Serialize(new
            {
                sequenceId = _currentSubtitle.SequenceId,
                englishText = _currentSubtitle.EnglishText,
                turkishText = _currentSubtitle.TurkishText,
                isPartial = _currentSubtitle.IsPartial,
                isCleared = _currentSubtitle.IsCleared
            }, JsonOptions);

            lock (writer)
            {
                writer.Write($"retry: 1000\nevent: style\ndata: {styleJson}\n\n");
                writer.Write($"event: subtitle\ndata: {subJson}\n\n");
                writer.Flush();
            }

            _sseClients[id] = writer;

            // Bağlantı açık kaldığı sürece iptali bekle
            while (!ct.IsCancellationRequested && _sseClients.ContainsKey(id))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            _sseClients.TryRemove(id, out _);
            try { writer.Dispose(); } catch { }
        }
    }

    private static async Task WriteTextAsync(HttpListenerResponse resp, int statusCode, string contentType, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        resp.StatusCode = statusCode;
        resp.ContentType = contentType;
        resp.Headers["Cache-Control"] = "no-store";
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        resp.Close();
    }

    private static int FindAvailablePort(int preferredPort)
    {
        if (preferredPort > 0)
        {
            try
            {
                using var tcp = new TcpListener(IPAddress.Loopback, preferredPort);
                tcp.Start();
                return preferredPort;
            }
            catch { }
        }

        using var dynamicTcp = new TcpListener(IPAddress.Loopback, 0);
        dynamicTcp.Start();
        return ((IPEndPoint)dynamicTcp.LocalEndpoint).Port;
    }

    public static string BuildObsHtmlPage() => """
<!DOCTYPE html>
<html lang="tr">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>bitiq OBS Overlay</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  html, body {
    width: 100vw;
    height: 100vh;
    overflow: hidden;
    background: transparent;
    background-color: rgba(0, 0, 0, 0);
    -webkit-font-smoothing: antialiased;
    user-select: none;
  }
  #stage {
    position: relative;
    width: 100vw;
    height: 100vh;
    overflow: hidden;
    background: transparent;
  }
  #anchor {
    position: absolute;
    display: flex;
    flex-direction: column;
    pointer-events: none;
    will-change: opacity, transform;
  }
  #box {
    display: flex;
    flex-direction: column;
    width: fit-content;
    max-width: 100%;
  }
  #box.full-width {
    width: 100%;
  }
  #lines {
    display: flex;
    flex-direction: column;
    gap: 0.35vh;
    overflow: hidden;
  }
  .sub-line {
    display: inline-block;
    white-space: pre-wrap;
    word-break: break-word;
    paint-order: stroke fill;
  }
  #original {
    margin-top: 0.45vh;
    white-space: pre-wrap;
    word-break: break-word;
    paint-order: stroke fill;
  }
  .hidden {
    opacity: 0 !important;
  }
</style>
</head>
<body>
<div id="stage">
  <div id="anchor" class="hidden">
    <div id="box">
      <div id="lines"></div>
      <div id="original" style="display:none;"></div>
    </div>
  </div>
</div>
<script>
(function() {
  const anchor = document.getElementById('anchor');
  const box = document.getElementById('box');
  const linesEl = document.getElementById('lines');
  const origEl = document.getElementById('original');

  let currentStyle = null;
  let currentSub = { turkishText: '', englishText: '', isCleared: true };
  let isVisible = false;

  function applyPosition(s) {
    anchor.style.top = '';
    anchor.style.bottom = '';
    anchor.style.left = '';
    anchor.style.right = '';
    anchor.style.transform = '';
    anchor.style.alignItems = 'center';

    const ox = s.offsetXPercent || '0%';
    const oy = s.offsetYPercent || '0%';
    const isFullBar = s.backgroundMode === 'FullWidthBar';

    anchor.style.width = isFullBar ? '100vw' : 'auto';
    anchor.style.maxWidth = isFullBar ? '100vw' : (s.maxWidthPercent || '84%');

    const preset = s.positionPreset || 'BottomCenter';
    let baseTx = '0%', baseTy = '0%';

    if (preset === 'BottomCenter') {
      anchor.style.bottom = `calc(5% - (${oy}))`;
      anchor.style.left = isFullBar ? '0' : `calc(50% + (${ox}))`;
      baseTx = isFullBar ? '0%' : '-50%';
    } else if (preset === 'TopCenter') {
      anchor.style.top = `calc(5% + (${oy}))`;
      anchor.style.left = isFullBar ? '0' : `calc(50% + (${ox}))`;
      baseTx = isFullBar ? '0%' : '-50%';
    } else if (preset === 'Center') {
      anchor.style.top = `calc(50% + (${oy}))`;
      anchor.style.left = isFullBar ? '0' : `calc(50% + (${ox}))`;
      baseTx = isFullBar ? '0%' : '-50%';
      baseTy = '-50%';
    } else if (preset === 'BottomLeft') {
      anchor.style.bottom = `calc(5% - (${oy}))`;
      anchor.style.left = isFullBar ? '0' : `calc(4% + (${ox}))`;
      anchor.style.alignItems = 'flex-start';
    } else if (preset === 'BottomRight') {
      anchor.style.bottom = `calc(5% - (${oy}))`;
      anchor.style.right = isFullBar ? '0' : `calc(4% - (${ox}))`;
      anchor.style.alignItems = 'flex-end';
    } else if (preset === 'TopLeft') {
      anchor.style.top = `calc(5% + (${oy}))`;
      anchor.style.left = isFullBar ? '0' : `calc(4% + (${ox}))`;
      anchor.style.alignItems = 'flex-start';
    } else if (preset === 'TopRight') {
      anchor.style.top = `calc(5% + (${oy}))`;
      anchor.style.right = isFullBar ? '0' : `calc(4% - (${ox}))`;
      anchor.style.alignItems = 'flex-end';
    }

    anchor.dataset.baseTransform = `translate(${baseTx}, ${baseTy})`;
    updateVisibilityTransform();
  }

  function updateVisibilityTransform() {
    const s = currentStyle;
    if (!s) return;
    const base = anchor.dataset.baseTransform || 'translate(0,0)';
    const dur = Math.max(0, s.animationDurationMs || 0);
    const anim = s.animationType || 'None';

    if (anim === 'None' || dur === 0) {
      anchor.style.transition = 'none';
      anchor.style.opacity = isVisible ? '1' : '0';
      anchor.style.transform = base;
    } else if (anim === 'Fade') {
      anchor.style.transition = `opacity ${dur}ms ease`;
      anchor.style.opacity = isVisible ? '1' : '0';
      anchor.style.transform = base;
    } else {
      anchor.style.transition = `opacity ${dur}ms ease, transform ${dur}ms ease`;
      anchor.style.opacity = isVisible ? '1' : '0';
      anchor.style.transform = isVisible ? base : `${base} translateY(1.2vh)`;
    }
  }

  function render() {
    const s = currentStyle;
    if (!s) return;

    const hasText = !currentSub.isCleared && currentSub.turkishText && currentSub.turkishText.trim().length > 0;
    isVisible = !!hasText;

    if (!hasText) {
      anchor.classList.add('hidden');
      updateVisibilityTransform();
      return;
    }

    anchor.classList.remove('hidden');

    const isFullBar = s.backgroundMode === 'FullWidthBar';
    const isSingleBlock = s.backgroundMode === 'SingleBlock';
    const isPerLine = s.backgroundMode === 'PerLineBox';

    box.className = isFullBar ? 'full-width' : '';
    box.style.background = (isFullBar || isSingleBlock) ? s.backgroundRgba : 'transparent';
    box.style.borderRadius = isSingleBlock ? s.borderRadiusVh : '0';
    box.style.padding = (isFullBar || isSingleBlock) ? `${s.paddingVerticalVh} ${s.paddingHorizontalVh}` : '0';
    box.style.border = (isFullBar || isSingleBlock) ? s.borderCss : 'none';
    box.style.textAlign = s.textAlign || 'center';

    const alignFlex = s.textAlign === 'left' ? 'flex-start' : (s.textAlign === 'right' ? 'flex-end' : 'center');
    linesEl.style.alignItems = isFullBar ? alignFlex : (isPerLine ? alignFlex : 'stretch');

    linesEl.innerHTML = '';
    const rawLines = currentSub.turkishText.split('\n');
    const maxLines = Math.max(1, s.maxLines || 3);

    for (const lineText of rawLines) {
      const span = document.createElement('span');
      span.className = 'sub-line';
      span.textContent = lineText;
      span.style.fontFamily = s.fontFamilyCss;
      span.style.fontSize = s.fontSizeVh;
      span.style.fontWeight = String(s.fontWeight || 700);
      span.style.fontStyle = s.fontStyle || 'normal';
      span.style.letterSpacing = s.letterSpacingVh || '0';
      span.style.lineHeight = s.lineHeight || '1.25';
      span.style.textTransform = s.textTransform || 'none';
      span.style.color = s.textColorRgba;
      span.style.webkitTextStroke = (parseFloat(s.outlineWidthVh) > 0)
        ? `${s.outlineWidthVh} ${s.outlineColorHex}`
        : '0 transparent';
      span.style.textShadow = s.textShadowCss || 'none';
      span.style.textAlign = s.textAlign || 'center';

      if (isPerLine) {
        span.style.background = s.backgroundRgba;
        span.style.borderRadius = s.borderRadiusVh;
        span.style.padding = `${s.paddingVerticalVh} ${s.paddingHorizontalVh}`;
        span.style.border = s.borderCss;
      }
      linesEl.appendChild(span);
    }

    // Taşma kontrolü: Clip veya ShrinkToFit
    linesEl.style.display = 'flex';
    linesEl.style.maxHeight = '';
    if (s.overflowMode === 'Clip') {
      const baseVh = parseFloat(s.fontSizeVh) || 3.3;
      const lh = parseFloat(s.lineHeight) || 1.25;
      linesEl.style.maxHeight = `${(baseVh * lh * maxLines + 0.8).toFixed(3)}vh`;
    } else {
      // ShrinkToFit: satır sayısı maxLines'ı aşarsa veya yükseklik taşarsa yazı boyutunu küçült
      const baseVh = parseFloat(s.fontSizeVh) || 3.3;
      const lh = parseFloat(s.lineHeight) || 1.25;
      const allowedPx = (baseVh * lh * maxLines + 0.5) * window.innerHeight / 100.0;
      let scale = 1.0;
      while (linesEl.scrollHeight > allowedPx + 2 && scale > 0.55) {
        scale -= 0.08;
        for (const child of linesEl.children) {
          child.style.fontSize = `${(baseVh * scale).toFixed(3)}vh`;
        }
      }
    }

    if (s.showOriginalText && currentSub.englishText && currentSub.englishText.trim().length > 0) {
      origEl.style.display = 'block';
      origEl.textContent = currentSub.englishText;
      origEl.style.fontFamily = s.fontFamilyCss;
      origEl.style.fontSize = s.originalFontSizeVh;
      origEl.style.fontWeight = String(s.originalFontWeight || 500);
      origEl.style.fontStyle = s.originalFontStyle || 'italic';
      origEl.style.color = s.originalTextColorRgba;
      origEl.style.textAlign = s.textAlign || 'center';
      origEl.style.webkitTextStroke = (parseFloat(s.outlineWidthVh) > 0)
        ? `${(parseFloat(s.outlineWidthVh) * 0.65).toFixed(3)}vh ${s.outlineColorHex}`
        : '0 transparent';
      origEl.style.textShadow = s.textShadowCss || 'none';
    } else {
      origEl.style.display = 'none';
    }

    updateVisibilityTransform();
  }

  function applyStyle(stylePayload) {
    currentStyle = stylePayload;
    applyPosition(currentStyle);
    render();
  }

  function applySubtitle(subPayload) {
    currentSub = subPayload;
    render();
  }

  function connectSse() {
    const es = new EventSource('/events');
    es.addEventListener('style', (e) => {
      try { applyStyle(JSON.parse(e.data)); } catch {}
    });
    es.addEventListener('subtitle', (e) => {
      try { applySubtitle(JSON.parse(e.data)); } catch {}
    });
    es.onerror = () => {
      // Tarayıcı yerleşik reconnect yapar; yine de bağlantı tamamen kapandıysa tekrar aç
      if (es.readyState === EventSource.CLOSED) {
        setTimeout(connectSse, 1000);
      }
    };
  }

  fetch('/state')
    .then(r => r.json())
    .then(data => {
      if (data.style) applyStyle(data.style);
      if (data.subtitle) applySubtitle(data.subtitle);
    })
    .catch(() => {})
    .finally(connectSse);
})();
</script>
</body>
</html>
""";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        Stop();
        _cts.Dispose();
    }
}
