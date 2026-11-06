using System.Numerics;
using FlugelKranz.Core;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>Direction math for the gravity compass: real axes under the flight transform.</summary>
public class CompassMathTests
{
    private const float Tolerance = 1e-4f;
    private static readonly RigidPose Head = new(Quaternion.Identity, new(0, 1.6f, 0));
    // Left hand held in front, below the eyes.
    private static readonly RigidPose Hand = new(Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.2f), new(-0.15f, 1.15f, -0.35f));

    private static RigidPose Yaw(float degrees) => new(Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(degrees)), new(5, -2, 7));
    private static RigidPose Pitch(float degrees) => new(Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(degrees)), new(-1, 3, 2));
    private static RigidPose Roll(float degrees) => new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(degrees)), new(0, 10, -4));
    private static RigidPose Combined() => Yaw(37) * Pitch(-58) * Roll(121) with { Position = new(12, -3, 40) };
    private static float Radians(float degrees) => degrees * MathF.PI / 180;

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < Tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void IdentityFlightKeepsTheRoomAxes()
    {
        var axes = CompassMath.RealAxesInFlown(RigidPose.Identity);
        Near(-Vector3.UnitY, axes.Down);
        Near(-Vector3.UnitZ, axes.Forward);
        Near(Vector3.UnitX, axes.Right);
    }

    [Fact]
    public void PureYawLeavesGravityAndTurnsForwardAndRight()
    {
        var axes = CompassMath.RealAxesInFlown(Yaw(90));
        Near(-Vector3.UnitY, axes.Down);
        Near(-Vector3.UnitX, axes.Forward);
        Near(-Vector3.UnitZ, axes.Right);
    }

    [Fact]
    public void PitchTiltsGravityTowardForward()
    {
        Near(-Vector3.UnitZ, CompassMath.RealAxesInFlown(Pitch(90)).Down);
        var half = CompassMath.RealAxesInFlown(Pitch(45));
        Near(Vector3.Normalize(new(0, -1, -1)), half.Down);
        Near(Vector3.UnitX, half.Right);
    }

    [Fact]
    public void RollTiltsGravitySideways()
    {
        var axes = CompassMath.RealAxesInFlown(Roll(90));
        Near(Vector3.UnitX, axes.Down);
        Near(-Vector3.UnitZ, axes.Forward);
        Near(Vector3.UnitY, axes.Right);
    }

    [Fact]
    public void CombinedFlightRotatesAllThreeAxesTogether()
    {
        var flight = Combined();
        var axes = CompassMath.RealAxesInFlown(flight);
        Near(Vector3.Transform(-Vector3.UnitY, flight.Orientation), axes.Down);
        Near(Vector3.Transform(-Vector3.UnitZ, flight.Orientation), axes.Forward);
        Near(Vector3.Transform(Vector3.UnitX, flight.Orientation), axes.Right);
        Assert.True(MathF.Abs(Vector3.Dot(axes.Down, axes.Forward)) < Tolerance);
        Assert.True(MathF.Abs(Vector3.Dot(axes.Down, axes.Right)) < Tolerance);
        // Right-handed: right x up = back.
        Near(-axes.Forward, Vector3.Cross(axes.Right, -axes.Down));
        // Translation never changes a direction.
        Near(axes.Down, CompassMath.RealAxesInFlown(flight with { Position = Vector3.Zero }).Down);
    }

    [Fact]
    public void IdentityImageShowsGravityStraightDownAndForwardIntoTheScene()
    {
        var view = CompassMath.Compute(RigidPose.Identity, Head, Hand, CompassMath.HandOffset(CompassHand.Left));
        // Looking down at the hand: gravity is straight down on the image and leans away from the
        // eye by the viewing angle (about 42 degrees below the eye line here).
        Assert.True(MathF.Abs(view.Axes.Down.X) < 0.02f, $"down {view.Axes.Down}");
        Assert.True(view.Axes.Down.Y < -0.6f && view.Axes.Down.Z < 0, $"down {view.Axes.Down}");
        Assert.True(view.Axes.Forward.Z < -0.4f, $"forward {view.Axes.Forward} points away from the viewer (far half)");
        // The hand is to the left, so the view is oblique and right leans a little.
        Assert.True(view.Axes.Right.X > 0.85f, $"right {view.Axes.Right}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheImageAndQuadOnTheHandDoNotDependOnTheFlight(int which)
    {
        // The flight moves head, hand and gravity together, so the compass seen from the eye keeps
        // pointing at the real floor however the world is turned.
        RigidPose flight = which switch { 0 => Yaw(90), 1 => Pitch(45), 2 => Roll(90), 3 => Combined(), _ => Pitch(-170) };
        var offset = CompassMath.HandOffset(CompassHand.Right);
        var still = CompassMath.Compute(RigidPose.Identity, Head, Hand, offset);
        var flown = CompassMath.Compute(flight, Head, Hand, offset);
        Near(still.Axes.Down, flown.Axes.Down);
        Near(still.Axes.Forward, flown.Axes.Forward);
        Near(still.Axes.Right, flown.Axes.Right);
        Assert.True(still.OverlayInHand.NearlyEquals(flown.OverlayInHand, 1e-4f));
        Assert.True(CompassMath.Angle(still.PhysicalBillboard, flown.PhysicalBillboard) < 1e-3f);
    }

    [Fact]
    public void RolledFlightTiltsTheArrowAgainstTheFlownWorld()
    {
        // In flown coordinates the real down vector is 90 degrees from the flown world's down.
        var flight = Roll(90);
        var view = CompassMath.Compute(flight, Head, Hand, CompassMath.LeftHandOffset);
        var quadInFlown = (flight * Hand * view.OverlayInHand).Orientation;
        var downInFlown = Vector3.Transform(view.Axes.Down, quadInFlown);
        Near(Vector3.UnitX, downInFlown);
        float angle = MathF.Acos(Vector3.Dot(downInFlown, -Vector3.UnitY)) * 180 / MathF.PI;
        Assert.InRange(angle, 89.9f, 90.1f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OverlaySitsOnTheHandWithoutFlightAndOnTheFlownHandWithFlight(int which)
    {
        // SteamVR places a device-relative overlay on the controller pose it has, which the driver
        // has already flown (game pose = D * physical). So the overlay must land at the flown hand.
        RigidPose flight = which switch
        {
            0 => RigidPose.Identity,
            1 => new(Quaternion.Identity, new(25, -4, 130)),
            _ => new(Quaternion.CreateFromYawPitchRoll(1.2f, -0.8f, 2.1f), new(-60, 15, 42))
        };
        var offset = CompassMath.HandOffset(CompassHand.Right);
        var view = CompassMath.Compute(flight, Head, Hand, offset);
        var flownHand = flight * Hand;
        var overlayInSteamVr = flownHand * view.OverlayInHand;
        // Far from the origin float precision is about a millimetre.
        static void Close(System.Numerics.Vector3 expected, System.Numerics.Vector3 actual) =>
            Assert.True(System.Numerics.Vector3.Distance(expected, actual) < 0.005f, $"expected {expected}, got {actual}");
        Close(flownHand.Transform(offset), overlayInSteamVr.Position);
        // Seen from the flown head it sits where the physical hand is seen from the physical head.
        var fromHead = (flight * Head).Inverse() * overlayInSteamVr;
        Close((Head.Inverse() * Hand).Transform(offset), fromHead.Position);
        if (which == 0) Near(Hand.Transform(offset), overlayInSteamVr.Position);
    }

    [Fact]
    public void QuadFacesTheHeadAndSitsAboveTheBackOfTheHand()
    {
        var flight = Combined();
        var view = CompassMath.Compute(flight, Head, Hand, CompassMath.LeftHandOffset);
        var quad = flight * Hand * view.OverlayInHand;
        var head = flight * Head;
        Near((flight * Hand).Transform(CompassMath.LeftHandOffset), quad.Position);
        var normal = Vector3.Transform(Vector3.UnitZ, quad.Orientation);
        Near(Vector3.Normalize(head.Position - quad.Position), normal);
        // Upright for the eye: quad +Y lies in the plane of the head's up and the view line.
        var up = Vector3.Transform(Vector3.UnitY, quad.Orientation);
        Assert.True(Vector3.Dot(up, Vector3.Transform(Vector3.UnitY, head.Orientation)) > 0.5f);
        Assert.True(MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitX, quad.Orientation),
            Vector3.Transform(Vector3.UnitY, head.Orientation))) < Tolerance);
        Assert.Equal(-CompassMath.LeftHandOffset.X, CompassMath.HandOffset(CompassHand.Right).X);
    }

    [Fact]
    public void TiltedHeadSeesGravityTiltedTheOtherWayOnTheImage()
    {
        // Physical head tilted 30 degrees toward the left shoulder: real down appears toward the left ear.
        var head = Head with { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(30)) };
        var hand = new RigidPose(Quaternion.Identity, new(0, 1.0f, -0.5f));
        var view = CompassMath.Compute(Roll(90), head, hand, Vector3.Zero);
        var image = Vector2.Normalize(new(view.Axes.Down.X, view.Axes.Down.Y));
        float degrees = MathF.Atan2(image.X, -image.Y) * 180 / MathF.PI;
        // 30 degrees of roll, plus some from looking down at the hand off the eye's axis.
        Assert.InRange(degrees, -45, -25);
    }

    [Fact]
    public void HeldBillboardKeepsTheImageFixedInTheRoomWhileTheHandTurns()
    {
        var flight = Combined();
        var first = CompassMath.Compute(flight, Head, Hand, CompassMath.LeftHandOffset);
        var turned = Hand with { Orientation = Quaternion.CreateFromYawPitchRoll(1.0f, 0.2f, -0.6f) * Hand.Orientation };
        var held = CompassMath.Compute(flight, Head, turned, CompassMath.LeftHandOffset, first.PhysicalBillboard);
        Near(first.Axes.Down, held.Axes.Down);
        Near(first.Axes.Forward, held.Axes.Forward);
        var quad = flight * turned * held.OverlayInHand;
        Assert.True(CompassMath.Angle(flight.Orientation * first.PhysicalBillboard, quad.Orientation) < 1e-3f);
    }

    [Fact]
    public void SettingsAreClampedAndDefaultToTheApprovedDesign()
    {
        var defaults = CompassSettings.Default;
        Assert.True(defaults.Enabled);
        Assert.Equal(CompassHand.Right, defaults.Hand);
        Assert.Equal(CompassVisibility.WhileFlying, defaults.Visibility);
        Assert.Equal(CompassGlow.Teal, defaults.Glow);
        Assert.Equal(4.5f, defaults.SizeCentimetres);
        Assert.Equal(8, (defaults with { SizeCentimetres = 50 }).Normalized().SizeCentimetres);
        Assert.Equal(3, (defaults with { SizeCentimetres = 0 }).Normalized().SizeCentimetres);
        Assert.Equal(4.5f, (defaults with { SizeCentimetres = float.NaN }).Normalized().SizeCentimetres);
        Assert.Equal(CompassGlow.Teal, (defaults with { Glow = (CompassGlow)9 }).Normalized().Glow);
        Assert.Equal(CompassHand.Right, (defaults with { Hand = (CompassHand)9 }).Normalized().Hand);
    }
}
