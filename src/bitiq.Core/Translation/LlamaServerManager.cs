using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bitiq.Core.Models;
using Bitiq.Core.Services;

namespace Bitiq.Core.Translation;

public sealed record OffloadVerificationResult(
    bool ExitedCleanly,
    long WorkingSetFreedBytes,
    bool GpuProcessGone,
    string Summary);

public sealed record GpuOffloadPlan(
    int GpuLayers,
    int TotalTransformerLayers,
    bool IsFullGpu,
    double EstimatedVramMb,
    string Summary);

public sealed class LlamaServerManager : IDisposable
{
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly ModelDownloadService _downloadService;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private Process? _process;
    private JobObject? _jobObject;
    private long _lastObservedWorkingSetBytes;
    private int _activePort;
    private string _activeBackendKey = string.Empty;
    private LocalModelKind _activeModel;
    private ModelQuantization _activeQuantization;
    private bool _activeSimulateVram;
    private double _activeSimulatedVramGb;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };
    public int? ActiveProcessId
    {
        get
        {
            try
            {
                return _process is { HasExited: false } p ? p.Id : null;
            }
            catch
            {
                return null;
            }
        }
    }
    public int ActivePort => _activePort;
    public string ActiveBackendKey => _activeBackendKey;
    public LocalModelKind ActiveModel => _activeModel;
    public GpuOffloadPlan? ActiveGpuOffloadPlan { get; private set; }
    public string BaseUrl => _activePort > 0 ? $"http://127.0.0.1:{_activePort}" : string.Empty;
    public NvidiaDriverInfo? LastDriverInfo { get; private set; }
    public string? LastVramWarning { get; private set; }
    public string LastStatusMessage { get; private set; } = "Kapalı";

    public event Action<string>? StatusChanged;
    public event Action<DownloadProgressInfo>? DownloadProgress;

    public LlamaServerManager(ModelDownloadService downloadService)
    {
        _downloadService = downloadService;
    }

    public static int FindAvailablePort(int preferredPort)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, preferredPort);
            listener.Start();
            return preferredPort;
        }
        catch
        {
            using var fallback = new TcpListener(IPAddress.Loopback, 0);
            fallback.Start();
            return ((IPEndPoint)fallback.LocalEndpoint).Port;
        }
    }

    public Task StartAsync(
        ModelQuantization quantization,
        LocalBackendKind requestedBackend,
        int preferredPort,
        int cpuThreads,
        CancellationToken ct)
    {
        var model = quantization == ModelQuantization.Q6_K
            ? LocalModelKind.HyMt2_7B
            : LocalModelKind.TranslateGemma4B;
        return StartAsync(model, quantization, requestedBackend, preferredPort, cpuThreads, ct);
    }

    public async Task StartAsync(
        LocalModelKind model,
        ModelQuantization quantization,
        LocalBackendKind requestedBackend,
        int preferredPort,
        int cpuThreads,
        CancellationToken ct,
        bool simulateVramLimit = false,
        double simulatedVramLimitGb = 10.0)
    {
        ModelQuantization resolvedQuant = ModelCatalog.ResolveQuantization(model, quantization);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool simChanged = _activeSimulateVram != simulateVramLimit
                || (simulateVramLimit && Math.Abs(_activeSimulatedVramGb - simulatedVramLimitGb) > 0.05);

            if (IsRunning && !simChanged && _activeModel == model && _activeQuantization == resolvedQuant &&
                (requestedBackend == LocalBackendKind.Auto ||
                 _activeBackendKey.StartsWith(requestedBackend.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            bool hadRunningServer = IsRunning;
            await StopInternalAsync().ConfigureAwait(false);
            if (hadRunningServer)
            {
                // NVML boş VRAM sayacının eski sürecin belleğini bırakması için kısa bekleme
                await Task.Delay(200, ct).ConfigureAwait(false);
            }

            LastVramWarning = null;
            var driverInfo = NvidiaDriverChecker.Check();
            LastDriverInfo = driverInfo;

            if (!string.IsNullOrEmpty(driverInfo.WarningMessage))
            {
                NotifyStatus($"{driverInfo.WarningMessage} {driverInfo.UpdateInstructions}");
            }

            VramBudgetSnapshot vramSnapshot = requestedBackend != LocalBackendKind.Cpu
                ? PerformanceMonitorService.QueryEffectiveVramBudget(simulateVramLimit, simulatedVramLimitGb)
                : new VramBudgetSnapshot(false, "CPU", 0, 0, 0);

            GpuOffloadPlan gpuPlan = ComputeGpuOffloadPlan(model, resolvedQuant, vramSnapshot);
            ActiveGpuOffloadPlan = gpuPlan;

            if (model == LocalModelKind.HyMt2_7B && requestedBackend != LocalBackendKind.Cpu && vramSnapshot.Available)
            {
                if (gpuPlan.GpuLayers == 0)
                {
                    LastVramWarning =
                        $"Boş VRAM çok düşük ({vramSnapshot.FreeMb:0} / {vramSnapshot.TotalMb:0} MB). Tüm katmanlar RAM/CPU'ya kaydırıldı; hız için GPU kullanan uygulamaları kapatın.";
                    NotifyStatus($"⚠ {LastVramWarning}");
                }
                else if (vramSnapshot.TotalMb < 7500)
                {
                    LastVramWarning =
                        $"GPU toplam VRAM ({vramSnapshot.TotalMb:0} MB) düşük olduğu için {gpuPlan.Summary} uygulanacak.";
                    NotifyStatus($"⚠ {LastVramWarning}");
                }
            }

            var candidateBackends = BuildBackendFallbackOrder(requestedBackend, driverInfo);
            Exception? lastError = null;
            string modelLabel = model == LocalModelKind.HyMt2_7B ? "Hy-MT2 7B" : "TranslateGemma 4B";

            foreach (string backendKey in candidateBackends)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    NotifyStatus($"Yerel model dosyaları kontrol ediliyor ({modelLabel} {resolvedQuant} • {backendKey})…");
                    var progress = new Progress<DownloadProgressInfo>(info =>
                    {
                        DownloadProgress?.Invoke(info);
                        NotifyStatus(info.StatusText);
                    });

                    await _downloadService.EnsureLocalAssetsAsync(model, resolvedQuant, backendKey, progress, ct)
                        .ConfigureAwait(false);

                    NotifyStatus($"Model yükleniyor ({modelLabel} {resolvedQuant} • {gpuPlan.Summary})…");
                    await StartProcessAndWaitHealthyAsync(model, resolvedQuant, backendKey, preferredPort, cpuThreads, gpuPlan, ct)
                        .ConfigureAwait(false);

                    _activeBackendKey = backendKey;
                    _activeModel = model;
                    _activeQuantization = resolvedQuant;
                    _activeSimulateVram = simulateVramLimit;
                    _activeSimulatedVramGb = simulatedVramLimitGb;
                    string offloadLabel = backendKey == "cpu" ? "CPU" : gpuPlan.Summary;
                    NotifyStatus($"Hazır ({modelLabel} {resolvedQuant} • {backendKey} • {offloadLabel})");
                    return;
                }
                catch (OperationCanceledException)
                {
                    await StopInternalAsync().ConfigureAwait(false);
                    throw;
                }
                catch (InsufficientVramException)
                {
                    await StopInternalAsync().ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await StopInternalAsync().ConfigureAwait(false);
                    NotifyStatus($"{backendKey} başlatılamadı ({ex.Message}), yedek deneniyor…");
                }
            }

            throw new InvalidOperationException(
                $"Yerel llama-server hiçbir motorla başlatılamadı: {lastError?.Message}", lastError);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private sealed class InsufficientVramException(string message) : InvalidOperationException(message);

    private static List<string> BuildBackendFallbackOrder(LocalBackendKind requested, NvidiaDriverInfo driver)
    {
        var list = new List<string>();
        if (requested == LocalBackendKind.Cpu)
        {
            list.Add("cpu");
            return list;
        }

        if (requested == LocalBackendKind.Vulkan)
        {
            list.Add("vulkan");
            list.Add("cpu");
            return list;
        }

        if (driver.SupportsCuda13_5)
            list.Add("cuda-13.5");
        else if (driver.SupportsCuda12_4)
            list.Add("cuda-12.4");

        list.Add("vulkan");
        list.Add("cpu");
        return list;
    }

    public static GpuOffloadPlan ComputeGpuOffloadPlan(
        LocalModelKind model,
        ModelQuantization quantization,
        VramBudgetSnapshot vram)
    {
        ModelQuantization resolved = ModelCatalog.ResolveQuantization(model, quantization);
        int totalLayers;
        double perLayerMb;
        double outputLayerMb;
        double baseOverheadMb;

        if (model == LocalModelKind.HyMt2_7B)
        {
            totalLayers = 32;
            perLayerMb = 175.0;     // ~170.7 MiB ağırlık + ~4.3 MiB q8_0 KV (-c 2048)
            outputLayerMb = 411.0;  // tie_word_embeddings token_embd.weight (-ngl > 32)
            baseOverheadMb = 580.0; // CUDA context + hesap tamponları (-b 256 -ub 128)
        }
        else
        {
            totalLayers = 34;
            if (resolved == ModelQuantization.Q8_0)
            {
                perLayerMb = 114.0;
                outputLayerMb = 550.0;
                baseOverheadMb = 450.0;
            }
            else
            {
                perLayerMb = 70.0;
                outputLayerMb = 350.0;
                baseOverheadMb = 420.0;
            }
        }

        double fullGpuMb = (totalLayers * perLayerMb) + outputLayerMb + baseOverheadMb;
        if (!vram.Available || vram.TotalMb <= 0 || vram.FreeMb <= 0)
        {
            return new GpuOffloadPlan(99, totalLayers, true, Math.Round(fullGpuMb, 0), $"Tam GPU ({totalLayers}/{totalLayers})");
        }

        // OBS + Windows DWM + NVENC için 400 MB güvenlik payı bırak, kalan bütçeyi sonuna kadar GPU katmanlarına ver
        const double reserveMb = 400.0;
        double budgetMb = Math.Max(0, vram.FreeMb - reserveMb);

        if (budgetMb >= fullGpuMb)
        {
            return new GpuOffloadPlan(99, totalLayers, true, Math.Round(fullGpuMb, 0), $"Tam GPU ({totalLayers}/{totalLayers})");
        }

        double allTransformerOnlyMb = (totalLayers * perLayerMb) + baseOverheadMb;
        if (budgetMb >= allTransformerOnlyMb)
        {
            return new GpuOffloadPlan(
                totalLayers,
                totalLayers,
                false,
                Math.Round(allTransformerOnlyMb, 0),
                $"GPU {totalLayers}/{totalLayers} katman (çıktı tablosu RAM'de)");
        }

        double usableForLayersMb = budgetMb - baseOverheadMb;
        if (usableForLayersMb < perLayerMb)
        {
            return new GpuOffloadPlan(
                0,
                totalLayers,
                false,
                0,
                $"Tam RAM/CPU (0/{totalLayers} GPU — boş VRAM yetersiz)");
        }

        int gpuLayers = Math.Clamp((int)Math.Floor(usableForLayersMb / perLayerMb), 1, totalLayers - 1);
        int ramLayers = totalLayers - gpuLayers;
        double estVramMb = (gpuLayers * perLayerMb) + baseOverheadMb;
        return new GpuOffloadPlan(
            gpuLayers,
            totalLayers,
            false,
            Math.Round(estVramMb, 0),
            $"GPU {gpuLayers}/{totalLayers} katman • {ramLayers} katman RAM'de");
    }

    private async Task StartProcessAndWaitHealthyAsync(
        LocalModelKind model,
        ModelQuantization quantization,
        string backendKey,
        int preferredPort,
        int cpuThreads,
        GpuOffloadPlan gpuPlan,
        CancellationToken ct)
    {
        string exePath = _downloadService.GetLlamaServerExePath(backendKey);
        string modelPath = _downloadService.GetModelPath(model, quantization);
        int port = FindAvailablePort(preferredPort);
        int threads = Math.Clamp(cpuThreads, 1, 8);
        int ngl = backendKey == "cpu" ? 0 : gpuPlan.GpuLayers;

        string args;
        if (model == LocalModelKind.HyMt2_7B)
        {
            // Hy-MT2 7B Q6_K: -c 2048 + -ctk/-ctv q8_0 (KV ~136 MB), -b 256 -ub 128 (düşük VRAM + kısa CUDA dilimleri)
            string kvFlags = ngl > 0 ? "-ctk q8_0 -ctv q8_0 " : string.Empty;
            args =
                $"--host 127.0.0.1 --port {port} -m \"{modelPath}\" -ngl {ngl} -c 2048 -np 1 -fa on {kvFlags}-b 256 -ub 128 --cache-ram 0 --no-webui -t {threads}";
        }
        else
        {
            // TranslateGemma 4B: küçük bağlam (1024), tek slot (-np 1), flash attention açık (-fa on)
            args =
                $"--host 127.0.0.1 --port {port} -m \"{modelPath}\" -ngl {ngl} -c 1024 -np 1 -fa on --cache-ram 0 --no-webui -t {threads}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var stderrBuffer = new StringBuilder();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stderrBuffer.Length < 8192)
                stderrBuffer.AppendLine(e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException("llama-server.exe başlatılamadı.");

        // OBS ve yakalama akışının CPU önceliği her zaman llama-server önünde kalsın
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        var job = new JobObject();
        job.AssignProcess(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        _process = proc;
        _jobObject = job;
        _activePort = port;

        // /health uç noktasını bekle
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            ct.ThrowIfCancellationRequested();

            if (proc.HasExited)
            {
                string err = stderrBuffer.ToString().Trim();
                if (IsOutOfMemoryError(err))
                {
                    throw new InsufficientVramException(
                        $"GPU VRAM yetersiz kaldı ({model} {quantization}). OBS yayınında drop yaşamamak için GPU kullanan diğer uygulamaları kapatın veya TranslateGemma 4B / Google Translate moduna geçin.");
                }
                throw new InvalidOperationException(
                    $"llama-server erken kapandı (kod {proc.ExitCode}): {err}");
            }

            try
            {
                using var resp = await HealthClient.GetAsync($"http://127.0.0.1:{port}/health", ct)
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("status", out var statusEl) &&
                        string.Equals(statusEl.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            proc.Refresh();
                            _lastObservedWorkingSetBytes = proc.WorkingSet64;
                        }
                        catch { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Henüz port dinlemiyor, kısa aralıkla tekrar dene
            }

            await Task.Delay(120, ct).ConfigureAwait(false);
        }

        throw new TimeoutException("llama-server 90 saniye içinde hazır durumuna geçemedi.");
    }

    private static bool IsOutOfMemoryError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        return stderr.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("cudaMalloc failed", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Google moduna geçildiğinde llama-server sürecini sonlandırır ve RAM/VRAM boşalmasını doğrular.
    /// </summary>
    public async Task<OffloadVerificationResult> StopAndOffloadAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<OffloadVerificationResult> StopInternalAsync()
    {
        var proc = _process;
        var job = _jobObject;
        int? pid = null;
        long freedRam = _lastObservedWorkingSetBytes;

        if (proc is not null)
        {
            try
            {
                pid = proc.Id;
                if (!proc.HasExited)
                {
                    proc.Refresh();
                    freedRam = Math.Max(freedRam, proc.WorkingSet64);
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }

        job?.Dispose();
        _process = null;
        _jobObject = null;
        _activePort = 0;
        _activeBackendKey = string.Empty;
        ActiveGpuOffloadPlan = null;
        _lastObservedWorkingSetBytes = 0;

        bool exitedCleanly = pid is null || !IsProcessAlive(pid.Value);
        bool gpuGone = pid is null || await VerifyGpuProcessGoneAsync(pid.Value).ConfigureAwait(false);

        string summary = pid is null
            ? "Yerel sunucu zaten kapalı."
            : $"llama-server (PID {pid}) sonlandırıldı; ~{freedRam / (1024 * 1024)} MB RAM ve GPU VRAM boşaltıldı (GPU kontrolü: {(gpuGone ? "Temiz" : "Doğrulanamadı")}).";

        NotifyStatus("Yerel model bellekten boşaltıldı.");
        return new OffloadVerificationResult(exitedCleanly, freedRam, gpuGone, summary);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> VerifyGpuProcessGoneAsync(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-compute-apps=pid --format=csv,noheader",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var smi = Process.Start(psi);
            if (smi is null)
                return true;

            string output = await smi.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await smi.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var pids = output.Split(new[] { '\r', '\n', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return !pids.Contains(pid.ToString(), StringComparer.Ordinal);
        }
        catch
        {
            // nvidia-smi yoksa Process.HasExited yeterli kabul edilir
            return !IsProcessAlive(pid);
        }
    }

    private void NotifyStatus(string msg)
    {
        LastStatusMessage = msg;
        StatusChanged?.Invoke(msg);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch { }
        _process?.Dispose();
        _jobObject?.Dispose();
        _lifecycleLock.Dispose();
    }
}
