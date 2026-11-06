using System.Numerics;
using FlugelKranz.Compass;
using FlugelKranz.Core;
using FlugelKranz.OpenVR;
using SkiaSharp;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>When the compass overlay shows, what it sends to IVROverlay, and how it looks.</summary>
public class CompassOverlayTests
{
    private static readonly RigidPose Head = new(Quaternion.Identity, new(0, 1.6f, 0));
    private static readonly RigidPose LeftHand = new(Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.2f), new(-0.15f, 1.15f, -0.35f));

    private static InputFrame Frame(RigidPose head, RigidPose left, bool leftTracked = true) =>
        new(head, true, new(left, 0, 0, 0, leftTracked), new(new(Quaternion.Identity, new(0.2f, 1.1f, -0.3f)), 0, 0, 0, true));

    private static (CompassOverlay Overlay, FakeApi Api, DriverFlightRuntimeTests.ManualTime Time, Func<CompassSettings, CompassSettings> Set)
        Create(CompassSettings? initial = null, CountingRenderer? renderer = null)
    {
        var api = new FakeApi();
        var settings = initial ?? CompassSettings.Default;
        var time = new DriverFlightRuntimeTests.ManualTime();
        var options = new CompassOptions(() => settings, renderer ?? new CountingRenderer()) { Api = _ => api, RunThread = false };
        var overlay = new CompassOverlay(options, new object(), time);
        return (overlay, api, time, s => settings = s);
    }

    [Fact]
    public void ShowsOnlyWhileFlyingByDefaultOnTheRightController()
    {
        var (overlay, api, _, _) = Create();
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.Equal(CompassState.WaitingForFlight, overlay.Status.State);
        Assert.False(api.Visible);

        overlay.SetFlightActive(true);
        overlay.Tick();
        Assert.Equal(CompassState.Visible, overlay.Status.State);
        Assert.True(api.Visible);
        Assert.Equal(4u, api.Device);
        Assert.Equal(CompassHand.Right, overlay.Status.Hand);
        Assert.Equal(1, overlay.ImagesDrawn);
        Assert.InRange(api.Width, 0.0511f, 0.0512f); // 4.5 cm sphere on a quad 1/0.88 as wide

        overlay.SetFlightActive(false);
        overlay.Tick();
        Assert.False(api.Visible);
    }

    [Fact]
    public void HandSwitchAlwaysAndOffFollowTheSettingsLive()
    {
        var (overlay, api, time, set) = Create();
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        set(CompassSettings.Default with { Visibility = CompassVisibility.Always });
        overlay.Tick();
        Assert.True(api.Visible);
        Assert.Equal(4u, api.Device);
        Assert.Equal(1, overlay.ImagesDrawn);

        // Flipping hands mid-session moves it on the next update, with an image for the new view.
        set(CompassSettings.Default with { Visibility = CompassVisibility.Always, Hand = CompassHand.Left });
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Tick();
        Assert.True(api.Visible);
        Assert.Equal(3u, api.Device);
        Assert.Equal(CompassHand.Left, overlay.Status.Hand);
        Assert.Equal(2, overlay.ImagesDrawn);
        set(CompassSettings.Default with { Visibility = CompassVisibility.Always });
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Tick();
        Assert.Equal(4u, api.Device);
        Assert.Equal(3, overlay.ImagesDrawn);

        set(CompassSettings.Default with { Enabled = false });
        overlay.Tick();
        Assert.False(api.Visible);
        Assert.Equal(CompassState.Off, overlay.Status.State);
    }

    [Fact]
    public void HidesWhileTheControllerIsNotTracked()
    {
        var (overlay, api, _, _) = Create(CompassSettings.Default with { Visibility = CompassVisibility.Always, Hand = CompassHand.Left });
        overlay.Publish(Frame(Head, LeftHand, leftTracked: false), 3, 4);
        overlay.Tick();
        Assert.Equal(CompassState.WaitingForTracking, overlay.Status.State);
        overlay.Publish(Frame(Head, LeftHand), uint.MaxValue, 4);
        overlay.Tick();
        Assert.Equal(CompassState.WaitingForTracking, overlay.Status.State);
        Assert.False(api.Visible);
    }

    [Fact]
    public void RedrawsOnlyWhenTheViewTurnsAndAtMost30TimesASecond()
    {
        var (overlay, api, time, _) = Create(CompassSettings.Default with { Visibility = CompassVisibility.Always, Hand = CompassHand.Left });
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.Equal(1, overlay.ImagesDrawn);

        // Turning the wrist about the sphere moves the quad on the hand but not the image.
        var centre = LeftHand.Transform(CompassMath.LeftHandOffset);
        for (int i = 1; i <= 60; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * 0.02f) * LeftHand.Orientation;
            var turned = new RigidPose(orientation, centre - Vector3.Transform(CompassMath.LeftHandOffset, orientation));
            overlay.Publish(Frame(Head, turned), 3, 4);
            overlay.Tick();
        }
        Assert.Equal(1, overlay.ImagesDrawn);
        Assert.True(api.Transforms > 30, $"{api.Transforms} transform updates");

        // Flight turning the whole world does not redraw either: the image is the physical view.
        for (int i = 1; i <= 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            overlay.PublishFlight(new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, i * 0.1f), new(i, 0, 0)));
            overlay.Tick();
        }
        Assert.Equal(1, overlay.ImagesDrawn);

        // Moving the head around the sphere does, capped at one image per 33 ms.
        for (int i = 1; i <= 66; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            var head = Head with { Position = Head.Position + new Vector3(i * 0.01f, 0, 0) };
            overlay.Publish(Frame(head, LeftHand), 3, 4);
            overlay.Tick();
        }
        Assert.InRange(overlay.ImagesDrawn, 15, 31);
    }

    [Fact]
    public void HeldImageStaysPhysicallyCorrectBetweenRedraws()
    {
        var renderer = new CountingRenderer();
        var (overlay, api, time, _) = Create(CompassSettings.Default with { Visibility = CompassVisibility.Always, Hand = CompassHand.Left }, renderer);
        var flight = new RigidPose(Quaternion.CreateFromYawPitchRoll(0.4f, 1.1f, -0.7f), new(3, 4, 5));
        overlay.PublishFlight(flight);
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        var drawn = renderer.LastAxes;
        var turned = LeftHand with { Orientation = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.1f) * LeftHand.Orientation };
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Publish(Frame(Head, turned), 3, 4);
        overlay.Tick();
        // The image on the quad, placed on the flown hand, still shows real down as physical -Y.
        var quad = flight * turned * api.Transform;
        var down = Vector3.Transform(drawn.Down, quad.Orientation);
        Assert.True(Vector3.Distance(Vector3.Transform(-Vector3.UnitY, flight.Orientation), down) < 1e-3f);
    }

    [Fact]
    public void MissingOverlayInterfaceLeavesFlightRunningAndSaysWhy()
    {
        var api = new FakeApi { CreateError = "IVROverlay is not available." };
        var session = new FakeSession();
        var options = new CompassOptions(() => CompassSettings.Default, new CountingRenderer()) { Api = _ => api, RunThread = false };
        using var runtime = new DriverFlightRuntime(session, () => new FakeDriver(), null, options);
        runtime.SetFlightActive(true);
        runtime.ReadPhysical();
        runtime.Apply(new(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), Vector3.Zero));
        runtime.Compass!.Tick();
        Assert.Equal(CompassState.Unavailable, runtime.CompassStatus!.State);
        Assert.Equal("IVROverlay is not available.", runtime.CompassStatus.Detail);
        runtime.ReadPhysical();
        runtime.Compass.Tick();
        Assert.Equal(1, api.Creates);
    }

    [Fact]
    public void RuntimeFeedsPhysicalPosesAndTheFlightToTheCompass()
    {
        var api = new FakeApi();
        var session = new FakeSession();
        var options = new CompassOptions(() => CompassSettings.Default, new CountingRenderer()) { Api = _ => api, RunThread = false };
        var runtime = new DriverFlightRuntime(session, () => new FakeDriver(), null, options);
        runtime.SetFlightActive(true);
        var flight = new RigidPose(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new(1, 2, 3));
        runtime.Apply(flight);
        runtime.ReadPhysical();
        runtime.Compass!.Tick();
        Assert.True(api.Visible);
        Assert.Equal(2u, api.Device); // right controller by default
        runtime.Dispose();
        Assert.True(api.Disposed);
    }

    [Fact]
    public void RendererDrawsAGlowingGravityArrowBelowTheCentreForTheIdentityView()
    {
        using var renderer = new CompassRenderer();
        var view = CompassMath.Compute(RigidPose.Identity, Head with { Position = new(0, 1.6f, 0.3f) },
            new(Quaternion.Identity, new(0, 1.55f, -0.1f)), Vector3.Zero);
        var pixels = new byte[renderer.Size * renderer.Size * 4];
        renderer.Render(view.Axes, CompassGlow.Teal, pixels);
        int size = renderer.Size;
        (byte R, byte G, byte B, byte A) At(int x, int y) { int i = (y * size + x) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]); }
        // Corners are clear; the sphere fill is faint.
        Assert.Equal(0, At(2, 2).A);
        Assert.InRange(At(size / 2 + 40, size / 2 - 60).A, 1, 80);
        // The gravity arrow runs straight down from the centre in teal.
        var arrow = At(size / 2, size / 2 + 60);
        Assert.True(arrow.A > 180 && arrow.G > arrow.R + 40, $"arrow pixel {arrow}");
        // The glow spills a little beside the stroke.
        Assert.True(At(size / 2 + 9, size / 2 + 60).A > At(size / 2 + 30, size / 2 + 60).A);
    }

    /// <summary>
    /// Saves the compass for identity, rolled 90 degrees and pitched 45 degrees flight when
    /// FLUGELKRANZ_COMPASS_PNG names a folder. Each case writes the overlay texture and a mock of
    /// the flown view (the flown world's horizon behind it) on a dark background.
    /// </summary>
    [Fact]
    public void RenderPreviewImagesWhenAsked()
    {
        string? folder = Environment.GetEnvironmentVariable("FLUGELKRANZ_COMPASS_PNG");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var renderer = new CompassRenderer();
        // Looking down at the left hand from slightly behind, head level.
        var head = new RigidPose(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f), new(0, 1.6f, 0));
        var hand = new RigidPose(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), new(-0.05f, 1.15f, -0.38f));
        var cases = new (string Name, RigidPose Flight, CompassGlow Glow)[]
        {
            ("identity", RigidPose.Identity, CompassGlow.Teal),
            ("roll90", new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new(0, 3, 0)), CompassGlow.Teal),
            ("pitch45", new(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4), new(0, 0, -2)), CompassGlow.Teal),
            ("identity-lime", RigidPose.Identity, CompassGlow.Lime),
            ("identity-amber", RigidPose.Identity, CompassGlow.Amber)
        };
        foreach (var (name, flight, glow) in cases)
        {
            var view = CompassMath.Compute(flight, head, hand, CompassMath.LeftHandOffset);
            File.WriteAllBytes(Path.Combine(folder, $"compass-{name}.png"), renderer.RenderPng(view.Axes, glow));
            if (name.Contains('-')) continue;
            File.WriteAllBytes(Path.Combine(folder, $"compass-{name}-in-view.png"), InView(renderer, flight, head, view, glow));
        }
    }

    /// <summary>The texture over a dark scene whose horizon is the flown world's, as seen from the physical head.</summary>
    private static byte[] InView(CompassRenderer renderer, RigidPose flight, RigidPose head, CompassView view, CompassGlow glow)
    {
        const int width = 640, height = 480;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        // The flown world's up, in the eye's frame: (D * H)^-1 applied to +Y.
        var worldUp = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(flight.Orientation * head.Orientation));
        float angle = MathF.Atan2(worldUp.X, worldUp.Y) * 180 / MathF.PI;
        canvas.Clear(new SKColor(0x0b, 0x0e, 0x14));
        canvas.Save();
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(angle);
        // Pinhole view (focal 400 px): the horizon sits uz / |(ux, uy)| above the centre along world up.
        float sideways = MathF.Max(1e-3f, MathF.Sqrt(worldUp.X * worldUp.X + worldUp.Y * worldUp.Y));
        float horizon = Math.Clamp(-worldUp.Z / sideways * 400, -900, 900);
        using (var sky = new SKPaint { Color = new SKColor(0x1b, 0x22, 0x33) })
            canvas.DrawRect(new SKRect(-1000, -2000, 1000, horizon), sky);
        using (var ground = new SKPaint { Color = new SKColor(0x12, 0x17, 0x12) })
            canvas.DrawRect(new SKRect(-1000, horizon, 1000, 2000), ground);
        using (var line = new SKPaint { Color = new SKColor(0x60, 0x70, 0x88), StrokeWidth = 2, IsAntialias = true })
            canvas.DrawLine(-1000, horizon, 1000, horizon, line);
        canvas.Restore();
        using var texture = SKImage.FromEncodedData(renderer.RenderPng(view.Axes, glow));
        canvas.DrawImage(texture, new SKRect(width / 2f - 128, height / 2f - 40, width / 2f + 128, height / 2f + 216));
        using var label = new SKPaint { Color = new SKColor(0x90, 0x98, 0xa8), IsAntialias = true };
        using var font = new SKFont { Size = 15 };
        canvas.DrawText("flown world horizon (game) vs compass (real)", 12, 22, font, label);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class CountingRenderer : ICompassRenderer
    {
        public CompassAxes LastAxes;
        public int Size => 8;
        public void Render(CompassAxes axes, CompassGlow glow, Span<byte> rgba) { LastAxes = axes; rgba.Fill(1); }
    }

    private sealed class FakeApi : ICompassOverlayApi
    {
        public string? CreateError;
        public int Creates, Transforms;
        public bool Visible, Disposed;
        public uint Device = uint.MaxValue;
        public float Width;
        public RigidPose Transform;
        public string? Create() { Creates++; return CreateError; }
        public void SetWidth(float metres) => Width = metres;
        public void SetTransform(uint device, RigidPose overlayInDevice) { Device = device; Transform = overlayInDevice; Transforms++; }
        public void SetImage(byte[] straightRgba, int size) { }
        public void Show() => Visible = true;
        public void Hide() => Visible = false;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDriver : IDriverConnection
    {
        public DriverSample Read(uint device) => device == 0
            ? new(new(Quaternion.Identity, new(0, 1.6f, 0)), true)
            : new(new(Quaternion.Identity, new(-0.15f, 1.15f, -0.35f)), true);
        public void Refresh() { }
        public void Apply(RigidPose pose) { }
        public void Dispose() { }
    }

    private sealed class FakeSession : IOpenVrSession
    {
        public RigidPose ReadWorkingStanding() => RigidPose.Identity;
        public InputFrame ReadRaw() => new(RigidPose.Identity, true, new(RigidPose.Identity, 0, 0, 0, true), new(RigidPose.Identity, 0, 0, 0, true));
        public (uint Left, uint Right) ControllerDevices() => (1, 2);
        public void PreviewStanding(RigidPose pose) { }
        public void HidePreview() { }
        public void Dispose() { }
    }
}
