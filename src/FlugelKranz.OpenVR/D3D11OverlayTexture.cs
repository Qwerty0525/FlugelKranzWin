using System.Runtime.InteropServices;

namespace FlugelKranz.OpenVR;

/// <summary>
/// One reusable D3D11 texture (R8G8B8A8_UNORM) for an OpenVR overlay, driven through raw COM
/// vtable calls on d3d11.dll / dxgi.dll. Only six methods are needed (create device, create
/// texture, UpdateSubresource, Flush, GetDeviceRemovedReason, Release), so a wrapper package
/// (Vortice, SharpDX, TerraFX) would add several assemblies for very little. Slot numbers are
/// the declaration order in the Windows SDK's d3d11.h / dxgi.h and never change for a COM
/// interface; <c>D3D11OverlayTextureTests</c> checks them on real hardware by reading the
/// texture back through a staging copy.
/// The device, its immediate context and the texture are created once and reused for every
/// image; <see cref="Dispose"/> releases all three. Not thread-safe: one thread uploads.
/// </summary>
internal sealed unsafe class D3D11OverlayTexture : IDisposable
{
    private const uint SdkVersion = 7;
    private const int DriverTypeUnknown = 0, DriverTypeHardware = 1;
    internal const int DriverTypeWarp = 5;
    private const uint FormatR8G8B8A8Unorm = 28;
    private const uint UsageDefault = 0, UsageStaging = 3;
    private const uint BindShaderResource = 0x8, BindRenderTarget = 0x20;
    private const uint CpuAccessRead = 0x20000;
    private const uint MapRead = 1;

    // IUnknown
    private const int SlotRelease = 2;
    // ID3D11Device
    private const int SlotCreateTexture2D = 5, SlotGetDeviceRemovedReason = 39;
    // ID3D11DeviceContext
    private const int SlotMap = 14, SlotUnmap = 15, SlotCopyResource = 47, SlotUpdateSubresource = 48, SlotFlush = 111;
    // IDXGIFactory1
    private const int SlotEnumAdapters1 = 12;
    private static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public nint Data;
        public uint RowPitch, DepthPitch;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    private nint device, context, texture;

    private D3D11OverlayTexture(nint device, nint context, nint texture, int width, int height, bool onAdapter)
    {
        this.device = device;
        this.context = context;
        this.texture = texture;
        Width = width;
        Height = height;
        OnRequestedAdapter = onAdapter;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>The ID3D11Texture2D pointer for <c>Texture_t.handle</c>.</summary>
    public nint Handle => texture;
    /// <summary>False when the adapter SteamVR named could not be opened and the default adapter was used.</summary>
    public bool OnRequestedAdapter { get; }

    /// <summary>
    /// Creates the device on DXGI adapter <paramref name="adapterIndex"/> (from
    /// <c>IVRSystem::GetDXGIOutputInfo</c>, so the texture lives on the GPU the compositor uses),
    /// falling back to the default hardware adapter. <paramref name="driverType"/> is for tests (WARP).
    /// </summary>
    public static D3D11OverlayTexture Create(int adapterIndex, int width, int height, int? driverType = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        nint adapter = 0;
        bool onAdapter = false;
        if (driverType is null && adapterIndex >= 0) adapter = OpenAdapter(adapterIndex);
        int hr;
        nint device, context;
        try
        {
            hr = D3D11CreateDevice(adapter, adapter != 0 ? DriverTypeUnknown : driverType ?? DriverTypeHardware, 0, 0, 0, 0,
                SdkVersion, out device, out _, out context);
            onAdapter = adapter != 0 && hr >= 0;
            if (hr < 0 && adapter != 0)
                hr = D3D11CreateDevice(0, DriverTypeHardware, 0, 0, 0, 0, SdkVersion, out device, out _, out context);
        }
        finally { if (adapter != 0) Release(adapter); }
        if (hr < 0) throw new InvalidOperationException($"D3D11CreateDevice: 0x{hr:X8}");

        var description = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = FormatR8G8B8A8Unorm,
            SampleCount = 1, Usage = UsageDefault, BindFlags = BindShaderResource | BindRenderTarget
        };
        nint texture;
        hr = CreateTexture2D(device, &description, &texture);
        if (hr < 0)
        {
            Release(context);
            Release(device);
            throw new InvalidOperationException($"CreateTexture2D: 0x{hr:X8}");
        }
        return new(device, context, texture, width, height, onAdapter);
    }

    private static nint OpenAdapter(int index)
    {
        if (CreateDXGIFactory1(DxgiFactory1, out var factory) < 0) return 0;
        try
        {
            nint adapter;
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtable(factory)[SlotEnumAdapters1];
            return enumAdapters(factory, (uint)index, &adapter) >= 0 ? adapter : 0;
        }
        finally { Release(factory); }
    }

    /// <summary>Copies one tightly packed RGBA image (Width × Height × 4 bytes) into the texture.</summary>
    public void Upload(ReadOnlySpan<byte> rgba)
    {
        ObjectDisposedException.ThrowIf(texture == 0, this);
        if (rgba.Length < Width * Height * 4) throw new ArgumentException("Buffer is smaller than the texture.", nameof(rgba));
        var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)Vtable(context)[SlotUpdateSubresource];
        fixed (byte* pixels = rgba)
            update(context, texture, 0, null, pixels, (uint)(Width * 4), (uint)(Width * Height * 4));
    }

    /// <summary>Submits queued GPU work (the upload and SteamVR's copy of the texture).</summary>
    public void Flush()
    {
        if (context == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, void>)Vtable(context)[SlotFlush])(context);
    }

    /// <summary>S_OK (0) while the device works; a DXGI_ERROR_DEVICE_* code once it is lost (driver reset, GPU removed).</summary>
    public int DeviceRemovedReason() =>
        device == 0 ? 0 : ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(device)[SlotGetDeviceRemovedReason])(device);

    /// <summary>Reads the texture back through a staging copy (tests only).</summary>
    internal byte[] ReadBack()
    {
        var description = new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = FormatR8G8B8A8Unorm,
            SampleCount = 1, Usage = UsageStaging, CpuAccessFlags = CpuAccessRead
        };
        nint staging;
        int hr = CreateTexture2D(device, &description, &staging);
        if (hr < 0) throw new InvalidOperationException($"CreateTexture2D (staging): 0x{hr:X8}");
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Vtable(context)[SlotCopyResource])(context, staging, texture);
            MappedSubresource mapped;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Vtable(context)[SlotMap])(
                context, staging, 0, MapRead, 0, &mapped);
            if (hr < 0) throw new InvalidOperationException($"Map: 0x{hr:X8}");
            var result = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
                new ReadOnlySpan<byte>((byte*)mapped.Data + y * mapped.RowPitch, Width * 4).CopyTo(result.AsSpan(y * Width * 4));
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Vtable(context)[SlotUnmap])(context, staging, 0);
            return result;
        }
        finally { Release(staging); }
    }

    private static int CreateTexture2D(nint device, Texture2DDescription* description, nint* texture) =>
        ((delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, void*, nint*, int>)Vtable(device)[SlotCreateTexture2D])(
            device, description, null, texture);

    private static void** Vtable(nint instance) => *(void***)instance;

    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(instance)[SlotRelease])(instance);
    }

    public void Dispose()
    {
        if (texture == 0) return;
        Release(texture);
        Release(context);
        Release(device);
        texture = context = device = 0;
    }
}
