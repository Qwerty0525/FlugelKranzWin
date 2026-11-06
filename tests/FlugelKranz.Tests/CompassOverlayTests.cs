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

    /// <summary>The compass is off by default; these tests turn it on.</summary>
    private static readonly CompassSettings On = CompassSettings.Default with { Enabled = true };

    private static (CompassOverlay Overlay, FakeApi Api, DriverFlightRuntimeTests.ManualTime Time, Func<CompassSettings, CompassSettings> Set)
        Create(CompassSettings? initial = null, CountingRenderer? renderer = null, FakeApi? api = null)
    {
        api ??= new FakeApi();
        var settings = initial ?? On;
        var time = new DriverFlightRuntimeTests.ManualTime();
        var fake = api;
        var options = new CompassOptions(() => settings, renderer ?? new CountingRenderer()) { Api = _ => fake, RunThread = false };
        var overlay = new CompassOverlay(options, new object(), time);
        return (overlay, api, time, s => settings = s);
    }

    private static CompassSettings Always(CompassHand hand = CompassHand.Left) =>
        On with { Visibility = CompassVisibility.Always, Hand = hand };

    /// <summary>The head circling the left-hand sphere at a constant distance, about 1.1 degrees a step.</summary>
    private static RigidPose Orbiting(int step)
    {
        var centre = LeftHand.Transform(CompassMath.LeftHandOffset);
        var around = Quaternion.CreateFromAxisAngle(Vector3.UnitY, step * 0.02f);
        return Head with { Position = centre + Vector3.Transform(Head.Position - centre, around) };
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
        // A 4.5 cm sphere on a quad 1/0.76 as wide (room for the edge pointer).
        Assert.InRange(api.Width, 0.0592f, 0.0593f);
        Assert.Equal((8, 8), api.ImageSize);

        overlay.SetFlightActive(false);
        overlay.Tick();
        Assert.False(api.Visible);
    }

    [Fact]
    public void OffByDefault()
    {
        var (overlay, api, _, _) = Create(CompassSettings.Default with { Visibility = CompassVisibility.Always });
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.SetFlightActive(true);
        overlay.Tick();
        Assert.Equal(CompassState.Off, overlay.Status.State);
        Assert.False(api.Visible);
        Assert.Equal(0, api.Creates);
    }

    [Fact]
    public void HandSwitchAlwaysAndOffFollowTheSettingsLive()
    {
        var (overlay, api, time, set) = Create();
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        set(On with { Visibility = CompassVisibility.Always });
        overlay.Tick();
        Assert.True(api.Visible);
        Assert.Equal(4u, api.Device);
        Assert.Equal(1, overlay.ImagesDrawn);

        // Flipping hands mid-session moves it on the next update, with an image for the new view.
        set(On with { Visibility = CompassVisibility.Always, Hand = CompassHand.Left });
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Tick();
        Assert.True(api.Visible);
        Assert.Equal(3u, api.Device);
        Assert.Equal(CompassHand.Left, overlay.Status.Hand);
        Assert.Equal(2, overlay.ImagesDrawn);
        set(On with { Visibility = CompassVisibility.Always });
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Tick();
        Assert.Equal(4u, api.Device);
        Assert.Equal(3, overlay.ImagesDrawn);
        // A colour change redraws at once too.
        set(On with { Visibility = CompassVisibility.Always, Glow = CompassGlow.Amber });
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Tick();
        Assert.Equal(4, overlay.ImagesDrawn);

        set(On with { Enabled = false });
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
    public void FlightRedrawsOnlyForTheWorldDownDot()
    {
        // Without the world-down dot the image is the physical view: flight never redraws it.
        var (hidden, _, hiddenTime, _) = Create(Always() with { ShowWorldDown = false });
        hidden.Publish(Frame(Head, LeftHand), 3, 4);
        hidden.Tick();
        for (int i = 1; i <= 20; i++)
        {
            hiddenTime.Advance(TimeSpan.FromMilliseconds(15));
            hidden.PublishFlight(new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, i * 0.1f), new(i, 0, 0)));
            hidden.Tick();
        }
        Assert.Equal(1, hidden.ImagesDrawn);

        var renderer = new CountingRenderer();
        var (overlay, _, time, set) = Create(Always(), renderer);
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.True(Vector3.Distance(renderer.LastAxes.Down, renderer.LastAxes.WorldDown) < 1e-4f, "no flight: the dots coincide");
        // Yaw turns the world about its down: no redraw.
        for (int i = 1; i <= 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            overlay.PublishFlight(new(Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * 0.1f), new(i, 0, 0)));
            overlay.Tick();
        }
        Assert.Equal(1, overlay.ImagesDrawn);
        // Rolling moves the world's down in the view: redrawn, at most every 30 ms.
        var down = renderer.LastAxes.Down;
        for (int i = 1; i <= 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            overlay.PublishFlight(new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, i * 0.05f), Vector3.Zero));
            overlay.Tick();
        }
        Assert.InRange(overlay.ImagesDrawn - 1, 9, 11);
        Assert.True(Vector3.Distance(down, renderer.LastAxes.Down) < 1e-4f, "real down stays");
        Assert.InRange(MathF.Acos(Vector3.Dot(renderer.LastAxes.Down, Vector3.Normalize(renderer.LastAxes.WorldDown))) * 180 / MathF.PI, 50, 58);

        // Hiding it redraws once without the dot.
        int images = overlay.ImagesDrawn;
        set(Always() with { ShowWorldDown = false });
        time.Advance(TimeSpan.FromMilliseconds(15));
        overlay.Tick();
        Assert.Equal(images + 1, overlay.ImagesDrawn);
        Assert.Equal(Vector3.Zero, renderer.LastAxes.WorldDown);
    }

    [Fact]
    public void RefusedImagesKeepTheCompassAndRetryLater()
    {
        var (overlay, api, time, _) = Create(On with { Visibility = CompassVisibility.Always });
        overlay.Publish(Frame(Head, LeftHand), 3, 4);
        overlay.Tick();
        Assert.True(api.Visible);
        api.RefuseImages = 3;
        int calls = api.ImageCalls;
        for (int i = 1; i <= 200; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(15));
            overlay.Publish(Frame(Orbiting(i), LeftHand), 3, 4);
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
        var (overlay, api, time, _) = Create(On with { Visibility = CompassVisibility.Always });
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
            overlay.Publish(Frame(Orbiting(i), LeftHand), 3, 4);
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
        var options = new CompassOptions(() => On, new CountingRenderer()) { Api = _ => api, RunThread = false };
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
        var options = new CompassOptions(() => On, new CountingRenderer()) { Api = _ => api, RunThread = false };
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

    /// <summary>Real down as seen looking down 30 degrees at the ball: on its far half, toward the bottom.</summary>
    private static readonly CompassAxes LookingDown = new(Vector3.Normalize(new(0, -0.866f, -0.5f)), -Vector3.UnitZ, Vector3.UnitX);
    /// <summary>Real down as seen looking up 45 degrees at the ball: on its near half.</summary>
    private static readonly CompassAxes LookingUp = new(Vector3.Normalize(new(0, -0.707f, 0.707f)), -Vector3.UnitZ, Vector3.UnitX);

    private static (byte R, byte G, byte B, byte A) Pixel(byte[] pixels, int size, float x, float y)
    {
        // Image coordinates in sphere radii (y up) to pixels.
        float radius = size * CompassMath.SphereFraction / 2;
        int px = (int)MathF.Round(size / 2f + x * radius), py = (int)MathF.Round(size / 2f - y * radius);
        int i = (py * size + px) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    [Fact]
    public void NavballShadesSkyAndGroundAndDrawsTheHorizon()
    {
        using var renderer = new CompassRenderer();
        int size = renderer.Size;
        var pixels = new byte[size * size * 4];
        renderer.Render(LookingDown, CompassGlow.Teal, pixels);
        // Corners are clear; nothing beyond the rim except the pointer.
        Assert.Equal(0, pixels[3]);
        Assert.Equal(0, Pixel(pixels, size, 0, 1.25f).A);
        // Sky tint above the horizon, ground tint below it (the horizon dips to y = -0.5 here).
        var sky = Pixel(pixels, size, 0, 0.5f);
        Assert.True(sky.B > sky.R + 60 && sky.A is > 50 and < 90, $"sky {sky}");
        var alsoSky = Pixel(pixels, size, 0.2f, -0.3f);
        Assert.True(alsoSky.B > alsoSky.R + 60, $"sky just above the horizon {alsoSky}");
        var ground = Pixel(pixels, size, -0.5f, -0.75f);
        Assert.True(ground.R > ground.B + 60 && ground.A is > 75 and < 115, $"ground {ground}");
        // A bright, nearly white horizon line where they meet.
        var horizon = Pixel(pixels, size, 0, -0.5f);
        Assert.True(horizon.A > 180 && horizon.R > 190 && horizon.B > 190, $"horizon {horizon}");
    }

    [Fact]
    public void DownIsAHollowRingBehindAndADotInFront()
    {
        using var renderer = new CompassRenderer();
        int size = renderer.Size;
        float ring = 0.032f * size / (size * CompassMath.SphereFraction / 2);
        var pixels = new byte[size * size * 4];

        renderer.Render(LookingDown, CompassGlow.Teal, pixels);
        var hollow = Pixel(pixels, size, 0, -0.866f);
        var rim = Pixel(pixels, size, ring, -0.866f);
        Assert.True(rim.A > hollow.A + 30, $"ring {rim} brighter than its middle {hollow}");
        Assert.True(rim.G > rim.R + 40, $"ring in the glow colour {rim}");

        renderer.Render(LookingUp, CompassGlow.Teal, pixels);
        var dot = Pixel(pixels, size, 0, -0.707f);
        Assert.True(dot.A > 230 && dot.G > dot.R + 40, $"dot {dot}");
        renderer.Render(LookingUp, CompassGlow.Amber, pixels);
        var amber = Pixel(pixels, size, 0, -0.707f);
        Assert.True(amber.R > amber.B + 60, $"amber dot {amber}");
    }

    [Fact]
    public void WorldDownIsAWhiteDotOrRingUnderTheGlowingMarker()
    {
        using var renderer = new CompassRenderer();
        int size = renderer.Size;
        var pixels = new byte[size * size * 4];

        // No flight: the dots coincide and the glowing real-down dot is drawn on top.
        renderer.Render(LookingUp with { WorldDown = LookingUp.Down }, CompassGlow.Teal, pixels);
        var shared = Pixel(pixels, size, 0, -0.707f);
        Assert.True(shared.G > shared.R + 40, $"glowing dot on top {shared}");

        // Rolled: a small plain white dot on the near half, left of centre here.
        var front = Vector3.Normalize(new(-0.6f, 0, 0.8f));
        renderer.Render(LookingDown with { WorldDown = front }, CompassGlow.Teal, pixels);
        var dot = Pixel(pixels, size, -0.6f, 0);
        Assert.True(dot.A > 200 && dot.R > 220 && dot.G > 220 && dot.B > 220, $"white dot {dot}");

        // On the far half: a faint white ring, hollow in the middle.
        renderer.Render(LookingDown with { WorldDown = Vector3.Normalize(new(-0.6f, 0, -0.8f)) }, CompassGlow.Teal, pixels);
        float ring = 0.02f * size / (size * CompassMath.SphereFraction / 2);
        var middle = Pixel(pixels, size, -0.6f, 0);
        var edge = Pixel(pixels, size, -0.6f - ring, 0);
        Assert.True(edge.A > middle.A + 15 && edge.R > 150, $"ring {edge} around {middle}");

        // Not shown: nothing white there.
        renderer.Render(LookingDown, CompassGlow.Teal, pixels);
        var none = Pixel(pixels, size, -0.6f, 0);
        Assert.True(none.R < 200, $"no dot {none}");
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    public void EdgePointerSitsJustOutsideTheRimTowardRealDown(float x, float y)
    {
        using var renderer = new CompassRenderer();
        int size = renderer.Size;
        var pixels = new byte[size * size * 4];
        // Real down on the far half, in the given direction on the image.
        var axes = new CompassAxes(Vector3.Normalize(new(x * 0.8f, y * 0.8f, -0.6f)), -Vector3.UnitZ, Vector3.UnitX);
        renderer.Render(axes, CompassGlow.Teal, pixels);
        float beyond = 1 + 0.05f * size / (size * CompassMath.SphereFraction / 2);
        var pointer = Pixel(pixels, size, x * beyond, y * beyond);
        Assert.True(pointer.A > 180 && pointer.G > pointer.R + 40, $"pointer {pointer}");
        Assert.Equal(0, Pixel(pixels, size, -x * beyond, -y * beyond).A);
        // It fits in the texture with its glow.
        Assert.Equal(0, Pixel(pixels, size, x * 1.31f, y * 1.31f).A);
    }

    /// <summary>
    /// Saves navball previews when FLUGELKRANZ_COMPASS_PNG names a folder: the texture (transparent)
    /// and on a dark background for real frames tilted against the eye (looking down 30° at the
    /// hand, head rolled 90°, looking up 45°, head upside down); a mock of the flown view for flight
    /// roll 90°, pitch 45° and upside down with the head level (the ball does not change: it shows
    /// real down as the eye sees it, while the flown world tilts); and timing.txt.
    /// </summary>
    [Fact]
    public void RenderPreviewImagesWhenAsked()
    {
        string? folder = Environment.GetEnvironmentVariable("FLUGELKRANZ_COMPASS_PNG");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var renderer = new CompassRenderer();
        static float Radians(float degrees) => degrees * MathF.PI / 180;
        static (RigidPose Head, RigidPose Ball) Looking(float pitch, float roll)
        {
            var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(pitch)) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(roll));
            var head = new RigidPose(orientation, new(0, 1.6f, 0));
            return (head, new RigidPose(Quaternion.Identity, head.Transform(new(0, 0, -0.4f))));
        }
        var views = new (string Name, float Pitch, float Roll, CompassGlow Glow)[]
        {
            ("identity", -30, 0, CompassGlow.Teal),
            ("roll90", -30, 90, CompassGlow.Teal),
            ("pitch45", 45, 0, CompassGlow.Teal),
            ("upside-down", -30, 180, CompassGlow.Teal),
            ("identity-lime", -30, 0, CompassGlow.Lime),
            ("identity-amber", -30, 0, CompassGlow.Amber)
        };
        foreach (var (name, pitch, roll, glow) in views)
        {
            var (head, ball) = Looking(pitch, roll);
            var view = CompassMath.Compute(RigidPose.Identity, head, ball, Vector3.Zero);
            var png = renderer.RenderPng(view.Axes, glow);
            File.WriteAllBytes(Path.Combine(folder, $"navball-{name}.png"), png);
            File.WriteAllBytes(Path.Combine(folder, $"navball-{name}-dark.png"), OnDark(png, renderer.Size, name));
        }
        var flights = new (string Name, RigidPose Flight)[]
        {
            ("identity", RigidPose.Identity),
            ("roll90", new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new(0, 3, 0))),
            ("pitch45", new(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4), new(0, 0, -2))),
            ("upside-down", new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), new(0, 3.2f, 0)))
        };
        var (levelHead, levelBall) = Looking(-30, 0);
        foreach (var (name, flight) in flights)
        {
            var view = CompassMath.Compute(flight, levelHead, levelBall, Vector3.Zero);
            var png = renderer.RenderPng(view.Axes, CompassGlow.Teal);
            File.WriteAllBytes(Path.Combine(folder, $"navball-flight-{name}.png"), png);
            File.WriteAllBytes(Path.Combine(folder, $"navball-flight-{name}-dark.png"), OnDark(png, renderer.Size, $"flight {name}"));
            File.WriteAllBytes(Path.Combine(folder, $"navball-flight-{name}-in-view.png"), InView(png, renderer.Size, flight, levelHead, name));
        }

        // Cost of one image: Skia drawing on the CPU, then the copy into a reused D3D11 texture.
        var still = CompassMath.Compute(RigidPose.Identity, levelHead, levelBall, Vector3.Zero);
        var pixels = new byte[renderer.Size * renderer.Size * 4];
        for (int i = 0; i < 20; i++) renderer.Render(still.Axes, CompassGlow.Teal, pixels);
        const int frames = 300;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            var moving = CompassMath.Compute(RigidPose.Identity, levelHead with { Position = levelHead.Position + new Vector3(i * 1e-3f, 0, 0) }, levelBall, Vector3.Zero);
            renderer.Render(moving.Axes, CompassGlow.Teal, pixels);
        }
        double drawMs = watch.Elapsed.TotalMilliseconds / frames;
        string upload;
        try
        {
            using var texture = D3D11OverlayTexture.Create(0, renderer.Size, renderer.Size);
            for (int i = 0; i < 20; i++) { texture.Upload(pixels); texture.Flush(); }
            watch.Restart();
            for (int i = 0; i < frames; i++) { texture.Upload(pixels); texture.Flush(); }
            upload = (watch.Elapsed.TotalMilliseconds / frames).ToString("0.000", CultureInfo.InvariantCulture) + " ms (UpdateSubresource + Flush, hardware adapter 0)";
        }
        catch (InvalidOperationException exception) { upload = "no D3D11 device: " + exception.Message; }
        File.WriteAllText(Path.Combine(folder, "timing.txt"),
            $"navball {renderer.Size}x{renderer.Size} draw: {drawMs.ToString("0.000", CultureInfo.InvariantCulture)} ms per image (mean of {frames})\n" +
            $"upload: {upload}\n" +
            $"at 32 images/s: {(drawMs * 32 / 10).ToString("0.00", CultureInfo.InvariantCulture)} % of one core for drawing\n");
    }

    /// <summary>The texture on a dark background with a label.</summary>
    private static byte[] OnDark(byte[] texturePng, int size, string name)
    {
        int width = size + 32, height = size + 48;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x0b, 0x0e, 0x14));
        using var texture = SKImage.FromEncodedData(texturePng);
        canvas.DrawImage(texture, 16, 36);
        using var label = new SKPaint { Color = new SKColor(0x90, 0x98, 0xa8), IsAntialias = true };
        using var font = new SKFont { Size = 14 };
        canvas.DrawText(name, 16, 24, font, label);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The texture over a dark scene whose horizon is the flown world's, as seen from the physical head.</summary>
    private static byte[] InView(byte[] texturePng, int size, RigidPose flight, RigidPose head, string name)
    {
        const int width = 520, height = 400;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        // The flown world's up, in the eye's frame: (D * H)^-1 applied to +Y.
        var worldUp = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(flight.Orientation * head.Orientation));
        float angle = MathF.Atan2(worldUp.X, worldUp.Y) * 180 / MathF.PI;
        canvas.Clear(new SKColor(0x0b, 0x0e, 0x14));
        canvas.Save();
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(angle);
        // Pinhole view (focal 300 px): the horizon sits uz / |(ux, uy)| above the centre along world up.
        float sideways = MathF.Max(1e-3f, MathF.Sqrt(worldUp.X * worldUp.X + worldUp.Y * worldUp.Y));
        float horizon = Math.Clamp(-worldUp.Z / sideways * 300, -900, 900);
        using (var sky = new SKPaint { Color = new SKColor(0x1b, 0x22, 0x33) })
            canvas.DrawRect(new SKRect(-1000, -2000, 1000, horizon), sky);
        using (var ground = new SKPaint { Color = new SKColor(0x12, 0x17, 0x12) })
            canvas.DrawRect(new SKRect(-1000, horizon, 1000, 2000), ground);
        using (var line = new SKPaint { Color = new SKColor(0x60, 0x70, 0x88), StrokeWidth = 2, IsAntialias = true })
            canvas.DrawLine(-1000, horizon, 1000, horizon, line);
        canvas.Restore();
        using var texture = SKImage.FromEncodedData(texturePng);
        canvas.DrawImage(texture, new SKRect(width / 2f - size / 2f, height / 2f - size / 2f + 40, width / 2f + size / 2f, height / 2f + size / 2f + 40));
        using var label = new SKPaint { Color = new SKColor(0x90, 0x98, 0xa8), IsAntialias = true };
        using var font = new SKFont { Size = 14 };
        canvas.DrawText($"flight {name}: flown world horizon (game) vs navball (real; white dot = world down)", 12, 22, font, label);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal sealed class CountingRenderer : ICompassRenderer
    {
        public CompassAxes LastAxes;
        public int Size => 8;
        public void Render(CompassAxes axes, CompassGlow glow, Span<byte> rgba) { LastAxes = axes; rgba.Fill(1); }
    }

    internal sealed class FakeApi : ICompassOverlayApi
    {
        public string? CreateError;
        public int Creates, Transforms;
        public bool Visible, Disposed;
        public uint Device = uint.MaxValue;
        public float Width;
        public RigidPose Transform;
        public int RefuseImages, FailTransforms, ImageCalls;
        public (int Width, int Height) ImageSize;
        public TimeSpan Held;
        public string? Create() { Creates++; return CreateError; }
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
