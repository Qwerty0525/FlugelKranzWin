using System.Diagnostics;
using System.Numerics;
using FlugelKranz.Core;
using Valve.VR;
using Vr = Valve.VR.OpenVR;

namespace FlugelKranz.OpenVR;

/// <summary>What the runtime needs to show the gravity compass. Null leaves it out entirely.</summary>
public sealed record CompassOptions(Func<CompassSettings> Settings, ICompassRenderer Renderer)
{
    /// <summary>Overlay calls; tests replace it. Receives the lock shared with the flight loop's OpenVR calls.</summary>
    public Func<object, ICompassOverlayApi> Api { get; init; } = gate => new OpenVrCompassOverlayApi(gate);
    /// <summary>False in tests, which call <see cref="CompassOverlay.Tick"/> themselves.</summary>
    public bool RunThread { get; init; } = true;
}

/// <summary>The few IVROverlay calls the compass uses. Each throws on a SteamVR error.</summary>
public interface ICompassOverlayApi : IDisposable
{
    /// <summary>Creates the overlay; returns an error text instead of throwing when overlays are unavailable.</summary>
    string? Create();
    /// <summary>Marks the overlay side-by-side stereo: the left half of the texture for the left eye.</summary>
    void SetStereo();
    /// <summary>Each eye's position in the HMD's frame, in metres (<c>GetEyeToHeadTransform</c>).</summary>
    (Vector3 Left, Vector3 Right) ReadEyes();
    void SetWidth(float metres);
    void SetTransform(uint device, RigidPose overlayInDevice);
    /// <summary>
    /// Puts one straight-alpha RGBA image into the overlay's GPU texture and hands it to SteamVR.
    /// Throws when SteamVR or the GPU did not take it; the previous image then stays on screen.
    /// Returns how long the OpenVR lock (shared with the flight loop) was held.
    /// </summary>
    TimeSpan Submit(byte[] rgba, int width, int height);
    void Show();
    void Hide();
}

/// <summary>
/// Gravity compass on a controller, drawn in stereo. The flight loop only publishes poses (cheap,
/// under a lock); a below-normal-priority thread computes, draws and talks to IVROverlay about 64
/// times a second (15 ms sleeps). The transform is sent when it moves by more than 0.5 mm / 0.3°.
/// A new side-by-side image (one half per eye, each drawn from that eye's position) is drawn when
/// the view of the sphere changes, that is when the quad turns by more than 0.5° or an eye moves by
/// more than 2 % of the sphere radius relative to it, at most once per 30 ms (about 32 a second
/// with 15.6 ms ticks); a still view sends nothing. In between, the previous image stays fixed in
/// the room (held billboard), so its arrows stay correct while its facing lags slightly.
/// Images go through one reused D3D11 texture and <c>SetOverlayTexture</c>. SetOverlayRaw is not
/// used: every call hands the compositor a shared memory block, and on 2026-11-06 vrclient refused
/// new ones after 200 ("Refusing to create memory block because 201 blocks are already
/// outstanding"), freezing the compass. A refused stereo flag or image keeps the last good image
/// (or shows nothing before the first) and is retried after 250 ms, doubling up to 2 s; other
/// SteamVR errors only disable the compass after 3 s of failing updates. Flight never depends on it.
/// </summary>
public sealed class CompassOverlay : IDisposable
{
    public static readonly TimeSpan ThreadPeriod = TimeSpan.FromMilliseconds(15);
    public static readonly TimeSpan MinimumImageInterval = TimeSpan.FromMilliseconds(30);
    public const float ImageAngleRadians = 0.5f * MathF.PI / 180;
    /// <summary>Eye movement relative to the quad, in sphere radii, that redraws (about 0.45 mm on a 4.5 cm sphere).</summary>
    public const float ImageEyeRadii = 0.02f;
    /// <summary>How often the eye positions are read again (the IPD can change while running).</summary>
    public static readonly TimeSpan EyeRefresh = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FirstUploadRetry = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumUploadRetry = TimeSpan.FromSeconds(2);
    /// <summary>How long other overlay calls may keep failing before the compass gives up.</summary>
    public static readonly TimeSpan FailureGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatsWindow = TimeSpan.FromSeconds(1);
    private const float TransformAngleRadians = 0.3f * MathF.PI / 180;
    private const float TransformMetres = 0.0005f;

    private readonly CompassOptions options;
    private readonly ICompassOverlayApi api;
    private readonly TimeProvider time;
    private readonly object poseGate = new();
    private readonly byte[] image;
    private readonly Thread? thread;
    private volatile bool stopping;

    private InputFrame physical;
    private bool hasPhysical;
    private uint leftDevice = Vr.k_unTrackedDeviceIndexInvalid, rightDevice = Vr.k_unTrackedDeviceIndexInvalid;
    private RigidPose flight = RigidPose.Identity;
    private bool flightActive;

    private bool created, stereo, unavailable, shown;
    private string? unavailableDetail;
    private float width = -1;
    private Quaternion? billboard;
    private CompassGlow imageGlow;
    private CompassHand imageHand;
    private float imageSize;
    private CompassEyes imageEyes;
    private long lastImage;
    private long nextAttempt;
    private TimeSpan retry;
    private long failingSince;
    private bool failing;
    private uint transformDevice = Vr.k_unTrackedDeviceIndexInvalid;
    private RigidPose transform;
    private Vector3 leftEye = CompassStereo.Default.LeftEyeInHead, rightEye = CompassStereo.Default.RightEyeInHead;
    private bool eyesTried;
    private long lastEyeRead;
    private volatile CompassStatus status = new(CompassState.Off, CompassSettings.Default.Hand);

    // Per-second statistics for the settings panel.
    private long windowStart;
    private int windowImages;
    private TimeSpan windowDraw, windowHeld, windowMaxHeld;
    private readonly TimeSpan[] recentMaxHeld = new TimeSpan[5];
    private int recentIndex;
    private float imagesPerSecond, drawMilliseconds, heldMilliseconds, heldMaxMilliseconds;

    /// <summary>Images SteamVR accepted so far (for tests and diagnostics).</summary>
    public int ImagesDrawn { get; private set; }
    /// <summary>Images or stereo-flag calls SteamVR refused (each kept the last good image and was retried).</summary>
    public int Refusals { get; private set; }
    public string? LastRefusal { get; private set; }
    public CompassStatus Status => status;

    public CompassOverlay(CompassOptions options, object openVrGate, TimeProvider? time = null)
    {
        this.options = options;
        this.time = time ?? TimeProvider.System;
        api = options.Api(openVrGate);
        int size = options.Renderer.Size;
        image = new byte[2 * size * size * 4];
        windowStart = this.time.GetTimestamp();
        if (!options.RunThread) return;
        thread = new Thread(Run) { IsBackground = true, Name = "FlugelKranz compass", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>Physical head / hands (Standing space) and the controller device indices, from the loop.</summary>
    public void Publish(InputFrame physicalFrame, uint left, uint right)
    {
        lock (poseGate) { physical = physicalFrame; hasPhysical = true; leftDevice = left; rightDevice = right; }
    }

    /// <summary>The flight transform D (game pose = D * physical) last sent to the driver.</summary>
    public void PublishFlight(RigidPose offset)
    {
        lock (poseGate) flight = offset;
    }

    public void SetFlightActive(bool active)
    {
        lock (poseGate) flightActive = active;
    }

    private void Run()
    {
        while (!stopping)
        {
            Tick();
            Thread.Sleep(ThreadPeriod);
        }
    }

    /// <summary>
    /// One update. A SteamVR error does not end the compass at once: updates keep retrying, and only
    /// <see cref="FailureGrace"/> of nothing but errors marks it unavailable.
    /// </summary>
    public void Tick()
    {
        try
        {
            Update();
            failing = false;
        }
        catch (Exception exception)
        {
            long now = time.GetTimestamp();
            if (!failing) { failing = true; failingSince = now; }
            else if (time.GetElapsedTime(failingSince, now) >= FailureGrace) MarkUnavailable(exception.Message);
        }
    }

    private void Update()
    {
        var settings = options.Settings().Normalized();
        long now = time.GetTimestamp();
        RollStatistics(now);
        if (!settings.Enabled)
        {
            Hide();
            status = MakeStatus(CompassState.Off, settings.Hand);
            return;
        }
        if (unavailable)
        {
            status = MakeStatus(CompassState.Unavailable, settings.Hand, unavailableDetail);
            return;
        }
        if (!created)
        {
            if (api.Create() is { } error) { MarkUnavailable(error); status = MakeStatus(CompassState.Unavailable, settings.Hand, error); return; }
            created = true;
        }
        if (!stereo)
        {
            // Without the side-by-side flag both halves would show next to each other: show nothing until it is set.
            if (now >= nextAttempt)
            {
                try { api.SetStereo(); stereo = true; retry = TimeSpan.Zero; }
                catch (Exception exception) { Refused(exception, now); }
            }
            if (!stereo)
            {
                Hide();
                status = MakeStatus(CompassState.WaitingForSteamVr, settings.Hand, LastRefusal);
                return;
            }
        }

        InputFrame frame;
        bool ready, active;
        uint device;
        RigidPose currentFlight;
        lock (poseGate)
        {
            frame = physical;
            ready = hasPhysical;
            active = flightActive;
            device = settings.Hand == CompassHand.Right ? rightDevice : leftDevice;
            currentFlight = flight;
        }
        if (settings.Visibility == CompassVisibility.WhileFlying && !active)
        {
            Hide();
            status = MakeStatus(CompassState.WaitingForFlight, settings.Hand);
            return;
        }
        var hand = settings.Hand == CompassHand.Right ? frame.Right : frame.Left;
        if (!ready || !frame.HeadTracked || !frame.Head.IsValid || !hand.IsTracked || !hand.Pose.IsValid ||
            device >= Vr.k_unMaxTrackedDeviceCount || !currentFlight.IsValid)
        {
            Hide();
            status = MakeStatus(CompassState.WaitingForTracking, settings.Hand);
            return;
        }

        RefreshEyes(now);
        float quadWidth = settings.SizeCentimetres / 100 / CompassMath.SphereFraction;
        if (quadWidth != width) { api.SetWidth(quadWidth); width = quadWidth; }

        var offset = CompassMath.HandOffset(settings.Hand);
        var eyes = new CompassStereo(leftEye, rightEye, settings.SizeCentimetres / 200);
        var fresh = CompassMath.Compute(currentFlight, frame.Head, hand.Pose, offset, stereo: eyes);
        // A hand, colour or size switch (live, from the settings) changes the picture: draw it at once.
        bool firstImage = billboard is null || imageGlow != settings.Glow || imageHand != settings.Hand ||
            imageSize != settings.SizeCentimetres;
        bool changed = billboard is { } held &&
            (CompassMath.Angle(held, fresh.PhysicalBillboard) > ImageAngleRadians ||
             Vector3.Distance(imageEyes.Left, fresh.Eyes.Left) > ImageEyeRadii ||
             Vector3.Distance(imageEyes.Right, fresh.Eyes.Right) > ImageEyeRadii);
        CompassView? uploaded = null;
        if ((firstImage || changed && time.GetElapsedTime(lastImage, now) >= MinimumImageInterval) && now >= nextAttempt)
        {
            long drawStart = time.GetTimestamp();
            options.Renderer.Render(fresh.Axes, settings.Glow, fresh.Eyes, image);
            var drawn = time.GetElapsedTime(drawStart);
            try
            {
                var heldLock = api.Submit(image, 2 * options.Renderer.Size, options.Renderer.Size);
                retry = TimeSpan.Zero;
                ImagesDrawn++;
                windowImages++;
                windowDraw += drawn;
                windowHeld += heldLock;
                if (heldLock > windowMaxHeld) windowMaxHeld = heldLock;
                billboard = fresh.PhysicalBillboard;
                imageGlow = settings.Glow;
                imageHand = settings.Hand;
                imageSize = settings.SizeCentimetres;
                imageEyes = fresh.Eyes;
                lastImage = now;
                uploaded = fresh;
            }
            catch (Exception exception)
            {
                // Refused or the GPU device was lost: keep the previous image, which stays correct
                // with the held billboard, and try again later.
                Refused(exception, now);
            }
        }
        if (billboard is null)
        {
            // No image has reached SteamVR yet: nothing to show.
            Hide();
            status = Refusals > 0
                ? MakeStatus(CompassState.WaitingForSteamVr, settings.Hand, LastRefusal)
                : MakeStatus(CompassState.WaitingForTracking, settings.Hand);
            return;
        }
        var view = uploaded ?? CompassMath.Compute(currentFlight, frame.Head, hand.Pose, offset, billboard, eyes);

        if (device != transformDevice ||
            Vector3.Distance(view.OverlayInHand.Position, transform.Position) > TransformMetres ||
            CompassMath.Angle(view.OverlayInHand.Orientation, transform.Orientation) > TransformAngleRadians)
        {
            api.SetTransform(device, view.OverlayInHand);
            transformDevice = device;
            transform = view.OverlayInHand;
        }
        if (!shown) { api.Show(); shown = true; }
        status = MakeStatus(CompassState.Visible, settings.Hand);
    }

    /// <summary>Reads the eye offsets now and then; keeps the last good (or default) ones when SteamVR has none.</summary>
    private void RefreshEyes(long now)
    {
        if (eyesTried && time.GetElapsedTime(lastEyeRead, now) < EyeRefresh) return;
        eyesTried = true;
        lastEyeRead = now;
        try
        {
            var (left, right) = api.ReadEyes();
            float ipd = Vector3.Distance(left, right);
            if (float.IsFinite(ipd) && ipd is > 0.03f and < 0.1f) { leftEye = left; rightEye = right; }
        }
        catch (Exception) { }
    }

    private void Refused(Exception exception, long now)
    {
        Refusals++;
        LastRefusal = exception.Message;
        retry = retry == TimeSpan.Zero ? FirstUploadRetry
            : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaximumUploadRetry.Ticks));
        nextAttempt = now + (long)(retry.TotalSeconds * time.TimestampFrequency);
    }

    private void RollStatistics(long now)
    {
        var elapsed = time.GetElapsedTime(windowStart, now);
        if (elapsed < StatsWindow) return;
        imagesPerSecond = (float)(windowImages / elapsed.TotalSeconds);
        drawMilliseconds = windowImages > 0 ? (float)(windowDraw.TotalMilliseconds / windowImages) : 0;
        heldMilliseconds = windowImages > 0 ? (float)(windowHeld.TotalMilliseconds / windowImages) : 0;
        recentMaxHeld[recentIndex] = windowMaxHeld;
        recentIndex = (recentIndex + 1) % recentMaxHeld.Length;
        heldMaxMilliseconds = (float)recentMaxHeld.Max().TotalMilliseconds;
        windowStart = now;
        windowImages = 0;
        windowDraw = windowHeld = windowMaxHeld = TimeSpan.Zero;
    }

    private CompassStatus MakeStatus(CompassState state, CompassHand hand, string? detail = null) => new(state, hand, detail)
    {
        Refusals = Refusals,
        LastRefusal = LastRefusal,
        ImagesPerSecond = imagesPerSecond,
        DrawMilliseconds = drawMilliseconds,
        SteamVrMilliseconds = heldMilliseconds,
        SteamVrMaxMilliseconds = heldMaxMilliseconds
    };

    private void Hide()
    {
        if (!shown) return;
        shown = false;
        api.Hide();
    }

    private void MarkUnavailable(string detail)
    {
        unavailable = true;
        unavailableDetail = detail;
        status = MakeStatus(CompassState.Unavailable, status.Hand, detail);
        try { if (shown) api.Hide(); }
        catch (Exception) { }
        shown = false;
    }

    public void Dispose()
    {
        if (stopping) return;
        stopping = true;
        thread?.Join();
        try { api.Dispose(); }
        catch (Exception) { }
    }
}

/// <summary>
/// IVROverlay calls, each under the lock the flight loop holds for its own OpenVR calls. Images go
/// into one D3D11 texture, created on the compositor's adapter on first use and reused for every
/// image; SteamVR copies it on each <c>SetOverlayTexture</c>. The device is recreated after it is
/// lost or after three refusals in a row (for example once the compositor restarted).
/// </summary>
internal sealed class OpenVrCompassOverlayApi(object gate, string key = OpenVrCompassOverlayApi.Key) : ICompassOverlayApi
{
    public const string Key = OpenVrSession.AppKey + ".compass";
    private const int RecreateAfterRefusals = 3;
    private ulong handle;
    private bool created;
    private int adapterIndex = -1;
    private D3D11OverlayTexture? texture;
    private int refusalsInARow;

    public string? Create()
    {
        lock (gate)
        {
            var overlay = Vr.Overlay;
            if (overlay is null) return "IVROverlay is not available.";
            var error = overlay.CreateOverlay(key, "FlugelKranz gravity compass", ref handle);
            if (error != EVROverlayError.None) return "CreateOverlay: " + error;
            created = true;
            // No input: the overlay never takes laser or mouse focus away from the game.
            Check(overlay.SetOverlayInputMethod(handle, VROverlayInputMethod.None), "SetOverlayInputMethod");
            Check(overlay.SetOverlayAlpha(handle, 1), "SetOverlayAlpha");
            // The GPU the compositor renders on, so SteamVR can copy the texture without crossing adapters.
            Vr.System?.GetDXGIOutputInfo(ref adapterIndex);
            return null;
        }
    }

    public void SetStereo()
    {
        lock (gate) Check(Overlay().SetOverlayFlag(handle, VROverlayFlags.SideBySide_Parallel, true), "SetOverlayFlag(SideBySide_Parallel)");
    }

    public (Vector3 Left, Vector3 Right) ReadEyes()
    {
        HmdMatrix34_t left, right;
        lock (gate)
        {
            var system = Vr.System ?? throw new InvalidOperationException("IVRSystem is not available.");
            left = system.GetEyeToHeadTransform(EVREye.Eye_Left);
            right = system.GetEyeToHeadTransform(EVREye.Eye_Right);
        }
        return (OpenVrPose.FromMatrix(left).Position, OpenVrPose.FromMatrix(right).Position);
    }

    public void SetWidth(float metres)
    {
        lock (gate) Check(Overlay().SetOverlayWidthInMeters(handle, metres), "SetOverlayWidthInMeters");
    }

    public void SetTransform(uint device, RigidPose overlayInDevice)
    {
        var matrix = OpenVrPose.ToMatrix(overlayInDevice);
        lock (gate) Check(Overlay().SetOverlayTransformTrackedDeviceRelative(handle, device, ref matrix), "SetOverlayTransformTrackedDeviceRelative");
    }

    public TimeSpan Submit(byte[] rgba, int width, int height)
    {
        if (texture is null || texture.Width != width || texture.Height != height)
        {
            texture?.Dispose();
            texture = null;
            texture = D3D11OverlayTexture.Create(adapterIndex, width, height);
        }
        // The upload runs on this app's own device, outside the OpenVR lock.
        texture.Upload(rgba);
        var native = new Texture_t { handle = texture.Handle, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
        EVROverlayError error;
        TimeSpan held;
        lock (gate)
        {
            long start = Stopwatch.GetTimestamp();
            error = Overlay().SetOverlayTexture(handle, ref native);
            held = Stopwatch.GetElapsedTime(start);
        }
        texture.Flush();
        int removed = texture.DeviceRemovedReason();
        if (removed != 0)
        {
            DropTexture();
            throw new InvalidOperationException($"D3D11 device lost (0x{removed:X8}); it is recreated on the next try.");
        }
        if (error != EVROverlayError.None)
        {
            if (++refusalsInARow >= RecreateAfterRefusals) DropTexture();
            throw new InvalidOperationException("SetOverlayTexture: " + error);
        }
        refusalsInARow = 0;
        return held;
    }

    private void DropTexture()
    {
        texture?.Dispose();
        texture = null;
        refusalsInARow = 0;
    }

    public void Show()
    {
        lock (gate) Check(Overlay().ShowOverlay(handle), "ShowOverlay");
    }

    public void Hide()
    {
        lock (gate) Check(Overlay().HideOverlay(handle), "HideOverlay");
    }

    private static CVROverlay Overlay() => Vr.Overlay ?? throw new InvalidOperationException("IVROverlay is not available.");

    private static void Check(EVROverlayError error, string operation)
    {
        if (error != EVROverlayError.None) throw new InvalidOperationException($"{operation}: {error}");
    }

    public void Dispose()
    {
        try
        {
            if (created)
            {
                created = false;
                // Destroy the overlay first so SteamVR lets go of the texture before it is released.
                lock (gate) Vr.Overlay?.DestroyOverlay(handle);
            }
        }
        finally { DropTexture(); }
    }
}
