using System.Diagnostics;
using FlugelKranz.OpenVR;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>
/// The compass's D3D11 texture on this machine's GPU (or WARP where there is none): the raw COM
/// vtable slots are right if what goes in comes back out, and devices are released.
/// </summary>
public class D3D11OverlayTextureTests
{
    private const int Width = 512, Height = 256;

    private static D3D11OverlayTexture Create(int adapter = 0)
    {
        try { return D3D11OverlayTexture.Create(adapter, Width, Height); }
        catch (InvalidOperationException) { return D3D11OverlayTexture.Create(-1, Width, Height, D3D11OverlayTexture.DriverTypeWarp); }
    }

    private static byte[] Pattern(int seed)
    {
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                pixels[i] = (byte)(x + seed);
                pixels[i + 1] = (byte)(y * 3 + seed);
                pixels[i + 2] = (byte)(x ^ y);
                pixels[i + 3] = (byte)(255 - seed);
            }
        return pixels;
    }

    [Fact]
    public void UploadsReachTheReusedTexture()
    {
        using var texture = Create();
        Assert.NotEqual(0, texture.Handle);
        nint handle = texture.Handle;
        foreach (int seed in new[] { 0, 77, 200 })
        {
            var pixels = Pattern(seed);
            texture.Upload(pixels);
            texture.Flush();
            Assert.Equal(pixels, texture.ReadBack());
        }
        // Same texture every time: nothing is created per image.
        Assert.Equal(handle, texture.Handle);
        Assert.Equal(0, texture.DeviceRemovedReason());
    }

    [Fact]
    public void WarpDeviceWorksToo()
    {
        using var texture = D3D11OverlayTexture.Create(-1, Width, Height, D3D11OverlayTexture.DriverTypeWarp);
        var pixels = Pattern(9);
        texture.Upload(pixels);
        Assert.Equal(pixels, texture.ReadBack());
    }

    [Fact]
    public void AnAdapterThatDoesNotExistFallsBackToTheDefaultOne()
    {
        using var texture = Create(adapter: 64);
        Assert.False(texture.OnRequestedAdapter);
        var pixels = Pattern(3);
        texture.Upload(pixels);
        Assert.Equal(pixels, texture.ReadBack());
    }

    [Fact]
    public void ReleasedDevicesDoNotPileUp()
    {
        // Recreating after a lost device or a SteamVR restart must not leak handles.
        var pixels = Pattern(1);
        for (int i = 0; i < 5; i++) { using var warmup = Create(); warmup.Upload(pixels); warmup.Flush(); }
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        int handlesBefore = process.HandleCount;
        for (int i = 0; i < 40; i++)
        {
            using var texture = Create();
            texture.Upload(pixels);
            texture.Flush();
        }
        process.Refresh();
        Assert.InRange(process.HandleCount - handlesBefore, -1000, 40);
        var disposed = Create();
        disposed.Dispose();
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.Upload(pixels));
    }

    [Fact]
    public void ShortBuffersAreRejected()
    {
        using var texture = Create();
        Assert.Throws<ArgumentException>(() => texture.Upload(new byte[16]));
    }
}
