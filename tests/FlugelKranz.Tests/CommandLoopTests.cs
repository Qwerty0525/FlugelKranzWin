using System.Diagnostics;
using System.Numerics;
using FlugelKranz.Core;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>The high-rate command loop option and rate-independent glide.</summary>
public class CommandLoopTests
{
    [Fact]
    public async Task HighRateUsesTheHighResolutionDelayAndSwitchesLive()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        Task Fake(TimeSpan interval, CancellationToken token)
        {
            calls.Enqueue(interval);
            return Task.Delay(1, token);
        }

        await FlightController.WaitForNextTick(false, Fake, CancellationToken.None);
        Assert.Empty(calls);
        await FlightController.WaitForNextTick(true, Fake, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(FlightController.HighRateIntervalMilliseconds), Assert.Single(calls));

        bool highRate = false;
        var settings = FlugelKranzSettings.Default;
        await using var controller = new FlightController(
            () => new IdleRuntime(), new Progress<FlightStatus>(), () => settings with { HighRateCommandLoop = Volatile.Read(ref highRate) }, Fake);
        controller.SetEnabled(true);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(calls);
        Volatile.Write(ref highRate, true);
        var watch = Stopwatch.StartNew();
        while (calls.Count < 20 && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(calls.Count >= 20, $"high-rate delay called {calls.Count} times");
        Volatile.Write(ref highRate, false);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        int afterOff = calls.Count;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(calls.Count <= afterOff + 1, "the legacy delay is used again after switching off");
    }

    [Theory]
    [InlineData(1f / 64)]
    [InlineData(0.003f)]
    public void GlideDistanceAndStopDoNotDependOnTheLoopRate(float tick)
    {
        var settings = FlightMotionSettings.Default with
        {
            HeadPilotEnabled = false,
            InertiaAccelerationBoostEnabled = false,
            DecelerationExemptionEnabled = false,
            DragSmoothSeconds = 0,
            DragAccelerationMultiplier = 1
        };
        var engine = new FreeFlightManipulator(RigidPose.Identity) { FixedVelocityWindow = tick < 0.01f };
        float x = 0;
        engine.Update(Frame(x, 0), tick, settings);
        engine.Update(Frame(x, 1), tick, settings);
        // Drag the hand at 1 m/s for 0.25 s, with some sample noise on each tick.
        var noise = new Random(7);
        for (float t = 0; t < 0.25f; t += tick)
        {
            x += tick;
            engine.Update(Frame(x + (float)(noise.NextDouble() - 0.5) * 0.0004f, 1), tick, settings);
        }
        float releasedAt = engine.Update(Frame(x, 0), tick, settings).Position.X;

        float seconds = 0;
        float position = releasedAt;
        while (engine.HasLinearInertia && seconds < 10)
        {
            position = engine.Update(Frame(x, 0), tick, settings).Position.X;
            seconds += tick;
        }

        // Released at -1 m/s and decaying at 2/s: about 0.5 m of glide, stopping after about 1.4 s (below 0.064 m/s).
        float glide = releasedAt - position;
        Assert.InRange(glide, 0.40f, 0.55f);
        Assert.InRange(seconds, 1.0f, 2.0f);
        Assert.False(engine.HasLinearInertia);
    }

    private static InputFrame Frame(float leftX, float leftGrip) => new(
        new(Quaternion.Identity, new(0, 1.7f, 0)),
        true,
        new(new(Quaternion.Identity, new(leftX, 0, 0)), leftGrip, 0, 0, true),
        new(new(Quaternion.Identity, Vector3.Zero), 0, 0, 0, true));

    private sealed class IdleRuntime : IFlightRuntime
    {
        public RigidPose OriginalOffset => RigidPose.Identity;
        public RigidPose CurrentOffset { get; private set; } = RigidPose.Identity;
        public InputFrame ReadPhysical() => Frame(0, 0);
        public void Apply(RigidPose value) => CurrentOffset = value;
        public void Restore() { }
        public void Dispose() { }
    }
}
