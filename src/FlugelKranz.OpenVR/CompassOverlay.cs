using System.Numerics;
using System.Runtime.InteropServices;
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
    void SetWidth(float metres);
    void SetTransform(uint device, RigidPose overlayInDevice);
    void SetImage(byte[] straightRgba, int size);
    void Show();
    void Hide();
}

/// <summary>
/// Gravity compass on a controller. The flight loop only publishes poses (cheap, under a lock);
/// a below-normal-priority thread computes, draws and talks to IVROverlay about 64 times a second
/// (15 ms sleeps). The transform is sent when it moves by more than 0.5 mm / 0.3 degrees; a new image
/// is drawn and uploaded only when the view of the sphere turns by more than 3 degrees, and at
/// most 10 times a second. In between the previous image stays fixed in the room (held billboard),
/// so its arrows stay correct while only its facing lags slightly.
/// Each SetOverlayRaw call hands the compositor a shared memory block; vrclient refuses new ones
/// while 200 are outstanding ("Refusing to create memory block because 201 blocks are already
/// outstanding", 2026-11-06 19:30). A refused upload keeps the previous image and is retried with a
/// growing delay, and other SteamVR errors only disable the compass after 3 s of failing updates.
/// </summary>
public sealed class CompassOverlay : IDisposable
{
    public static readonly TimeSpan ThreadPeriod = TimeSpan.FromMilliseconds(15);
    public static readonly TimeSpan MinimumImageInterval = TimeSpan.FromMilliseconds(100);
    public const float ImageAngleRadians = 3f * MathF.PI / 180;
    public static readonly TimeSpan FirstUploadRetry = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumUploadRetry = TimeSpan.FromSeconds(2);
    /// <summary>How long other overlay calls may keep failing before the compass gives up.</summary>
    public static readonly TimeSpan FailureGrace = TimeSpan.FromSeconds(3);
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

    private bool created, unavailable, shown;
    private string? unavailableDetail;
    private float width = -1;
    private Quaternion? billboard;
    private CompassGlow imageGlow;
    private CompassHand imageHand;
    private long lastImage;
    private long nextUploadAllowed;
    private TimeSpan uploadRetry;
    private long failingSince;
    private bool failing;
    private uint transformDevice = Vr.k_unTrackedDeviceIndexInvalid;
    private RigidPose transform;
    private volatile CompassStatus status = new(CompassState.Off, CompassSettings.Default.Hand);

    /// <summary>Images drawn so far (for tests and diagnostics).</summary>
    public int ImagesDrawn { get; private set; }
    /// <summary>Uploads SteamVR refused (each kept the previous image and was retried).</summary>
    public int UploadFailures { get; private set; }
    public CompassStatus Status => status;

    public CompassOverlay(CompassOptions options, object openVrGate, TimeProvider? time = null)
    {
        this.options = options;
        this.time = time ?? TimeProvider.System;
        api = options.Api(openVrGate);
        image = new byte[options.Renderer.Size * options.Renderer.Size * 4];
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
        if (!settings.Enabled)
        {
            Hide();
            status = new(CompassState.Off, settings.Hand);
            return;
        }
        if (unavailable)
        {
            status = new(CompassState.Unavailable, settings.Hand, unavailableDetail);
            return;
        }
        if (!created)
        {
            if (api.Create() is { } error) { MarkUnavailable(error); status = new(CompassState.Unavailable, settings.Hand, error); return; }
            created = true;
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
            status = new(CompassState.WaitingForFlight, settings.Hand);
            return;
        }
        var hand = settings.Hand == CompassHand.Right ? frame.Right : frame.Left;
        if (!ready || !frame.HeadTracked || !frame.Head.IsValid || !hand.IsTracked || !hand.Pose.IsValid ||
            device >= Vr.k_unMaxTrackedDeviceCount || !currentFlight.IsValid)
        {
            Hide();
            status = new(CompassState.WaitingForTracking, settings.Hand);
            return;
        }

        float quadWidth = settings.SizeCentimetres / 100 / CompassMath.SphereFraction;
        if (quadWidth != width) { api.SetWidth(quadWidth); width = quadWidth; }

        var offset = CompassMath.HandOffset(settings.Hand);
        var fresh = CompassMath.Compute(currentFlight, frame.Head, hand.Pose, offset);
        long now = time.GetTimestamp();
        // A hand switch (live, from the settings) moves the sphere: draw for the new view at once.
        bool firstImage = billboard is null || imageGlow != settings.Glow || imageHand != settings.Hand;
        bool turned = billboard is { } held && CompassMath.Angle(held, fresh.PhysicalBillboard) > ImageAngleRadians;
        CompassView? uploaded = null;
        bool uploadDue = (firstImage || turned && time.GetElapsedTime(lastImage, now) >= MinimumImageInterval) &&
            now >= nextUploadAllowed;
        if (uploadDue)
        {
            options.Renderer.Render(fresh.Axes, settings.Glow, image);
            try
            {
                api.SetImage(image, options.Renderer.Size);
                uploadRetry = TimeSpan.Zero;
                ImagesDrawn++;
                billboard = fresh.PhysicalBillboard;
                imageGlow = settings.Glow;
                imageHand = settings.Hand;
                lastImage = now;
                uploaded = fresh;
            }
            catch (Exception)
            {
                // Refused (for example too many outstanding memory blocks): keep the previous image,
                // which stays correct with the held billboard, and try again later.
                UploadFailures++;
                uploadRetry = uploadRetry == TimeSpan.Zero ? FirstUploadRetry
                    : TimeSpan.FromTicks(Math.Min(uploadRetry.Ticks * 2, MaximumUploadRetry.Ticks));
                nextUploadAllowed = now + (long)(uploadRetry.TotalSeconds * time.TimestampFrequency);
            }
        }
        if (billboard is null)
        {
            // No image has reached SteamVR yet: nothing to show.
            Hide();
            status = new(CompassState.WaitingForTracking, settings.Hand);
            return;
        }
        var view = uploaded ?? CompassMath.Compute(currentFlight, frame.Head, hand.Pose, offset, billboard);

        if (device != transformDevice ||
            Vector3Distance(view.OverlayInHand, transform) > TransformMetres ||
            CompassMath.Angle(view.OverlayInHand.Orientation, transform.Orientation) > TransformAngleRadians)
        {
            api.SetTransform(device, view.OverlayInHand);
            transformDevice = device;
            transform = view.OverlayInHand;
        }
        if (!shown) { api.Show(); shown = true; }
        status = new(CompassState.Visible, settings.Hand,
            UploadFailures > 0 ? UploadFailures.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static float Vector3Distance(RigidPose a, RigidPose b) => Vector3.Distance(a.Position, b.Position);

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
        status = new(CompassState.Unavailable, status.Hand, detail);
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

/// <summary>IVROverlay calls, each under the lock the flight loop holds for its own OpenVR calls.</summary>
internal sealed class OpenVrCompassOverlayApi(object gate) : ICompassOverlayApi
{
    public const string Key = OpenVrSession.AppKey + ".compass";
    private ulong handle;
    private bool created;

    public string? Create()
    {
        lock (gate)
        {
            var overlay = Vr.Overlay;
            if (overlay is null) return "IVROverlay is not available.";
            var error = overlay.CreateOverlay(Key, "FlugelKranz gravity compass", ref handle);
            if (error != EVROverlayError.None) return "CreateOverlay: " + error;
            created = true;
            // No input: the overlay never takes laser or mouse focus away from the game.
            Check(overlay.SetOverlayInputMethod(handle, VROverlayInputMethod.None), "SetOverlayInputMethod");
            Check(overlay.SetOverlayAlpha(handle, 1), "SetOverlayAlpha");
            return null;
        }
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

    public void SetImage(byte[] straightRgba, int size)
    {
        var pin = GCHandle.Alloc(straightRgba, GCHandleType.Pinned);
        try
        {
            lock (gate) Check(Overlay().SetOverlayRaw(handle, pin.AddrOfPinnedObject(), (uint)size, (uint)size, 4), "SetOverlayRaw");
        }
        finally { pin.Free(); }
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
        if (!created) return;
        created = false;
        lock (gate) Vr.Overlay?.DestroyOverlay(handle);
    }
}
