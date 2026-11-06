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

    /// <summary>Off for new configs; a saved value is kept.</summary>
    public bool Enabled { get; init; }
    /// <summary>Right by default: wrist-watch style overlays usually sit on the left wrist.</summary>
    public CompassHand Hand { get; init; } = CompassHand.Right;
    public CompassVisibility Visibility { get; init; } = CompassVisibility.WhileFlying;
    public CompassGlow Glow { get; init; } = CompassGlow.Teal;
    /// <summary>Also mark the flown (VRChat) world's down with a small white dot.</summary>
    public bool ShowWorldDown { get; init; } = true;
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
public readonly record struct CompassAxes(Vector3 Down, Vector3 Forward, Vector3 Right)
{
    /// <summary>
    /// The flown (VRChat) world's down in the same frame, or zero when it is not shown. Before any
    /// flight it equals <see cref="Down"/>: the world, the tracking space and the room agree.
    /// </summary>
    public Vector3 WorldDown { get; init; }
}

/// <summary>
/// One compass image and where its quad sits. Axis vectors are in the quad's frame:
/// +X right on the image, +Y up on the image, +Z toward the viewer (the near half of the sphere).
/// </summary>
public readonly record struct CompassView(
    RigidPose OverlayInHand,
    CompassAxes Axes,
    Quaternion PhysicalBillboard);

/// <summary>
/// Direction math for the gravity compass. Coordinates follow the rest of the app: right-handed,
/// metres, +Y up, -Z forward, Standing space. The flight transform D maps physical to flown poses
/// (game pose = D * physical, see <c>DriverFlightRuntime.Apply</c>).
/// </summary>
public static class CompassMath
{
    /// <summary>Sphere centre in the controller's frame, before mirroring X for the right hand: above and behind the grip (the back of the hand).</summary>
    public static readonly Vector3 LeftHandOffset = new(-0.01f, 0.05f, 0.07f);

    /// <summary>
    /// Sphere diameter per texture (and quad) width. The rest is room for the edge pointer just
    /// outside the rim; the sphere keeps its set size, only the quad around it is wider.
    /// </summary>
    public const float SphereFraction = 0.76f;

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
    public static CompassView Compute(RigidPose flight, RigidPose physicalHead, RigidPose physicalHand, Vector3 handOffset,
        Quaternion? heldPhysicalBillboard = null)
    {
        var flownHead = flight * physicalHead;
        var flownHand = flight * physicalHand;
        var centre = flownHand.Transform(handOffset);
        var billboard = heldPhysicalBillboard is { } held
            ? Quaternion.Normalize(flight.Orientation * held)
            : Billboard(flownHead.Position, Vector3.Transform(Vector3.UnitY, flownHead.Orientation), centre);
        var real = RealAxesInFlown(flight);
        var toQuad = Quaternion.Conjugate(billboard);
        // The flown world's down is -Y of the flown (tracking) space, where the quad lives.
        var axes = new CompassAxes(
            Vector3.Transform(real.Down, toQuad),
            Vector3.Transform(real.Forward, toQuad),
            Vector3.Transform(real.Right, toQuad))
        { WorldDown = Vector3.Transform(-Vector3.UnitY, toQuad) };
        var overlayInHand = flownHand.Inverse() * new RigidPose(billboard, centre);
        var physical = Quaternion.Normalize(Quaternion.Conjugate(flight.Orientation) * billboard);
        return new(overlayInHand, axes, physical);
    }

    /// <summary>Angle between two orientations, in radians.</summary>
    public static float Angle(Quaternion a, Quaternion b)
    {
        float dot = MathF.Min(1, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
        return 2 * MathF.Acos(dot);
    }
}

/// <summary>
/// The navball: the half of the sphere facing the viewer, shaded by the real frame (sky above the
/// real horizon, ground below), with real down as a dot at its pole (a hollow ring when the pole is
/// on the far half) and a pointer just outside the rim toward real down. Image coordinates are the
/// quad's x (right) and y (up) in sphere radii; the visible face at (x, y) is the sphere point
/// (x, y, sqrt(1 - x² - y²)).
/// </summary>
public static class NavballMath
{
    /// <summary>Real up in the quad's frame.</summary>
    public static Vector3 Up(CompassAxes axes) => -axes.Down;

    /// <summary>The sphere point seen at <paramref name="image"/> (inside the unit disc) on the near half.</summary>
    public static Vector3 FrontPoint(Vector2 image)
    {
        float z2 = 1 - image.LengthSquared();
        return new(image.X, image.Y, z2 > 0 ? MathF.Sqrt(z2) : 0);
    }

    /// <summary>True when the visible face at <paramref name="image"/> lies above the real horizon.</summary>
    public static bool IsSky(Vector2 image, CompassAxes axes) => Vector3.Dot(FrontPoint(image), Up(axes)) > 0;

    /// <summary>True when real down lies on the near half (drawn as a dot); false on the far half (a ring).</summary>
    public static bool PoleOnFront(CompassAxes axes) => axes.Down.Z >= 0;

    /// <summary>Where the down pole appears on the image (front or back, both project inside the disc).</summary>
    public static Vector2 PoleInImage(CompassAxes axes) => new(axes.Down.X, axes.Down.Y);

    /// <summary>
    /// Unit direction on the image toward real down, for the edge pointer. Straight down on the image
    /// when real down points straight at or away from the viewer and has no direction on the image.
    /// </summary>
    public static Vector2 PointerDirection(CompassAxes axes)
    {
        var onImage = PoleInImage(axes);
        return onImage.LengthSquared() > 1e-8f ? Vector2.Normalize(onImage) : -Vector2.UnitY;
    }

    /// <summary>The pointer's angle on the image in degrees, counter-clockwise from +x: -90 is straight down.</summary>
    public static float PointerDegrees(CompassAxes axes)
    {
        var direction = PointerDirection(axes);
        return MathF.Atan2(direction.Y, direction.X) * 180 / MathF.PI;
    }

    /// <summary>True when the flown world's down is to be drawn (<see cref="CompassAxes.WorldDown"/> is set).</summary>
    public static bool HasWorldDown(CompassAxes axes) => axes.WorldDown.LengthSquared() > 0.5f;

    /// <summary>True when the flown world's down lies on the near half (a white dot); false on the far half (a faint ring).</summary>
    public static bool WorldDownOnFront(CompassAxes axes) => axes.WorldDown.Z >= 0;

    /// <summary>Where the flown world's down appears on the image.</summary>
    public static Vector2 WorldDownInImage(CompassAxes axes) => new(axes.WorldDown.X, axes.WorldDown.Y);

    /// <summary>Angle between real down and the flown world's down, in degrees (0 before any flight).</summary>
    public static float DownsApartDegrees(CompassAxes axes)
    {
        float cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(axes.Down), Vector3.Normalize(axes.WorldDown)), -1, 1);
        return MathF.Acos(cos) * 180 / MathF.PI;
    }

    /// <summary>
    /// The real horizon's front half: it runs from <c>Side</c> through <c>Near</c> (its point closest
    /// to the viewer) to <c>-Side</c>, both on the rim at <c>±Side</c>. <c>SkyRim</c> is the rim point
    /// highest in the real frame. Null when real up points straight at or away from the viewer:
    /// the horizon is then the rim itself and the whole face is sky (<see cref="IsSky"/> at the centre) or ground.
    /// </summary>
    public static (Vector3 Side, Vector3 Near, Vector3 SkyRim)? Horizon(CompassAxes axes)
    {
        var up = Vector3.Normalize(Up(axes));
        var side = Vector3.Cross(up, Vector3.UnitZ);
        if (side.LengthSquared() < 1e-8f) return null;
        side = Vector3.Normalize(side);
        var near = Vector3.Normalize(Vector3.UnitZ - up.Z * up);
        var skyRim = Vector3.Normalize(new Vector3(up.X, up.Y, 0));
        return (side, near, skyRim);
    }
}

/// <summary>Draws one compass image into a square RGBA buffer (straight, not premultiplied, alpha).</summary>
public interface ICompassRenderer
{
    /// <summary>Texture width and height in pixels.</summary>
    int Size { get; }
    void Render(CompassAxes axes, CompassGlow glow, Span<byte> rgba);
}

public enum CompassState
{
    /// <summary>Turned off in the settings.</summary>
    Off,
    /// <summary>Waiting: flight is OFF and the compass shows only while flying.</summary>
    WaitingForFlight,
    /// <summary>The chosen controller or the HMD is not tracked.</summary>
    WaitingForTracking,
    /// <summary>SteamVR has not accepted the first image yet; retrying with backoff.</summary>
    WaitingForSteamVr,
    Visible,
    /// <summary>SteamVR has no overlay interface or refused the overlay; flight is unaffected.</summary>
    Unavailable
}

/// <param name="Detail">Why it is unavailable or still waiting for SteamVR.</param>
public sealed record CompassStatus(CompassState State, CompassHand Hand, string? Detail = null)
{
    /// <summary>Image submissions SteamVR refused so far (each kept the last good image).</summary>
    public int Refusals { get; init; }
    public string? LastRefusal { get; init; }
    /// <summary>Images sent over the last full second.</summary>
    public float ImagesPerSecond { get; init; }
    /// <summary>Mean CPU time to draw one image over the last second.</summary>
    public float DrawMilliseconds { get; init; }
    /// <summary>Mean time the compass held the OpenVR lock (shared with the flight loop) per image over the last second.</summary>
    public float SteamVrMilliseconds { get; init; }
    /// <summary>Longest such hold in the last five seconds.</summary>
    public float SteamVrMaxMilliseconds { get; init; }
}
