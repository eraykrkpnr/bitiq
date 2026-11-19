using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Bitiq.Core.Services;

public sealed record VramBudgetSnapshot(
    bool Available,
    string GpuName,
    double TotalMb,
    double UsedMb,
    double FreeMb);

public sealed record PerformanceSnapshot
{
    public double AppCpuPercent { get; init; }
    public double LlamaCpuPercent { get; init; }
    public double TotalCpuPercent => Math.Round(AppCpuPercent + LlamaCpuPercent, 1);

    public double AppRamMb { get; init; }
    public double LlamaRamMb { get; init; }
    public double TotalRamMb => Math.Round(AppRamMb + LlamaRamMb, 1);

    public bool IsLlamaRunning { get; init; }
    public int? LlamaPid { get; init; }
    public string ActiveBackend { get; init; } = string.Empty;

    public bool HasGpuMetrics { get; init; }
    public string GpuName { get; init; } = "Algılanmadı";
    public int GpuUtilizationPercent { get; init; }
    public double AppAndLlamaVramMb { get; init; }
    public bool HasPerProcessVram { get; init; }
    public bool IsLlamaOnGpu { get; init; }
    public double TotalVramUsedMb { get; init; }
    public double TotalVramCapacityMb { get; init; }
}

/// <summary>
/// bitiq ve yerel llama-server sürecinin CPU, RAM ve NVIDIA GPU/VRAM kullanımını
/// harici alt süreç (nvidia-smi.exe vb.) başlatmadan süreç içi (in-process) okur.
/// </summary>
public sealed class PerformanceMonitorService : IDisposable
{
    private const int NvmlErrorInsufficientSize = 7;

    private readonly Process _currentProcess;
    private Process? _llamaProcess;
    private int _cachedLlamaPid;

    private long _lastSampleTicks;
    private TimeSpan _lastAppCpuTime;
    private TimeSpan _lastLlamaCpuTime;

    private IntPtr _nvmlHandle;
    private IntPtr _nvmlDevice;
    private string _gpuName = "Algılanmadı";
    private bool _nvmlInitialized;
    private bool _nvmlUnavailable;
    private bool _disposed;

    private NvmlShutdownDelegate? _nvmlShutdown;
    private NvmlGetUtilizationRatesDelegate? _nvmlGetUtilization;
    private NvmlGetMemoryInfoDelegate? _nvmlGetMemoryInfo;
    private NvmlGetRunningProcessesV3Delegate? _nvmlGetComputeProcesses;
    private NvmlGetRunningProcessesV3Delegate? _nvmlGetGraphicsProcesses;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitV2Delegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndexV2Delegate(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetNameDelegate(IntPtr device, byte[] name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetUtilizationRatesDelegate(IntPtr device, out NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetMemoryInfoDelegate(IntPtr device, out NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetRunningProcessesV3Delegate(IntPtr device, ref uint infoCount, [Out] NvmlProcessInfoV3[]? infos);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint gpu;
        public uint memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong total;
        public ulong free;
        public ulong used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlProcessInfoV3
    {
        public uint pid;
        public ulong usedGpuMemory;
        public uint gpuInstanceId;
        public uint computeInstanceId;
    }

    public PerformanceMonitorService()
    {
        _currentProcess = Process.GetCurrentProcess();
        _lastSampleTicks = Stopwatch.GetTimestamp();
        try
        {
            _lastAppCpuTime = _currentProcess.TotalProcessorTime;
        }
        catch
        {
            _lastAppCpuTime = TimeSpan.Zero;
        }
    }

    public PerformanceSnapshot Sample(int? llamaPid, string activeBackend)
    {
        if (_disposed)
            return new PerformanceSnapshot();

        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedSec = (nowTicks - _lastSampleTicks) / (double)Stopwatch.Frequency;
        _lastSampleTicks = nowTicks;

        double appRamMb = 0;
        double appCpuPercent = 0;

        try
        {
            _currentProcess.Refresh();
            appRamMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0);
            TimeSpan curAppCpu = _currentProcess.TotalProcessorTime;
            if (elapsedSec > 0.05)
            {
                double cpuSec = (curAppCpu - _lastAppCpuTime).TotalSeconds;
                appCpuPercent = Math.Clamp((cpuSec / (elapsedSec * Environment.ProcessorCount)) * 100.0, 0.0, 100.0);
            }
            _lastAppCpuTime = curAppCpu;
        }
        catch { }

        double llamaRamMb = 0;
        double llamaCpuPercent = 0;
        bool isLlamaRunning = false;

        if (llamaPid is { } pid && pid > 0)
        {
            try
            {
                if (_llamaProcess is null || _cachedLlamaPid != pid)
                {
                    _llamaProcess?.Dispose();
                    _llamaProcess = Process.GetProcessById(pid);
                    _cachedLlamaPid = pid;
                    _lastLlamaCpuTime = _llamaProcess.TotalProcessorTime;
                }

                _llamaProcess.Refresh();
                if (!_llamaProcess.HasExited)
                {
                    isLlamaRunning = true;
                    llamaRamMb = _llamaProcess.WorkingSet64 / (1024.0 * 1024.0);
                    TimeSpan curLlamaCpu = _llamaProcess.TotalProcessorTime;
                    if (elapsedSec > 0.05)
                    {
                        double cpuSec = (curLlamaCpu - _lastLlamaCpuTime).TotalSeconds;
                        llamaCpuPercent = Math.Clamp((cpuSec / (elapsedSec * Environment.ProcessorCount)) * 100.0, 0.0, 100.0);
                    }
                    _lastLlamaCpuTime = curLlamaCpu;
                }
            }
            catch
            {
                _llamaProcess?.Dispose();
                _llamaProcess = null;
                _cachedLlamaPid = 0;
                _lastLlamaCpuTime = TimeSpan.Zero;
            }
        }
        else if (_llamaProcess is not null)
        {
            _llamaProcess.Dispose();
            _llamaProcess = null;
            _cachedLlamaPid = 0;
            _lastLlamaCpuTime = TimeSpan.Zero;
        }

        bool hasGpu = false;
        int gpuUtil = 0;
        double totalVramUsedMb = 0;
        double totalVramCapMb = 0;
        double procVramMb = 0;
        bool hasPerProcVram = false;
        bool isLlamaOnGpu = false;

        if (TryEnsureNvmlInitialized())
        {
            hasGpu = QueryGpuMetrics(
                _currentProcess.Id,
                isLlamaRunning ? llamaPid : null,
                out gpuUtil,
                out totalVramUsedMb,
                out totalVramCapMb,
                out procVramMb,
                out hasPerProcVram,
                out isLlamaOnGpu);
        }

        return new PerformanceSnapshot
        {
            AppCpuPercent = Math.Round(appCpuPercent, 1),
            LlamaCpuPercent = Math.Round(llamaCpuPercent, 1),
            AppRamMb = Math.Round(appRamMb, 1),
            LlamaRamMb = Math.Round(llamaRamMb, 1),
            IsLlamaRunning = isLlamaRunning,
            LlamaPid = isLlamaRunning ? llamaPid : null,
            ActiveBackend = activeBackend ?? string.Empty,
            HasGpuMetrics = hasGpu,
            GpuName = _gpuName,
            GpuUtilizationPercent = gpuUtil,
            AppAndLlamaVramMb = Math.Round(procVramMb, 1),
            HasPerProcessVram = hasPerProcVram,
            IsLlamaOnGpu = isLlamaOnGpu,
            TotalVramUsedMb = Math.Round(totalVramUsedMb, 1),
            TotalVramCapacityMb = Math.Round(totalVramCapMb, 1)
        };
    }

    /// <summary>
    /// Harici süreç başlatmadan GPU toplam ve boş VRAM bilgisini döndürür (ön kontrol için).
    /// </summary>
    public static VramBudgetSnapshot QueryVramBudget()
    {
        try
        {
            using var svc = new PerformanceMonitorService();
            if (!svc.TryEnsureNvmlInitialized() || svc._nvmlDevice == IntPtr.Zero || svc._nvmlGetMemoryInfo is null)
                return new VramBudgetSnapshot(false, "Algılanmadı", 0, 0, 0);

            if (svc._nvmlGetMemoryInfo(svc._nvmlDevice, out var mem) == 0 && mem.total > 0)
            {
                double totalMb = mem.total / (1024.0 * 1024.0);
                double usedMb = mem.used / (1024.0 * 1024.0);
                double freeMb = mem.free / (1024.0 * 1024.0);
                return new VramBudgetSnapshot(true, svc._gpuName, Math.Round(totalMb, 1), Math.Round(usedMb, 1), Math.Round(freeMb, 1));
            }
        }
        catch { }

        return new VramBudgetSnapshot(false, "Algılanmadı", 0, 0, 0);
    }

    /// <summary>
    /// Simülasyon açıksa karttaki gerçek dolu belleği koruyarak toplam/boş VRAM'i seçilen limite göre sınırlar.
    /// </summary>
    public static VramBudgetSnapshot QueryEffectiveVramBudget(bool simulateEnabled, double simulatedLimitGb)
        => ApplySimulatedVramLimit(QueryVramBudget(), simulateEnabled, simulatedLimitGb);

    /// <summary>
    /// Verilen gerçek VRAM anlık görüntüsüne simüle edilmiş kapasite sınırını deterministik olarak uygular.
    /// </summary>
    public static VramBudgetSnapshot ApplySimulatedVramLimit(
        VramBudgetSnapshot real,
        bool simulateEnabled,
        double simulatedLimitGb)
    {
        if (!real.Available || !simulateEnabled)
            return real;

        double clampedGb = Math.Clamp(simulatedLimitGb, 4.0, 32.0);
        double simTotalMb = clampedGb * 1024.0;
        double systemReservedMb = Math.Max(0.0, real.TotalMb - real.UsedMb - real.FreeMb);
        double simFreeMb = Math.Max(0.0, simTotalMb - real.UsedMb - systemReservedMb);

        return new VramBudgetSnapshot(
            Available: true,
            GpuName: $"{real.GpuName} (Simüle {clampedGb:0.#} GB)",
            TotalMb: Math.Round(simTotalMb, 1),
            UsedMb: real.UsedMb,
            FreeMb: Math.Round(simFreeMb, 1));
    }

    private bool TryEnsureNvmlInitialized()
    {
        if (_nvmlInitialized)
            return true;
        if (_nvmlUnavailable)
            return false;

        try
        {
            string nvsmiPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvml.dll");

            if (!NativeLibrary.TryLoad("nvml.dll", out _nvmlHandle) &&
                (!File.Exists(nvsmiPath) || !NativeLibrary.TryLoad(nvsmiPath, out _nvmlHandle)))
            {
                _nvmlUnavailable = true;
                return false;
            }

            if (!NativeLibrary.TryGetExport(_nvmlHandle, "nvmlInit_v2", out IntPtr initPtr) ||
                !NativeLibrary.TryGetExport(_nvmlHandle, "nvmlDeviceGetHandleByIndex_v2", out IntPtr getHandlePtr) ||
                !NativeLibrary.TryGetExport(_nvmlHandle, "nvmlDeviceGetUtilizationRates", out IntPtr getUtilPtr) ||
                !NativeLibrary.TryGetExport(_nvmlHandle, "nvmlDeviceGetMemoryInfo", out IntPtr getMemPtr))
            {
                CleanupNvml();
                _nvmlUnavailable = true;
                return false;
            }

            var initFn = Marshal.GetDelegateForFunctionPointer<NvmlInitV2Delegate>(initPtr);
            if (initFn() != 0)
            {
                CleanupNvml();
                _nvmlUnavailable = true;
                return false;
            }

            if (NativeLibrary.TryGetExport(_nvmlHandle, "nvmlShutdown", out IntPtr shutdownPtr))
                _nvmlShutdown = Marshal.GetDelegateForFunctionPointer<NvmlShutdownDelegate>(shutdownPtr);

            var getHandleFn = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetHandleByIndexV2Delegate>(getHandlePtr);
            if (getHandleFn(0, out _nvmlDevice) != 0 || _nvmlDevice == IntPtr.Zero)
            {
                CleanupNvml();
                _nvmlUnavailable = true;
                return false;
            }

            if (NativeLibrary.TryGetExport(_nvmlHandle, "nvmlDeviceGetName", out IntPtr getNamePtr))
            {
                var getNameFn = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetNameDelegate>(getNamePtr);
                byte[] nameBuf = new byte[128];
                if (getNameFn(_nvmlDevice, nameBuf, (uint)nameBuf.Length) == 0)
                {
                    int nullIdx = Array.IndexOf(nameBuf, (byte)0);
                    string rawName = Encoding.UTF8.GetString(nameBuf, 0, nullIdx >= 0 ? nullIdx : nameBuf.Length).Trim();
                    if (!string.IsNullOrWhiteSpace(rawName))
                        _gpuName = rawName;
                }
            }

            _nvmlGetUtilization = Marshal.GetDelegateForFunctionPointer<NvmlGetUtilizationRatesDelegate>(getUtilPtr);
            _nvmlGetMemoryInfo = Marshal.GetDelegateForFunctionPointer<NvmlGetMemoryInfoDelegate>(getMemPtr);

            if (NativeLibrary.TryGetExport(_nvmlHandle, "nvmlDeviceGetComputeRunningProcesses_v3", out IntPtr compPtr))
                _nvmlGetComputeProcesses = Marshal.GetDelegateForFunctionPointer<NvmlGetRunningProcessesV3Delegate>(compPtr);

            if (NativeLibrary.TryGetExport(_nvmlHandle, "nvmlDeviceGetGraphicsRunningProcesses_v3", out IntPtr gfxPtr))
                _nvmlGetGraphicsProcesses = Marshal.GetDelegateForFunctionPointer<NvmlGetRunningProcessesV3Delegate>(gfxPtr);

            _nvmlInitialized = true;
            return true;
        }
        catch
        {
            CleanupNvml();
            _nvmlUnavailable = true;
            return false;
        }
    }

    private bool QueryGpuMetrics(
        int appPid,
        int? llamaPid,
        out int gpuUtilPercent,
        out double totalVramUsedMb,
        out double totalVramCapMb,
        out double procVramMb,
        out bool hasPerProcVram,
        out bool isLlamaOnGpu)
    {
        gpuUtilPercent = 0;
        totalVramUsedMb = 0;
        totalVramCapMb = 0;
        procVramMb = 0;
        hasPerProcVram = false;
        isLlamaOnGpu = false;

        if (_nvmlDevice == IntPtr.Zero || _nvmlGetUtilization is null || _nvmlGetMemoryInfo is null)
            return false;

        bool anyOk = false;

        if (_nvmlGetUtilization(_nvmlDevice, out var util) == 0)
        {
            gpuUtilPercent = (int)Math.Clamp(util.gpu, 0, 100);
            anyOk = true;
        }

        if (_nvmlGetMemoryInfo(_nvmlDevice, out var mem) == 0 && mem.total > 0)
        {
            totalVramUsedMb = mem.used / (1024.0 * 1024.0);
            totalVramCapMb = mem.total / (1024.0 * 1024.0);
            anyOk = true;
        }

        ulong trackedVramBytes = 0;
        bool trackedAnyValidVram = false;
        var seenPids = new HashSet<uint>();

        InspectProcessList(_nvmlGetComputeProcesses, appPid, llamaPid, seenPids, ref trackedVramBytes, ref trackedAnyValidVram, ref isLlamaOnGpu);
        InspectProcessList(_nvmlGetGraphicsProcesses, appPid, llamaPid, seenPids, ref trackedVramBytes, ref trackedAnyValidVram, ref isLlamaOnGpu);

        if (trackedAnyValidVram)
        {
            hasPerProcVram = true;
            procVramMb = trackedVramBytes / (1024.0 * 1024.0);
        }

        return anyOk;
    }

    private void InspectProcessList(
        NvmlGetRunningProcessesV3Delegate? queryFn,
        int appPid,
        int? llamaPid,
        HashSet<uint> seenPids,
        ref ulong trackedVramBytes,
        ref bool trackedAnyValidVram,
        ref bool isLlamaOnGpu)
    {
        if (queryFn is null || _nvmlDevice == IntPtr.Zero)
            return;

        try
        {
            uint count = 0;
            int status = queryFn(_nvmlDevice, ref count, null);
            if (status != NvmlErrorInsufficientSize && status != 0)
                return;
            if (count == 0)
                return;

            var infos = new NvmlProcessInfoV3[count + 4];
            uint bufCount = (uint)infos.Length;
            if (queryFn(_nvmlDevice, ref bufCount, infos) != 0)
                return;

            int actualCount = (int)Math.Min(bufCount, (uint)infos.Length);
            for (int i = 0; i < actualCount; i++)
            {
                uint pid = infos[i].pid;
                bool isApp = pid == (uint)appPid;
                bool isLlama = llamaPid.HasValue && pid == (uint)llamaPid.Value;

                if (!isApp && !isLlama)
                    continue;

                if (isLlama)
                    isLlamaOnGpu = true;

                if (seenPids.Add(pid) && infos[i].usedGpuMemory != ulong.MaxValue)
                {
                    trackedVramBytes += infos[i].usedGpuMemory;
                    trackedAnyValidVram = true;
                }
            }
        }
        catch { }
    }

    private void CleanupNvml()
    {
        try
        {
            if (_nvmlInitialized)
                _nvmlShutdown?.Invoke();
        }
        catch { }

        _nvmlInitialized = false;
        _nvmlDevice = IntPtr.Zero;
        _nvmlShutdown = null;
        _nvmlGetUtilization = null;
        _nvmlGetMemoryInfo = null;
        _nvmlGetComputeProcesses = null;
        _nvmlGetGraphicsProcesses = null;

        if (_nvmlHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_nvmlHandle);
            _nvmlHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _llamaProcess?.Dispose();
        _llamaProcess = null;
        _currentProcess.Dispose();
        CleanupNvml();
    }
}
