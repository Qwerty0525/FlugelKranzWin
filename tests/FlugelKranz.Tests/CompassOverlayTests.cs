using System.Diagnostics;
using System.Globalization;
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
        Create(CompassSettings? initial = null, CountingRenderer? renderer = null, FakeApi? api = null)
    {
        api ??= new FakeApi();
        var settings = initial ?? CompassSettings.Default;
        var time = new DriverFlightRuntimeTests.ManualTime();
        var fake = api;
        var options = new CompassOptions(() => settings, renderer ?? new CountingRenderer()) { Api = _ => fake, RunThread = false };
        var overlay = new CompassOverlay(options, new object(), time);
        return (overlay, api, time, s => settings = s);
    }

    private static CompassSettings Always(CompassHand hand = CompassHand.Left) =>
        CompassSettings.Default with { Visibility = CompassVisibility.Always, Hand = hand };

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
        Assert.True(api.Stereo);
        Assert.Equal(4u, api.Device);
        Assert.Equal(CompassHand.Right, overlay.Status.Hand);
        Assert.Equal(1, overlay.ImagesDrawn);
        Assert.InRange(api.Width, 0.0511f, 0.0512f); // 4.5 cm sphere on a quad 1/0.88 as wide
        // Side by side: both eyes' halves in one texture twice as wide as it is tall.
        Assert.Equal((16, 8), api.ImageSize);

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
        // A size change redraws at once too (the eyes are measured in sphere radii).
        set(CompassSettings.Default with { Visibility = CompassVisibility.Always, SizeCentimetres = 6 });
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Tick();
        Assert.Equal(4, overlay.ImagesDrawn);

        set(CompassSettings.Default with { Enabled = false });
        overlay.Tick();
        Assert.False(api.Visible);
        Assert.Equal(CompassState.Off, overlay.Status.State);
    }

    [Fact]
    public void HidesWhileTheControllerIsNotTracked()
    {
        var (overlay, api, _, _) = Create(Always());
        overlay.Publish(Frame(Head, LeftHand, leftTracked: false), 3, 4);
        overlay.Tick();
        Assert.Equal(CompassState.WaitingForTracking, overlay.Status.State);
        overlay.Publish(Frame(Head, LeftHand), uint.MaxValue, 4);
        overlay.Tick();
        Assert.Equal(CompassState.WaitingForTracking, overlay.Status.State);
        Assert.False(api.Visible);
    }

    [Fact]
    public void RedrawsOnlyWhenTheViewChangesAndAtMostEvery30Milliseconds()
    {
        var (overlay, api, time, _) = Create(Always());
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.Equal(1, overlay.ImagesDrawn);

        // A still view sends nothing.
        for (int i = 1; i <= 20; i++) { time.Advance(TimeSpan.FromMilliseconds(15)); overlay.Tick(); }
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

        // Moving the head around the sphere does, capped at one image per 30 ms (every other 15 ms tick).
        for (int i = 1; i <= 66; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            var head = Head with { Position = Head.Position + new Vector3(i * 0.01f, 0, 0) };
            overlay.Publish(Frame(head, LeftHand), 3, 4);
            overlay.Tick();
        }
        Assert.InRange(overlay.ImagesDrawn - 1, 30, 34);
    }

    [Fact]
    public void EachImageIsDrawnFromBothEyesAsSteamVrReportsThem()
    {
        var renderer = new CountingRenderer();
        var api = new FakeApi { Eyes = (new(-0.030f, 0, 0.01f), new(0.030f, 0, 0.01f)) };
        var (overlay, _, time, _) = Create(Always(), renderer, api);
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        float radius = CompassSettings.Default.SizeCentimetres / 200;
        Assert.InRange(Vector3.Distance(renderer.LastEyes.Left, renderer.LastEyes.Right) * radius, 0.0599f, 0.0601f);
        // The left eye is on the image's left, both in front of the quad.
        Assert.True(renderer.LastEyes.Left.X < renderer.LastEyes.Right.X);
        Assert.True(renderer.LastEyes.Left.Z > 5 && renderer.LastEyes.Right.Z > 5);
        Assert.Equal(1, api.EyeReads);

        // Read again every 2 s; nonsense from SteamVR keeps the last good eyes.
        api.Eyes = (Vector3.Zero, Vector3.Zero);
        for (int i = 0; i < 140; i++) { time.Advance(TimeSpan.FromMilliseconds(15)); overlay.Tick(); }
        Assert.Equal(2, api.EyeReads);
        overlay.Publish(Frame(Head with { Position = Head.Position + new Vector3(0.05f, 0, 0) }, LeftHand), 3, 4);
        time.Advance(TimeSpan.FromMilliseconds(30));
        overlay.Tick();
        Assert.InRange(Vector3.Distance(renderer.LastEyes.Left, renderer.LastEyes.Right) * radius, 0.0599f, 0.0601f);
    }

    [Fact]
    public void MissingEyeOffsetsFallBackToA64MillimetreIpd()
    {
        var renderer = new CountingRenderer();
        var api = new FakeApi { FailEyes = true };
        var (overlay, _, _, _) = Create(Always(), renderer, api);
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.True(api.Visible);
        float radius = CompassSettings.Default.SizeCentimetres / 200;
        Assert.InRange(Vector3.Distance(renderer.LastEyes.Left, renderer.LastEyes.Right) * radius, 0.0639f, 0.0641f);
    }

    [Fact]
    public void RefusedImagesKeepTheCompassAndRetryLater()
    {
        var (overlay, api, time, _) = Create(CompassSettings.Default with { Visibility = CompassVisibility.Always });
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.True(api.Visible);
        api.RefuseImages = 3;
        int calls = api.ImageCalls;
        for (int i = 1; i <= 200; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            var head = Head with { Position = Head.Position + new Vector3(i * 0.01f, 0, 0) };
            overlay.Publish(Frame(head, LeftHand), 3, 4);
            overlay.Tick();
            Assert.True(api.Visible, $"still shown at tick {i}");
            Assert.Equal(CompassState.Visible, overlay.Status.State);
        }
        Assert.Equal(3, overlay.Refusals);
        Assert.Equal(3, overlay.Status.Refusals);
        Assert.Contains("SetOverlayTexture", overlay.Status.LastRefusal);
        Assert.True(overlay.ImagesDrawn >= 3, "images resume after the retry delay");
        // 3 s at one image per 30 ms would be 100 calls; 250 + 500 + 1000 ms of backoff skip about 58.
        Assert.InRange(api.ImageCalls - calls, 30, 60);
    }

    [Fact]
    public void FirstImageRefusedShowsNothingUntilOneArrives()
    {
        var (overlay, api, time, _) = Create(CompassSettings.Default with { Visibility = CompassVisibility.Always });
        api.RefuseImages = 1;
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.False(api.Visible);
        Assert.Equal(CompassState.WaitingForSteamVr, overlay.Status.State);
        Assert.Equal(1, overlay.Status.Refusals);
        time.Advance(TimeSpan.FromMilliseconds(300));
        overlay.Tick();
        Assert.True(api.Visible);
        Assert.Equal(CompassState.Visible, overlay.Status.State);
    }

    [Fact]
    public void RefusedStereoFlagShowsNothingAndIsRetriedWithBackoff()
    {
        // Without the side-by-side flag both halves would show next to each other.
        var (overlay, api, time, _) = Create(Always());
        api.RefuseStereo = 3;
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.Equal(CompassState.WaitingForSteamVr, overlay.Status.State);
        Assert.Contains("SideBySide", overlay.Status.LastRefusal);
        Assert.False(api.Visible);
        Assert.Equal(0, api.ImageCalls);
        // 250 + 500 + 1000 ms of backoff, then it goes through.
        int ticks = 0;
        while (!api.Visible && ticks < 300) { time.Advance(TimeSpan.FromMilliseconds(15)); overlay.Tick(); ticks++; }
        Assert.True(api.Visible);
        Assert.Equal(4, api.StereoCalls);
        Assert.InRange(ticks * 15, 1750, 1800);
        Assert.Equal(CompassState.Visible, overlay.Status.State);
        Assert.Equal(3, overlay.Status.Refusals);
    }

    [Fact]
    public void ShortSteamVrErrorsDoNotDisableTheCompassButLastingOnesDo()
    {
        var (overlay, api, time, _) = Create(Always());
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        api.FailTransforms = 10;
        for (int i = 0; i < 12; i++) { overlay.Tick(); time.Advance(TimeSpan.FromMilliseconds(15)); }
        Assert.Equal(CompassState.Visible, overlay.Status.State);
        Assert.True(api.Visible);

        api.FailTransforms = int.MaxValue;
        // Turning the wrist changes the transform on the hand, so every update tries (and fails) to send it.
        overlay.Publish(Frame(Head, LeftHand with { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f) * LeftHand.Orientation }), 3, 4);
        for (int i = 0; i < 250; i++) { overlay.Tick(); time.Advance(TimeSpan.FromMilliseconds(15)); }
        Assert.Equal(CompassState.Unavailable, overlay.Status.State);
        Assert.Contains("RequestFailed", overlay.Status.Detail);
    }

    [Fact]
    public void StatusReportsImageRateDrawTimeAndOpenVrLockTime()
    {
        var api = new FakeApi { Held = TimeSpan.FromMilliseconds(0.4) };
        var (overlay, _, time, _) = Create(Always(), api: api);
        for (int i = 1; i <= 140; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            overlay.Publish(Frame(Head with { Position = Head.Position + new Vector3(i * 0.005f, 0, 0) }, LeftHand), 3, 4);
            overlay.Tick();
        }
        Assert.InRange(overlay.Status.ImagesPerSecond, 30, 35);
        Assert.InRange(overlay.Status.SteamVrMilliseconds, 0.39f, 0.41f);
        Assert.InRange(overlay.Status.SteamVrMaxMilliseconds, 0.39f, 0.41f);
        Assert.Equal(0, overlay.Status.DrawMilliseconds); // the manual clock does not move while drawing
    }

    [Fact]
    public void HeldImageStaysPhysicallyCorrectBetweenRedraws()
    {
        var renderer = new CountingRenderer();
        var (overlay, api, time, _) = Create(Always(), renderer);
        var flight = new RigidPose(Quaternion.CreateFromYawPitchRoll(0.4f, 1.1f, -0.7f), new(3, 4, 5));
        overlay.PublishFlight(flight);
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        var drawn = renderer.LastAxes;
        var turned = LeftHand with { Orientation = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.1f) * LeftHand.Orientation };
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Publish(Frame(Head, turned), 3, 4);
        overlay.Tick();
        Assert.Equal(1, overlay.ImagesDrawn);
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

    private static CompassEyes EyesAt(float distance) =>
        new(new(-0.032f / 0.0225f, 0, distance / 0.0225f), new(0.032f / 0.0225f, 0, distance / 0.0225f));

    [Fact]
    public void RendererDrawsAGlowingGravityArrowBelowTheCentreInBothHalves()
    {
        using var renderer = new CompassRenderer();
        var view = CompassMath.Compute(RigidPose.Identity, Head with { Position = new(0, 1.6f, 0.3f) },
            new(Quaternion.Identity, new(0, 1.55f, -0.1f)), Vector3.Zero);
        var pixels = new byte[renderer.Size * renderer.Size * 8];
        renderer.Render(view.Axes, CompassGlow.Teal, view.Eyes, pixels);
        int size = renderer.Size;
        foreach (int half in new[] { 0, size })
        {
            (byte R, byte G, byte B, byte A) At(int x, int y) { int i = (y * 2 * size + half + x) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]); }
            // Corners are clear; the sphere fill is faint.
            Assert.Equal(0, At(2, 2).A);
            Assert.InRange(At(size / 2 + 40, size / 2 - 60).A, 1, 80);
            // The gravity arrow runs straight down from the centre in teal.
            var arrow = At(size / 2, size / 2 + 60);
            Assert.True(arrow.A > 180 && arrow.G > arrow.R + 40, $"arrow pixel {arrow} in half {half}");
            // The glow spills a little beside the stroke.
            Assert.True(At(size / 2 + 9, size / 2 + 60).A > At(size / 2 + 30, size / 2 + 60).A);
            // Nothing from one eye's view bleeds into the other half.
            Assert.Equal(0, At(0, size / 2).A);
            Assert.Equal(0, At(size - 1, size / 2).A);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrowTowardTheViewerHasCrossedDisparityAndAwayUncrossed(bool toward)
    {
        using var renderer = new CompassRenderer();
        int size = renderer.Size;
        var forward = Vector3.Normalize(new(0.6f, 0.2f, toward ? 0.75f : -0.75f));
        var axes = new CompassAxes(-Vector3.UnitY, forward, Vector3.Normalize(Vector3.Cross(-Vector3.UnitY, forward)));
        var pixels = new byte[size * size * 8];
        renderer.Render(axes, CompassGlow.Teal, EyesAt(0.4f), pixels);
        // Centroid of the blue forward arrow in each half.
        float Centroid(int half)
        {
            double sum = 0; int count = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = (y * 2 * size + half + x) * 4;
                    // Blue enough to be the forward arrow (dimmed to about 70 / 255 on the far half).
                    if (pixels[i + 3] > 40 && pixels[i + 2] > pixels[i] + 60 && pixels[i + 2] > pixels[i + 1] + 30) { sum += x; count++; }
                }
            Assert.True(count > 40, $"forward arrow found in half {half}: {count} px");
            return (float)(sum / count);
        }
        float disparity = Centroid(0) - Centroid(size);
        if (toward) Assert.True(disparity > 3, $"toward the viewer: left - right = {disparity} px");
        else Assert.True(disparity < -3, $"away from the viewer: left - right = {disparity} px");
    }

    /// <summary>
    /// Saves the stereo texture for identity, rolled 90 degrees and pitched 45 degrees flight when
    /// FLUGELKRANZ_COMPASS_PNG names a folder: the texture itself (transparent, left eye on the left)
    /// and a copy on a dark background. Also writes timing.txt with the draw and GPU upload cost.
    /// </summary>
    [Fact]
    public void RenderPreviewImagesWhenAsked()
    {
        string? folder = Environment.GetEnvironmentVariable("FLUGELKRANZ_COMPASS_PNG");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var renderer = new CompassRenderer();
        // Looking down at the right hand from slightly behind, head level.
        var head = new RigidPose(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f), new(0, 1.6f, 0));
        var hand = new RigidPose(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), new(0.05f, 1.15f, -0.38f));
        var offset = CompassMath.HandOffset(CompassHand.Right);
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
            var view = CompassMath.Compute(flight, head, hand, offset);
            var png = renderer.RenderPng(view.Axes, glow, view.Eyes);
            File.WriteAllBytes(Path.Combine(folder, $"compass-stereo-{name}.png"), png);
            File.WriteAllBytes(Path.Combine(folder, $"compass-stereo-{name}-dark.png"), OnDark(png, renderer.Size, name));
            if (!name.Contains('-'))
                File.WriteAllBytes(Path.Combine(folder, $"compass-stereo-{name}-in-view.png"), InView(png, renderer.Size, flight, head, name));
        }

        // Cost of one stereo image: Skia drawing on the CPU, then the copy into a reused D3D11 texture.
        var still = CompassMath.Compute(RigidPose.Identity, head, hand, offset);
        var pixels = new byte[renderer.Size * renderer.Size * 8];
        for (int i = 0; i < 20; i++) renderer.Render(still.Axes, CompassGlow.Teal, still.Eyes, pixels);
        const int frames = 300;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            var moving = CompassMath.Compute(RigidPose.Identity, head with { Position = head.Position + new Vector3(i * 1e-4f, 0, 0) }, hand, offset);
            renderer.Render(moving.Axes, CompassGlow.Teal, moving.Eyes, pixels);
        }
        double drawMs = watch.Elapsed.TotalMilliseconds / frames;
        string upload;
        try
        {
            using var texture = D3D11OverlayTexture.Create(0, 2 * renderer.Size, renderer.Size);
            for (int i = 0; i < 20; i++) { texture.Upload(pixels); texture.Flush(); }
            watch.Restart();
            for (int i = 0; i < frames; i++) { texture.Upload(pixels); texture.Flush(); }
            upload = (watch.Elapsed.TotalMilliseconds / frames).ToString("0.000", CultureInfo.InvariantCulture) + " ms (UpdateSubresource + Flush, hardware adapter 0)";
        }
        catch (InvalidOperationException exception) { upload = "no D3D11 device: " + exception.Message; }
        File.WriteAllText(Path.Combine(folder, "timing.txt"),
            $"stereo {2 * renderer.Size}x{renderer.Size} draw: {drawMs.ToString("0.000", CultureInfo.InvariantCulture)} ms per image (mean of {frames})\n" +
            $"upload: {upload}\n" +
            $"at 32 images/s: {(drawMs * 32 / 10).ToString("0.00", CultureInfo.InvariantCulture)} % of one core for drawing\n");
    }

    /// <summary>The stereo texture on a dark background with the two halves labelled.</summary>
    private static byte[] OnDark(byte[] texturePng, int size, string name)
    {
        int width = 2 * size + 48, height = size + 56;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x0b, 0x0e, 0x14));
        using var texture = SKImage.FromEncodedData(texturePng);
        canvas.DrawImage(texture, 16, 40);
        using var line = new SKPaint { Color = new SKColor(0x33, 0x3a, 0x48), StrokeWidth = 1 };
        canvas.DrawLine(16 + size + 0.5f, 40, 16 + size + 0.5f, 40 + size, line);
        using var label = new SKPaint { Color = new SKColor(0x90, 0x98, 0xa8), IsAntialias = true };
        using var font = new SKFont { Size = 14 };
        canvas.DrawText($"{name}: left eye", 16, 26, font, label);
        canvas.DrawText("right eye", 16 + size + 16, 26, font, label);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Each eye's half over a dark scene whose horizon is the flown world's as seen from the head,
    /// so the flown world tilts while the compass keeps showing the real floor.
    /// </summary>
    private static byte[] InView(byte[] texturePng, int size, RigidPose flight, RigidPose head, string name)
    {
        const int panel = 320, height = 360;
        using var surface = SKSurface.Create(new SKImageInfo(2 * panel, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x0b, 0x0e, 0x14));
        // The flown world's up, in the eye's frame: (D * H)^-1 applied to +Y.
        var worldUp = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(flight.Orientation * head.Orientation));
        float angle = MathF.Atan2(worldUp.X, worldUp.Y) * 180 / MathF.PI;
        // Pinhole view (focal 300 px): the horizon sits uz / |(ux, uy)| above the centre along world up.
        float sideways = MathF.Max(1e-3f, MathF.Sqrt(worldUp.X * worldUp.X + worldUp.Y * worldUp.Y));
        float horizon = Math.Clamp(-worldUp.Z / sideways * 300, -900, 900);
        using var texture = SKImage.FromEncodedData(texturePng);
        for (int eye = 0; eye < 2; eye++)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(eye * panel, 0, (eye + 1) * panel, height));
            canvas.Translate(eye * panel + panel / 2f, height / 2f);
            canvas.Save();
            canvas.RotateDegrees(angle);
            using (var sky = new SKPaint { Color = new SKColor(0x1b, 0x22, 0x33) })
                canvas.DrawRect(new SKRect(-1000, -2000, 1000, horizon), sky);
            using (var ground = new SKPaint { Color = new SKColor(0x12, 0x17, 0x12) })
                canvas.DrawRect(new SKRect(-1000, horizon, 1000, 2000), ground);
            using (var line = new SKPaint { Color = new SKColor(0x60, 0x70, 0x88), StrokeWidth = 2, IsAntialias = true })
                canvas.DrawLine(-1000, horizon, 1000, horizon, line);
            canvas.Restore();
            canvas.DrawImage(texture, new SKRect(eye * size, 0, (eye + 1) * size, size),
                new SKRect(-size / 2f, -size / 2f + 30, size / 2f, size / 2f + 30));
            canvas.Restore();
        }
        using var label = new SKPaint { Color = new SKColor(0x90, 0x98, 0xa8), IsAntialias = true };
        using var font = new SKFont { Size = 14 };
        canvas.DrawText($"{name}: flown world horizon (game) vs compass (real), left / right eye", 12, 20, font, label);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal sealed class CountingRenderer : ICompassRenderer
    {
        public CompassAxes LastAxes;
        public CompassEyes LastEyes;
        public int Size => 8;
        public void Render(CompassAxes axes, CompassGlow glow, CompassEyes eyes, Span<byte> rgba)
        {
            LastAxes = axes;
            LastEyes = eyes;
            rgba.Fill(1);
        }
    }

    internal sealed class FakeApi : ICompassOverlayApi
    {
        public string? CreateError;
        public int Creates, Transforms, StereoCalls, EyeReads;
        public bool Visible, Disposed, Stereo, FailEyes;
        public uint Device = uint.MaxValue;
        public float Width;
        public RigidPose Transform;
        public int RefuseImages, RefuseStereo, FailTransforms, ImageCalls;
        public (int Width, int Height) ImageSize;
        public TimeSpan Held;
        public (Vector3 Left, Vector3 Right) Eyes = (new(-0.032f, 0, 0), new(0.032f, 0, 0));
        public string? Create() { Creates++; return CreateError; }
        public void SetStereo()
        {
            StereoCalls++;
            if (RefuseStereo > 0) { RefuseStereo--; throw new InvalidOperationException("SetOverlayFlag(SideBySide_Parallel): RequestFailed"); }
            Stereo = true;
        }
        public (Vector3 Left, Vector3 Right) ReadEyes()
        {
            EyeReads++;
            if (FailEyes) throw new InvalidOperationException("IVRSystem is not available.");
            return Eyes;
        }
        public void SetWidth(float metres) => Width = metres;
        public void SetTransform(uint device, RigidPose overlayInDevice)
        {
            if (FailTransforms > 0) { FailTransforms--; throw new InvalidOperationException("SetOverlayTransformTrackedDeviceRelative: RequestFailed"); }
            Device = device; Transform = overlayInDevice; Transforms++;
        }
        public TimeSpan Submit(byte[] rgba, int width, int height)
        {
            ImageCalls++;
            ImageSize = (width, height);
            Assert.True(rgba.Length >= width * height * 4);
            if (RefuseImages > 0) { RefuseImages--; throw new InvalidOperationException("SetOverlayTexture: RequestFailed"); }
            return Held;
        }
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
