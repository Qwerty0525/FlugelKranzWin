using FlugelKranz.Core;

namespace FlugelKranz.OpenVR;

public sealed class DriverFlightRuntime : IFlightRuntime, IReferenceSpaceOffsetProvider, IFlightBindings, IFlightInputControl,
    IFlightInputDiagnostics, IFlightActivity, ICompassStatusSource
{
    private readonly IOpenVrSession session;
    private readonly IDriverConnection driver;
    private readonly RigidPose standing;
    private readonly StandingOriginCheck originCheck;
    // Held for every OpenVR call from the flight loop and from the compass thread.
    private readonly object openVrGate = new();
    private readonly CompassOverlay? compass;
    private bool disposed;
    public RigidPose OriginalOffset => RigidPose.Identity;
    public RigidPose CurrentOffset { get; private set; } = RigidPose.Identity;
    public RigidPose ReferenceSpaceOffset => CurrentOffset;
    public string InputDiagnostics => session.DescribeInput();
    public CompassStatus? CompassStatus => compass?.Status;
    internal CompassOverlay? Compass => compass;

    public DriverFlightRuntime() : this(compass: null) { }
    public DriverFlightRuntime(CompassOptions? compass) : this(new OpenVrSession(), () => new DriverConnection(), compass: compass) { }
    public DriverFlightRuntime(IOpenVrSession session, Func<IDriverConnection> connect, TimeProvider? time = null,
        CompassOptions? compass = null)
    {
        this.session = session;
        originCheck = new(time ?? TimeProvider.System);
        try
        {
            standing = session.ReadWorkingStanding();
            if (!standing.IsValid) throw new InvalidOperationException("Standing 原点が不正です。");
            driver = connect();
        }
        catch { session.Dispose(); throw; }
        // The compass is optional: if it cannot start, flight still works.
        if (compass is not null)
        {
            try { this.compass = new CompassOverlay(compass, openVrGate); }
            catch (Exception exception) { Console.Error.WriteLine($"Compass: {exception.Message}"); }
        }
    }

    public InputFrame ReadPhysical()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        driver.Refresh();
        InputFrame input;
        uint left, right;
        lock (openVrGate)
        {
            // Every 100 ms, not every tick: each read makes SteamVR write a vrclient log line.
            if (originCheck.Due() && !session.ReadWorkingStanding().NearlyEquals(standing))
                throw new InvalidOperationException("SteamVRの基準空間が変更されました。飛行変換を解除して再接続してください。");
            input = session.ReadRaw();
            (left, right) = session.ControllerDevices();
        }
        var rawToPhysical = standing.Inverse();
        var head = driver.Read(0);
        HandSample ReadHand(HandSample hand, uint device)
        {
            var raw = driver.Read(device);
            bool tracked = raw.Tracked && hand.IsTracked;
            return hand with { Pose = rawToPhysical * raw.Pose, IsTracked = tracked };
        }
        var physical = input with
        {
            Head = rawToPhysical * head.Pose,
            HeadTracked = head.Tracked && input.HeadTracked,
            Left = ReadHand(input.Left, left), Right = ReadHand(input.Right, right)
        };
        compass?.Publish(physical, left, right);
        return physical;
    }

    public void Apply(RigidPose offset)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!offset.IsValid) throw new ArgumentException("空間変換が不正です。", nameof(offset));
        // Game pose S^-1 * (S * D * S^-1) * raw = D * physical.
        driver.Apply(standing * offset * standing.Inverse());
        CurrentOffset = offset;
        compass?.PublishFlight(offset);
    }

    public void SetPilotInputEnabled(bool enabled) { lock (openVrGate) session.SetPilotInputEnabled(enabled); }
    public void SetFlightActive(bool active) => compass?.SetFlightActive(active);
    public void Restore() => Apply(OriginalOffset);
    public void OpenBindings() { lock (openVrGate) session.OpenBindings(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { compass?.Dispose(); }
        finally
        {
            try { driver.Dispose(); }
            finally { session.Dispose(); }
        }
    }
}
