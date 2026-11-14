using System.Diagnostics;
using System.Runtime.InteropServices;
using Bitiq.Core.Models;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Bitiq.Core.Capture;

/// <summary>
/// Windows.Graphics.Capture ile olay tabanlı (polling yok) ve GPU üzerinde bölge kırpmalı yakalama servisi.
/// </summary>
public sealed class WgcRegionCaptureService : IDisposable
{
    private readonly FrameChangeDetector _changeDetector = new();
    private readonly object _stateLock = new();

    private IntPtr _d3d11Device;
    private IntPtr _d3d11Context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private IntPtr _stagingTexture;
    private uint _stagingWidth;
    private uint _stagingHeight;

    private CaptureRegion _region = new();
    private IntPtr _targetWindowHwnd;
    private int _lastContentWidth;
    private int _lastContentHeight;
    private double _changeThreshold = 2.0;
    private bool _running;
    private bool _disposed;

    public IntPtr ActiveTargetWindowHwnd => _targetWindowHwnd;

    public event Action<CapturedFrameData>? FrameChanged;
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => _running;

    public void UpdateRegion(CaptureRegion region)
    {
        lock (_stateLock)
        {
            _region = region;
            _changeDetector.Reset();
        }
    }

    public void UpdateChangeThreshold(double threshold)
    {
        lock (_stateLock)
        {
            _changeThreshold = Math.Clamp(threshold, 0.2, 50.0);
        }
    }

    public void Start(CaptureRegion region, double changeThreshold = 2.0, IntPtr targetWindowHwnd = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WgcRegionCaptureService));

        Stop();

        if (targetWindowHwnd == IntPtr.Zero && region.IsWindowLocked)
            targetWindowHwnd = WindowEnumerationService.ResolveTargetWindow(region);

        lock (_stateLock)
        {
            _region = region;
            _targetWindowHwnd = targetWindowHwnd;
            _changeThreshold = Math.Clamp(changeThreshold, 0.2, 50.0);
            _changeDetector.Reset();
        }

        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows.Graphics.Capture bu sistemde desteklenmiyor (Windows 10 1903+ gerekli).");

        InitializeD3D11();

        if (targetWindowHwnd != IntPtr.Zero)
        {
            WindowEnumerationService.EnsureWindowRenderableInBackground(targetWindowHwnd);
            _captureItem = CreateCaptureItemForWindow(targetWindowHwnd);
        }
        else
        {
            int centerX = region.X + (region.Width / 2);
            int centerY = region.Y + (region.Height / 2);
            IntPtr hMonitor = Direct3D11Interop.GetMonitorFromPoint(centerX, centerY);
            if (hMonitor == IntPtr.Zero)
                throw new InvalidOperationException("Seçilen bölge için monitör bulunamadı.");

            _captureItem = CreateCaptureItemForMonitor(hMonitor);
        }

        if (_captureItem is null)
            throw new InvalidOperationException("Yakalama öğesi oluşturulamadı.");

        _lastContentWidth = _captureItem.Size.Width;
        _lastContentHeight = _captureItem.Size.Height;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice!,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _captureItem.Size);

        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_captureItem);

        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                _session.IsCursorCaptureEnabled = false;
        }
        catch { }

        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                var session3 = _session.As<IGraphicsCaptureSession3>();
                session3?.put_IsBorderRequired(false);
            }
        }
        catch { }

        _running = true;
        _session.StartCapture();
    }

    public void Stop()
    {
        _running = false;
        _targetWindowHwnd = IntPtr.Zero;
        _lastContentWidth = 0;
        _lastContentHeight = 0;

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _session = null;

        _framePool?.Dispose();
        _framePool = null;
        _captureItem = null;

        ReleaseStagingTexture();
        ReleaseD3D11();
        _changeDetector.Reset();
    }

    private unsafe void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_running || _disposed)
            return;

        using var frame = sender.TryGetNextFrame();
        if (frame is null)
            return;

        var swCapture = Stopwatch.StartNew();

        CaptureRegion region;
        IntPtr targetWindowHwnd;
        double threshold;
        lock (_stateLock)
        {
            region = _region;
            targetWindowHwnd = _targetWindowHwnd;
            threshold = _changeThreshold;
        }

        if (!region.IsValid)
            return;

        try
        {
            SizeInt32 contentSize = frame.ContentSize;
            if (contentSize.Width <= 0 || contentSize.Height <= 0)
                return;

            // Hedef pencere veya monitör yeniden boyutlandıysa frame pool'u güncelle
            if (contentSize.Width != _lastContentWidth || contentSize.Height != _lastContentHeight)
            {
                _lastContentWidth = contentSize.Width;
                _lastContentHeight = contentSize.Height;
                sender.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
                return;
            }

            uint srcLeft, srcTop, copyWidth, copyHeight;
            if (region.IsWindowLocked && targetWindowHwnd != IntPtr.Zero)
            {
                int relX = region.WindowRelativeX;
                int relY = region.WindowRelativeY;
                int regW = region.Width;
                int regH = region.Height;

                if (region.NormalizedWidth > 0.005 && region.NormalizedHeight > 0.005 &&
                    (Math.Abs(contentSize.Width - region.ReferenceWindowWidth) > 4 ||
                     Math.Abs(contentSize.Height - region.ReferenceWindowHeight) > 4))
                {
                    relX = (int)Math.Round(region.NormalizedX * contentSize.Width);
                    relY = (int)Math.Round(region.NormalizedY * contentSize.Height);
                    regW = (int)Math.Round(region.NormalizedWidth * contentSize.Width);
                    regH = (int)Math.Round(region.NormalizedHeight * contentSize.Height);
                }

                srcLeft = (uint)Math.Clamp(relX, 0, Math.Max(0, contentSize.Width - 1));
                srcTop = (uint)Math.Clamp(relY, 0, Math.Max(0, contentSize.Height - 1));
                copyWidth = (uint)Math.Clamp(regW, 1, contentSize.Width - (int)srcLeft);
                copyHeight = (uint)Math.Clamp(regH, 1, contentSize.Height - (int)srcTop);
            }
            else
            {
                srcLeft = (uint)Math.Clamp(region.RelativeX, 0, Math.Max(0, contentSize.Width - 1));
                srcTop = (uint)Math.Clamp(region.RelativeY, 0, Math.Max(0, contentSize.Height - 1));
                copyWidth = (uint)Math.Clamp(region.Width, 1, contentSize.Width - (int)srcLeft);
                copyHeight = (uint)Math.Clamp(region.Height, 1, contentSize.Height - (int)srcTop);
            }

            if (copyWidth < 4 || copyHeight < 4)
                return;

            IntPtr srcTexture = GetDxgiTextureFromSurface(frame.Surface);
            if (srcTexture == IntPtr.Zero)
                return;

            try
            {
                var pixels = CopyRegionFromTexture(
                    srcTexture,
                    srcLeft,
                    srcTop,
                    copyWidth,
                    copyHeight,
                    checkDiff: true,
                    threshold,
                    swCapture,
                    out double captureMs,
                    out double changeDetectMs);

                if (pixels is null)
                    return;

                FrameChanged?.Invoke(new CapturedFrameData
                {
                    BgraPixels = pixels,
                    Width = (int)copyWidth,
                    Height = (int)copyHeight,
                    CaptureMs = captureMs,
                    ChangeDetectMs = changeDetectMs,
                    CapturedAtTimestamp = Stopwatch.GetTimestamp()
                });
            }
            finally
            {
                Marshal.Release(srcTexture);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Yakalama hatası: {ex.Message}");
        }
    }

    private unsafe byte[]? CopyRegionFromTexture(
        IntPtr srcTexture,
        uint srcLeft,
        uint srcTop,
        uint copyWidth,
        uint copyHeight,
        bool checkDiff,
        double threshold,
        Stopwatch swCapture,
        out double captureMs,
        out double changeDetectMs)
    {
        captureMs = 0;
        changeDetectMs = 0;

        EnsureStagingTexture(copyWidth, copyHeight);

        var box = new Direct3D11Interop.D3D11_BOX
        {
            left = srcLeft,
            top = srcTop,
            front = 0,
            right = srcLeft + copyWidth,
            bottom = srcTop + copyHeight,
            back = 1
        };

        Direct3D11Interop.CopySubresourceRegion(
            _d3d11Context,
            _stagingTexture,
            0,
            0,
            0,
            0,
            srcTexture,
            0,
            in box);

        int hr = Direct3D11Interop.Map(
            _d3d11Context,
            _stagingTexture,
            0,
            Direct3D11Interop.D3D11_MAP_READ,
            0,
            out var mapped);

        if (hr < 0 || mapped.pData == IntPtr.Zero)
            return null;

        try
        {
            swCapture.Stop();
            captureMs = swCapture.Elapsed.TotalMilliseconds;
            int rowPitch = (int)mapped.RowPitch;

            if (checkDiff)
            {
                var swDiff = Stopwatch.StartNew();
                int bufferLength = rowPitch * (int)copyHeight;
                var mappedSpan = new ReadOnlySpan<byte>((void*)mapped.pData, bufferLength);

                bool changed = _changeDetector.HasSignificantChange(
                    mappedSpan,
                    (int)copyWidth,
                    (int)copyHeight,
                    rowPitch,
                    threshold,
                    out _);

                swDiff.Stop();
                changeDetectMs = swDiff.Elapsed.TotalMilliseconds;
                if (!changed)
                    return null;
            }

            // Sıkı paketlenmiş (tightly packed) BGRA piksel dizisine kopyala
            int packedStride = (int)copyWidth * 4;
            var pixels = new byte[packedStride * (int)copyHeight];
            byte* srcPtr = (byte*)mapped.pData;
            fixed (byte* dstPtr = pixels)
            {
                if (rowPitch == packedStride)
                {
                    Buffer.MemoryCopy(srcPtr, dstPtr, pixels.Length, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < (int)copyHeight; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + (y * rowPitch),
                            dstPtr + (y * packedStride),
                            packedStride,
                            packedStride);
                    }
                }
            }

            return pixels;
        }
        finally
        {
            Direct3D11Interop.Unmap(_d3d11Context, _stagingTexture, 0);
        }
    }

    /// <summary>
    /// Hedef pencerenin tüm grafik yüzeyinden anlık tek bir kare yakalar (arka planda veya örtülü olsa bile çalışır).
    /// UI thread'i kilitlememek için arka plan thread'inden çağrılmalıdır.
    /// </summary>
    public static CapturedFrameData? TryCaptureWindowSnapshot(IntPtr hwnd, int timeoutMs = 900)
    {
        if (!WindowEnumerationService.IsWindowValid(hwnd) || !GraphicsCaptureSession.IsSupported())
            return null;

        bool wasMinimized = WindowEnumerationService.IsWindowMinimized(hwnd);
        WindowEnumerationService.EnsureWindowRenderableInBackground(hwnd);
        if (wasMinimized)
            Thread.Sleep(120);

        using var service = new WgcRegionCaptureService();
        using var doneEvent = new ManualResetEventSlim(false);
        CapturedFrameData? captured = null;

        try
        {
            service.InitializeD3D11();
            var item = CreateCaptureItemForWindow(hwnd);
            if (item is null || item.Size.Width <= 0 || item.Size.Height <= 0)
                return null;

            service._captureItem = item;
            service._framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                service._winrtDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);

            void Handler(Direct3D11CaptureFramePool sender, object args)
            {
                if (doneEvent.IsSet)
                    return;

                try
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame is null)
                        return;

                    SizeInt32 size = frame.ContentSize;
                    if (size.Width <= 4 || size.Height <= 4)
                        return;

                    IntPtr srcTexture = GetDxgiTextureFromSurface(frame.Surface);
                    if (srcTexture == IntPtr.Zero)
                        return;

                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var pixels = service.CopyRegionFromTexture(
                            srcTexture,
                            0,
                            0,
                            (uint)size.Width,
                            (uint)size.Height,
                            checkDiff: false,
                            threshold: 0,
                            sw,
                            out double captureMs,
                            out _);

                        if (pixels is not null)
                        {
                            captured = new CapturedFrameData
                            {
                                BgraPixels = pixels,
                                Width = size.Width,
                                Height = size.Height,
                                CaptureMs = captureMs,
                                ChangeDetectMs = 0,
                                CapturedAtTimestamp = Stopwatch.GetTimestamp()
                            };
                            doneEvent.Set();
                        }
                    }
                    finally
                    {
                        Marshal.Release(srcTexture);
                    }
                }
                catch
                {
                    doneEvent.Set();
                }
            }

            service._framePool.FrameArrived += Handler;
            service._session = service._framePool.CreateCaptureSession(item);
            try
            {
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                    service._session.IsCursorCaptureEnabled = false;
            }
            catch { }
            try
            {
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                {
                    var session3 = service._session.As<IGraphicsCaptureSession3>();
                    session3?.put_IsBorderRequired(false);
                }
            }
            catch { }

            service._session.StartCapture();
            doneEvent.Wait(timeoutMs);
            service._framePool.FrameArrived -= Handler;
            return captured;
        }
        catch
        {
            return null;
        }
    }

    private void InitializeD3D11()
    {
        int hr = Direct3D11Interop.D3D11CreateDevice(
            IntPtr.Zero,
            Direct3D11Interop.D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            Direct3D11Interop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero,
            0,
            Direct3D11Interop.D3D11_SDK_VERSION,
            out _d3d11Device,
            out _,
            out _d3d11Context);

        Marshal.ThrowExceptionForHR(hr);

        var dxgiGuid = Direct3D11Interop.IID_IDXGIDevice;
        hr = Marshal.QueryInterface(_d3d11Device, ref dxgiGuid, out IntPtr dxgiDevice);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            hr = Direct3D11Interop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr graphicsDevice);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
            }
            finally
            {
                Marshal.Release(graphicsDevice);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    private static GraphicsCaptureItem? CreateCaptureItemForWindow(IntPtr hwnd)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = Direct3D11Interop.WindowsCreateString(classId, classId.Length, out IntPtr hstring);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var interopGuid = Direct3D11Interop.IID_IGraphicsCaptureItemInterop;
            hr = Direct3D11Interop.RoGetActivationFactory(hstring, ref interopGuid, out IntPtr factoryPtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                var interop = (Direct3D11Interop.IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var itemGuid = Direct3D11Interop.IID_IGraphicsCaptureItem;
                hr = interop.CreateForWindow(hwnd, ref itemGuid, out IntPtr itemPtr);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            Direct3D11Interop.WindowsDeleteString(hstring);
        }
    }

    private static GraphicsCaptureItem? CreateCaptureItemForMonitor(IntPtr hMonitor)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = Direct3D11Interop.WindowsCreateString(classId, classId.Length, out IntPtr hstring);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var interopGuid = Direct3D11Interop.IID_IGraphicsCaptureItemInterop;
            hr = Direct3D11Interop.RoGetActivationFactory(hstring, ref interopGuid, out IntPtr factoryPtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                var interop = (Direct3D11Interop.IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var itemGuid = Direct3D11Interop.IID_IGraphicsCaptureItem;
                hr = interop.CreateForMonitor(hMonitor, ref itemGuid, out IntPtr itemPtr);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            Direct3D11Interop.WindowsDeleteString(hstring);
        }
    }

    private static IntPtr GetDxgiTextureFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<Direct3D11Interop.IDirect3DDxgiInterfaceAccess>();
        var texGuid = Direct3D11Interop.IID_ID3D11Texture2D;
        int hr = access.GetInterface(ref texGuid, out IntPtr texPtr);
        if (hr < 0)
            return IntPtr.Zero;
        return texPtr;
    }

    private void EnsureStagingTexture(uint width, uint height)
    {
        if (_stagingTexture != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
            return;

        ReleaseStagingTexture();

        var desc = new Direct3D11Interop.D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Direct3D11Interop.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new Direct3D11Interop.DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = Direct3D11Interop.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = Direct3D11Interop.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        int hr = Direct3D11Interop.CreateTexture2D(_d3d11Device, in desc, out _stagingTexture);
        Marshal.ThrowExceptionForHR(hr);
        _stagingWidth = width;
        _stagingHeight = height;
    }

    private void ReleaseStagingTexture()
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }
        _stagingWidth = 0;
        _stagingHeight = 0;
    }

    private void ReleaseD3D11()
    {
        if (_winrtDevice is IDisposable d)
            d.Dispose();
        _winrtDevice = null;

        if (_d3d11Context != IntPtr.Zero)
        {
            Marshal.Release(_d3d11Context);
            _d3d11Context = IntPtr.Zero;
        }

        if (_d3d11Device != IntPtr.Zero)
        {
            Marshal.Release(_d3d11Device);
            _d3d11Device = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    [ComImport]
    [System.Runtime.InteropServices.Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IGraphicsCaptureSession3
    {
        [PreserveSig]
        int get_IsBorderRequired(out bool value);

        [PreserveSig]
        int put_IsBorderRequired([MarshalAs(UnmanagedType.U1)] bool value);
    }
}
