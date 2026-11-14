using System.Runtime.InteropServices;

namespace Bitiq.Core.Capture;

internal static unsafe class Direct3D11Interop
{
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_SDK_VERSION = 7;

    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const int D3D11_MAP_READ = 1;

    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    public static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    public static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static readonly Guid IID_IMemoryBufferByteAccess = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    public static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
    public static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint left;
        public uint top;
        public uint front;
        public uint right;
        public uint bottom;
        public uint back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(ref Guid iid, out IntPtr pResource);
    }

    [ComImport]
    [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    // Vtable helpers for ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D
    // Avoiding full COM interface declarations keeps AOT/single-file trimming clean.
    public static int CreateTexture2D(IntPtr d3d11Device, in D3D11_TEXTURE2D_DESC desc, out IntPtr texture2D)
    {
        // ID3D11Device::CreateTexture2D is at vtable index 5 (IUnknown: 0,1,2; ID3D11Device: CreateBuffer=3, CreateTexture1D=4, CreateTexture2D=5)
        var vtable = *(IntPtr**)d3d11Device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vtable[5];
        fixed (D3D11_TEXTURE2D_DESC* pDesc = &desc)
        fixed (IntPtr* pTex = &texture2D)
        {
            return fn(d3d11Device, pDesc, IntPtr.Zero, pTex);
        }
    }

    public static void GetTexture2DDesc(IntPtr texture2D, out D3D11_TEXTURE2D_DESC desc)
    {
        // ID3D11Texture2D::GetDesc is at vtable index 10
        // IUnknown(3) + ID3D11DeviceChild(4: 3,4,5,6) + ID3D11Resource(3: 7,8,9) + GetDesc(10)
        var vtable = *(IntPtr**)texture2D;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)vtable[10];
        fixed (D3D11_TEXTURE2D_DESC* pDesc = &desc)
        {
            fn(texture2D, pDesc);
        }
    }

    public static void CopySubresourceRegion(
        IntPtr d3d11Context,
        IntPtr dstResource,
        uint dstSubresource,
        uint dstX,
        uint dstY,
        uint dstZ,
        IntPtr srcResource,
        uint srcSubresource,
        in D3D11_BOX srcBox)
    {
        // ID3D11DeviceContext::CopySubresourceRegion is at vtable index 46
        // Verified Windows SDK 10.0.19041+ d3d11.h ID3D11DeviceContext vtable offset:
        // IUnknown(0..2), ID3D11DeviceChild(3..6), VSSetConstantBuffers(7)..CopySubresourceRegion(46)
        var vtable = *(IntPtr**)d3d11Context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)vtable[46];
        fixed (D3D11_BOX* pBox = &srcBox)
        {
            fn(d3d11Context, dstResource, dstSubresource, dstX, dstY, dstZ, srcResource, srcSubresource, pBox);
        }
    }

    public static int Map(
        IntPtr d3d11Context,
        IntPtr resource,
        uint subresource,
        int mapType,
        uint mapFlags,
        out D3D11_MAPPED_SUBRESOURCE mappedResource)
    {
        // ID3D11DeviceContext::Map is at vtable index 14
        var vtable = *(IntPtr**)d3d11Context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)vtable[14];
        fixed (D3D11_MAPPED_SUBRESOURCE* pMapped = &mappedResource)
        {
            return fn(d3d11Context, resource, subresource, mapType, mapFlags, pMapped);
        }
    }

    public static void Unmap(IntPtr d3d11Context, IntPtr resource, uint subresource)
    {
        // ID3D11DeviceContext::Unmap is at vtable index 15
        var vtable = *(IntPtr**)d3d11Context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtable[15];
        fn(d3d11Context, resource, subresource);
    }

    public static IntPtr GetMonitorFromPoint(int x, int y)
    {
        return MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
}
