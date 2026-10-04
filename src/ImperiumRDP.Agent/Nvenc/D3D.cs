using System.Runtime.InteropServices;

namespace ImperiumRDP.Agent.Nvenc;

/// <summary>
/// Минимальный D3D11/DXGI interop для Desktop Duplication.
/// Vtable-порядок и IID сверены с Windows SDK 10.0.26100 (d3d11.h, dxgi.h, dxgi1_2.h).
/// Неиспользуемые методы объявлены без параметров — для выравнивания важен только порядок.
/// </summary>
internal static class D3D
{
    public const uint D3D11_SDK_VERSION = 7;
    public const int DriverTypeUnknown = 0;   // обязательный тип при явном адаптере
    public const uint CreateDeviceBgraSupport = 0x20;
    public const uint CreateDeviceVideoSupport = 0x800; // требуется NVENC

    public const uint FormatB8G8R8A8Unorm = 87;
    public const uint UsageDefault = 0, UsageStaging = 3;
    public const uint BindRenderTarget = 0x20;
    public const uint CpuAccessRead = 0x20000, CpuAccessWrite = 0x10000;
    public const uint MapRead = 1;

    public static readonly int DxgiErrorWaitTimeout = unchecked((int)0x87A00027);
    public static readonly int DxgiErrorAccessLost = unchecked((int)0x87A00026);

    public static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IidD3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    public static readonly Guid IidIdxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", PreserveSig = false)]
    public static extern void CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out ID3D11Device ppDevice, out IntPtr pFeatureLevel, out ID3D11DeviceContext ppImmediateContext);

    [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1", PreserveSig = false)]
    private static extern void CreateDXGIFactory1Private(ref Guid riid, out IntPtr ppFactory);

    public static IDXGIFactory1 CreateFactory()
    {
        Guid iid = IidIdxgiFactory1;
        CreateDXGIFactory1Private(ref iid, out IntPtr ptr);
        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(ptr);
        Marshal.Release(ptr);
        return factory;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct Luid { public uint LowPart; public int HighPart; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DxgiAdapterDesc1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public Luid AdapterLuid;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DxgiRect { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DxgiOutputDesc
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public DxgiRect DesktopCoordinates;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;
}

[StructLayout(LayoutKind.Sequential)]
public struct DxgiOutduplPointerPosition
{
    public int X, Y;
    public int Visible; // BOOL
}

[StructLayout(LayoutKind.Sequential)]
public struct DxgiOutduplFrameInfo
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public long AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public DxgiOutduplPointerPosition PointerPosition;
    public uint MouseButtonsCounter;
}

[StructLayout(LayoutKind.Sequential)]
public struct DxgiOutduplPointerShapeInfo
{
    public uint Type;      // 1=color(ARGB32), 2=monochrome, 4=masked color
    public uint Width, Height, Pitch;
    public int HotSpotX, HotSpotY;
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D11Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize;
    public uint Format;
    public uint SampleCount, SampleQuality;
    public uint Usage;
    public uint BindFlags, CPUAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D11MappedSubresource
{
    public IntPtr pData;
    public uint RowPitch, DepthPitch;
}

// ---------- интерфейсы (порядок методов = vtable-слоты) ----------

[ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDXGIFactory1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIFactory
    void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
    // IDXGIFactory1
    [PreserveSig] int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
    void IsCurrent();
}

[ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDXGIAdapter1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIAdapter
    [PreserveSig] int EnumOutputs(uint Output, out IDXGIOutput ppOutput);
    void GetDesc(); void CheckInterfaceSupport();
    // IDXGIAdapter1
    [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 pDesc);
}

[ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDXGIOutput
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIOutput
    [PreserveSig] int GetDesc(out DxgiOutputDesc pDesc);
    void GetDisplayModeList(); void FindClosestMatchingMode(); void WaitForVBlank();
    void TakeOwnership(); void ReleaseOwnership(); void GetGammaControlCapabilities();
    void SetGammaControl(); void GetGammaControl(); void SetDisplaySurface(); void GetDisplaySurfaceData();
    void GetFrameStatistics();
}

[ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDXGIOutput1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIOutput
    void GetDesc(); void GetDisplayModeList(); void FindClosestMatchingMode(); void WaitForVBlank();
    void TakeOwnership(); void ReleaseOwnership(); void GetGammaControlCapabilities();
    void SetGammaControl(); void GetGammaControl(); void SetDisplaySurface(); void GetDisplaySurfaceData();
    void GetFrameStatistics();
    // IDXGIOutput1
    void GetDisplayModeList1(); void FindClosestMatchingMode1(); void GetDisplaySurfaceData1();
    [PreserveSig] int DuplicateOutput(IntPtr pDevice, out IDXGIOutputDuplication ppOutputDuplication);
}

[ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDXGIOutputDuplication
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIOutputDuplication
    void GetDesc();
    [PreserveSig] int AcquireNextFrame(uint TimeoutInMilliseconds, out DxgiOutduplFrameInfo pFrameInfo, out IntPtr ppDesktopResource);
    void GetFrameDirtyRects();
    void GetFrameMoveRects();
    [PreserveSig] int GetFramePointerShape(uint PointerShapeSize, IntPtr pPointerShapeBuffer, out uint pPointerShapeBufferSizeRequired, ref DxgiOutduplPointerShapeInfo pPointerShapeInfo);
    void MapDesktopSurface();
    void UnMapDesktopSurface();
    [PreserveSig] int ReleaseFrame();
}

[ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID3D11Device
{
    void CreateBuffer(); void CreateTexture1D();
    void CreateTexture2D(ref D3D11Texture2DDesc pDesc, IntPtr pInitialData, out ID3D11Texture2D ppTexture2D);
    void CreateTexture3D(); void CreateShaderResourceView(); void CreateUnorderedAccessView();
    void CreateRenderTargetView(); void CreateDepthStencilView(); void CreateInputLayout();
    void CreateVertexShader(); void CreateGeometryShader(); void CreateGeometryShaderWithStreamOutput();
    void CreatePixelShader(); void CreateHullShader(); void CreateDomainShader(); void CreateComputeShader();
    void CreateClassLinkage(); void CreateBlendState(); void CreateDepthStencilState();
    void CreateRasterizerState(); void CreateSamplerState(); void CreateQuery(); void CreatePredicate();
    void CreateCounter(); void CreateDeferredContext(); void OpenSharedResource(); void CheckFormatSupport();
    void CheckMultisampleQualityLevels(); void CheckCounterInfo(); void CheckCounter(); void CheckFeatureSupport();
    void GetPrivateData(); void SetPrivateData(); void SetPrivateDataInterface();
    void GetFeatureLevel(); void GetCreationFlags();
    [PreserveSig] int GetDeviceRemovedReason();
    void GetImmediateContext(out ID3D11DeviceContext ppImmediateContext);
    void SetExceptionMode(); void GetExceptionMode();
}

[ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID3D11DeviceContext
{
    // ID3D11DeviceChild
    void GetDevice(); void GetPrivateData(); void SetPrivateData(); void SetPrivateDataInterface();
    // ID3D11DeviceContext
    void VSSetConstantBuffers(); void PSSetShaderResources(); void PSSetShader(); void PSSetSamplers();
    void VSSetShader(); void DrawIndexed(); void Draw();
    [PreserveSig] int Map(IntPtr pResource, uint Subresource, uint MapType, uint MapFlags, out D3D11MappedSubresource pMappedResource);
    void Unmap(IntPtr pResource, uint Subresource);
    void PSSetConstantBuffers(); void IASetInputLayout(); void IASetVertexBuffers(); void IASetIndexBuffer();
    void DrawIndexedInstanced(); void DrawInstanced(); void GSSetConstantBuffers(); void GSSetShader();
    void IASetPrimitiveTopology(); void VSSetShaderResources(); void VSSetSamplers();
    void Begin(); void End(); void GetData(); void SetPredication(); void GSSetShaderResources(); void GSSetSamplers();
    void OMSetRenderTargets(); void OMSetRenderTargetsAndUnorderedAccessViews(); void OMSetBlendState();
    void OMSetDepthStencilState(); void SOSetTargets(); void DrawAuto(); void DrawIndexedInstancedIndirect();
    void DrawInstancedIndirect(); void Dispatch(); void DispatchIndirect(); void RSSetState(); void RSSetViewports();
    void RSSetScissorRects(); void CopySubresourceRegion();
    void CopyResource(IntPtr pDstResource, IntPtr pSrcResource);
    void UpdateSubresource(IntPtr pDstResource, uint DstSubresource, IntPtr pDstBox, IntPtr pSrcData, uint SrcRowPitch, uint SrcDepthPitch);
    void CopyStructureCount(); void ClearRenderTargetView(); void ClearUnorderedAccessViewUint();
    void ClearUnorderedAccessViewFloat(); void ClearDepthStencilView(); void GenerateMips(); void SetResourceMinLOD();
    void GetResourceMinLOD(); void ResolveSubresource(); void ExecuteCommandList();
    void HSSetShaderResources(); void HSSetShader(); void HSSetSamplers(); void HSSetConstantBuffers();
    void DSSetShaderResources(); void DSSetShader(); void DSSetSamplers(); void DSSetConstantBuffers();
    void CSSetShaderResources(); void CSSetUnorderedAccessViews(); void CSSetShader(); void CSSetSamplers(); void CSSetConstantBuffers();
    void VSGetConstantBuffers(); void PSGetShaderResources(); void PSGetShader(); void PSGetSamplers();
    void VSGetShader(); void PSGetConstantBuffers(); void IAGetInputLayout(); void IAGetVertexBuffers();
    void IAGetIndexBuffer(); void GSGetConstantBuffers(); void GSGetShader(); void IAGetPrimitiveTopology();
    void VSGetShaderResources(); void VSGetSamplers(); void GetPredication(); void GSGetShaderResources();
    void GSGetSamplers(); void OMGetRenderTargets(); void OMGetRenderTargetsAndUnorderedAccessViews();
    void OMGetBlendState(); void OMGetDepthStencilState(); void SOGetTargets(); void RSGetState();
    void RSGetViewports(); void RSGetScissorRects(); void HSGetShaderResources(); void HSGetShader();
    void HSGetSamplers(); void HSGetConstantBuffers(); void DSGetShaderResources(); void DSGetShader();
    void DSGetSamplers(); void DSGetConstantBuffers(); void CSGetShaderResources(); void CSGetUnorderedAccessViews();
    void CSGetShader(); void CSGetSamplers(); void CSGetConstantBuffers(); void ClearState(); void Flush();
    void GetType_(); void GetContextFlags(); void FinishCommandList();
}

[ComImport, Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID3D11Texture2D
{
    // ID3D11DeviceChild
    void GetDevice(); void GetPrivateData(); void SetPrivateData(); void SetPrivateDataInterface();
    // ID3D11Resource
    void GetType_(); void SetEvictionPriority(); void GetEvictionPriority();
    // ID3D11Texture2D
    void GetDesc(out D3D11Texture2DDesc pDesc);
}
