using System.Numerics;

namespace FlugelKranz.Core;

public enum CompassHand
{
    Left,
    Right
}

public enum CompassVisibility
{
    /// <summary>Shown while flight is ON.</summary>
    WhileFlying,
    /// <summary>Shown whenever the app is connected to SteamVR.</summary>
    Always
}

public enum CompassGlow
{
    Teal,
    Lime,
    Amber
}

/// <summary>The gravity compass on the back of one hand. Saved as <c>"compass"</c> in config.json.</summary>
public sealed record CompassSettings
{
    public const float MinimumSizeCentimetres = 3;
    public const float MaximumSizeCentimetres = 8;
    public static CompassSettings Default { get; } = new();

    public bool Enabled { get; init; } = true;
    /// <summary>Right by default: wrist-watch style overlays usually sit on the left wrist.</summary>
    public CompassHand Hand { get; init; } = CompassHand.Right;
    public CompassVisibility Visibility { get; init; } = CompassVisibility.WhileFlying;
    public CompassGlow Glow { get; init; } = CompassGlow.Teal;
    /// <summary>Sphere diameter.</summary>
    public float SizeCentimetres { get; init; } = 4.5f;

    public CompassSettings Normalized() => this with
    {
        Hand = Enum.IsDefined(Hand) ? Hand : CompassHand.Right,
        Visibility = Enum.IsDefined(Visibility) ? Visibility : CompassVisibility.WhileFlying,
        Glow = Enum.IsDefined(Glow) ? Glow : CompassGlow.Teal,
        SizeCentimetres = float.IsFinite(SizeCentimetres)
            ? Math.Clamp(SizeCentimetres, MinimumSizeCentimetres, MaximumSizeCentimetres)
            : Default.SizeCentimetres
    };
}

/// <summary>The real (physical) room axes, as unit vectors.</summary>
public readonly record struct CompassAxes(Vector3 Down, Vector3 Forward, Vector3 Right);

/// <summary>
/// The two eyes in the quad's frame (see <see cref="CompassView"/>), in sphere radii, with the
/// sphere centre at the origin. Each half of the stereo texture is drawn from one of them.
/// </summary>
public readonly record struct CompassEyes(Vector3 Left, Vector3 Right);

/// <summary>
/// What the stereo image needs besides the poses: each eye's position in the HMD's frame
/// (<c>IVRSystem::GetEyeToHeadTransform</c>, metres) and the sphere radius in metres.
/// </summary>
public readonly record struct CompassStereo(Vector3 LeftEyeInHead, Vector3 RightEyeInHead, float SphereRadius)
{
    /// <summary>64 mm between the eyes and the default 4.5 cm sphere, until SteamVR reports the real eyes.</summary>
    public static CompassStereo Default { get; } = new(new(-0.032f, 0, 0), new(0.032f, 0, 0), CompassSettings.Default.SizeCentimetres / 200);
}

/// <summary>
/// One compass image and where its quad sits. Axis vectors are in the quad's frame:
/// +X right on the image, +Y up on the image, +Z toward the viewer (the near half of the sphere).
/// </summary>
public readonly record struct CompassView(
    RigidPose OverlayInHand,
    CompassAxes Axes,
    Quaternion PhysicalBillboard,
    CompassEyes Eyes);

/// <summary>
/// Direction math for the gravity compass. Coordinates follow the rest of the app: right-handed,
/// metres, +Y up, -Z forward, Standing space. The flight transform D maps physical to flown poses
/// (game pose = D * physical, see <c>DriverFlightRuntime.Apply</c>).
/// </summary>
public static class CompassMath
{
    /// <summary>Sphere centre in the controller's frame, before mirroring X for the right hand: above and behind the grip (the back of the hand).</summary>
    public static readonly Vector3 LeftHandOffset = new(-0.01f, 0.05f, 0.07f);

    /// <summary>Quad width per sphere diameter; the renderer draws the sphere at <see cref="SphereFraction"/> of the texture.</summary>
    public const float SphereFraction = 0.88f;

    public static Vector3 HandOffset(CompassHand hand) =>
        hand == CompassHand.Right ? LeftHandOffset with { X = -LeftHandOffset.X } : LeftHandOffset;

    /// <summary>
    /// Where real gravity, forward and right point in the flown space. Physical down is -Y in
    /// Standing space; the flight shows a physical direction v at D.Orientation * v, so the real axes
    /// in flown coordinates are the room axes rotated by the flight. Identity flight gives -Y, -Z, +X.
    /// </summary>
    public static CompassAxes RealAxesInFlown(RigidPose flight)
    {
        var q = flight.Orientation;
        return new(Vector3.Transform(-Vector3.UnitY, q), Vector3.Transform(-Vector3.UnitZ, q), Vector3.Transform(Vector3.UnitX, q));
    }

    /// <summary>
    /// Orientation of a quad at <paramref name="centre"/> that faces <paramref name="eye"/> (+Z toward it),
    /// with its +Y as close to <paramref name="eyeUp"/> as possible, so the image is upright for the viewer.
    /// </summary>
    public static Quaternion Billboard(Vector3 eye, Vector3 eyeUp, Vector3 centre)
    {
        var z = eye - centre;
        z = z.LengthSquared() > 1e-10f ? Vector3.Normalize(z) : Vector3.UnitZ;
        var y = eyeUp - Vector3.Dot(eyeUp, z) * z;
        if (y.LengthSquared() < 1e-8f)
        {
            // Looking straight along eyeUp: any upright is fine, pick one stable axis.
            var fallback = MathF.Abs(z.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
            y = Vector3.Cross(z, fallback);
        }
        y = Vector3.Normalize(y);
        var x = Vector3.Cross(y, z);
        var basis = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
    }

    /// <summary>
    /// The compass for one moment. The quad faces the flown head, the axes are the real axes
    /// seen in that quad's frame, and <see cref="CompassView.OverlayInHand"/> is the quad relative to
    /// the flown controller (what <c>SetOverlayTransformTrackedDeviceRelative</c> takes, since SteamVR
    /// places the overlay on the flown controller pose).
    /// </summary>
    /// <param name="heldPhysicalBillboard">
    /// Keep this quad orientation (physical frame, from an earlier view) instead of facing the head
    /// again, so a transform update between two images keeps the arrows on the drawn image correct.
    /// </param>
    /// <param name="stereo">Eye offsets and sphere size for <see cref="CompassView.Eyes"/>; <see cref="CompassStereo.Default"/> when null.</param>
    public static CompassView Compute(RigidPose flight, RigidPose physicalHead, RigidPose physicalHand, Vector3 handOffset,
        Quaternion? heldPhysicalBillboard = null, CompassStereo? stereo = null)
    {
        var flownHead = flight * physicalHead;
        var flownHand = flight * physicalHand;
        var centre = flownHand.Transform(handOffset);
        var billboard = heldPhysicalBillboard is { } held
            ? Quaternion.Normalize(flight.Orientation * held)
            : Billboard(flownHead.Position, Vector3.Transform(Vector3.UnitY, flownHead.Orientation), centre);
        var real = RealAxesInFlown(flight);
        var toQuad = Quaternion.Conjugate(billboard);
        var axes = new CompassAxes(
            Vector3.Transform(real.Down, toQuad),
            Vector3.Transform(real.Forward, toQuad),
            Vector3.Transform(real.Right, toQuad));
        var overlayInHand = flownHand.Inverse() * new RigidPose(billboard, centre);
        var physical = Quaternion.Normalize(Quaternion.Conjugate(flight.Orientation) * billboard);
        // The eyes relative to the quad do not depend on the flight (it moves head and hand together),
        // so use the physical poses, which stay near the origin and keep float precision.
        var s = stereo ?? CompassStereo.Default;
        float radius = s.SphereRadius > 1e-4f && float.IsFinite(s.SphereRadius) ? s.SphereRadius : CompassStereo.Default.SphereRadius;
        var physicalCentre = physicalHand.Transform(handOffset);
        var toPhysicalQuad = Quaternion.Conjugate(physical);
        Vector3 Eye(Vector3 inHead) => Vector3.Transform(physicalHead.Transform(inHead) - physicalCentre, toPhysicalQuad) / radius;
        return new(overlayInHand, axes, physical, new(Eye(s.LeftEyeInHead), Eye(s.RightEyeInHead)));
    }

    /// <summary>Eyes closer than this (sphere radii) are treated as this far, so projections stay finite.</summary>
    public const float MinimumEyeDistance = 2;

    /// <summary>
    /// Where a point of the gizmo appears on the quad for one eye: the ray from the eye through
    /// the point meets the quad plane (z = 0). Point and eye are in the quad's frame in sphere radii
    /// (the sphere is the unit sphere at the origin, the eye in front at +Z), and so is the result.
    /// A point in front of the plane lands farther out than it is and one behind it closer in, by
    /// opposite amounts for the two eyes: that difference is the stereo depth.
    /// </summary>
    public static Vector2 Project(Vector3 point, Vector3 eye)
    {
        float distance = MathF.Max(eye.Z, MinimumEyeDistance);
        float scale = distance / MathF.Max(distance - point.Z, 0.25f);
        return new(eye.X + (point.X - eye.X) * scale, eye.Y + (point.Y - eye.Y) * scale);
    }

    /// <summary>
    /// Horizontal disparity of <paramref name="point"/>: its x on the left eye's image minus its x on
    /// the right eye's. Positive (crossed) means in front of the quad, toward the viewer; negative behind it.
    /// </summary>
    public static float Disparity(Vector3 point, CompassEyes eyes) => Project(point, eyes.Left).X - Project(point, eyes.Right).X;

    /// <summary>Angle between two orientations, in radians.</summary>
    public static float Angle(Quaternion a, Quaternion b)
    {
        float dot = MathF.Min(1, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
        return 2 * MathF.Acos(dot);
    }
}

/// <summary>
/// Draws one side-by-side stereo compass image (straight, not premultiplied, alpha): the left
/// eye's view in the left <see cref="Size"/> × <see cref="Size"/> half and the right eye's in the right
/// half, so the buffer is <c>2 * Size</c> pixels wide, <see cref="Size"/> tall, 4 bytes a pixel, rows packed.
/// </summary>
public interface ICompassRenderer
{
    /// <summary>Width and height of one eye's half, in pixels.</summary>
    int Size { get; }
    void Render(CompassAxes axes, CompassGlow glow, CompassEyes eyes, Span<byte> rgba);
}

public enum CompassState
{
    /// <summary>Turned off in the settings.</summary>
    Off,
    /// <summary>Waiting: flight is OFF and the compass shows only while flying.</summary>
    WaitingForFlight,
    /// <summary>The chosen controller or the HMD is not tracked.</summary>
    WaitingForTracking,
    /// <summary>SteamVR has not accepted the stereo flag or the first image yet; retrying with backoff.</summary>
    WaitingForSteamVr,
    Visible,
    /// <summary>SteamVR has no overlay interface or refused the overlay; flight is unaffected.</summary>
    Unavailable
}

/// <param name="Detail">Why it is unavailable or still waiting for SteamVR.</param>
public sealed record CompassStatus(CompassState State, CompassHand Hand, string? Detail = null)
{
    /// <summary>Image submissions or stereo-flag calls SteamVR refused so far (each kept the last good image).</summary>
    public int Refusals { get; init; }
    public string? LastRefusal { get; init; }
    /// <summary>Images sent over the last full second.</summary>
    public float ImagesPerSecond { get; init; }
    /// <summary>Mean CPU time to draw one stereo image over the last second.</summary>
    public float DrawMilliseconds { get; init; }
    /// <summary>Mean time the compass held the OpenVR lock (shared with the flight loop) per image over the last second.</summary>
    public float SteamVrMilliseconds { get; init; }
    /// <summary>Longest such hold in the last five seconds.</summary>
    public float SteamVrMaxMilliseconds { get; init; }
}
