using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlugelKranz.Core;
using FlugelKranz.Localization;
using FlugelKranz.OpenXR;
using FlugelKranz.OpenVR;
using System.Numerics;

namespace FlugelKranz.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly FlightController controller;
    private readonly SettingsStore settingsStore;
    private CancellationTokenSource? settingsAnimation;
    private double settingsPanelTargetWidth = 430;
    private bool closing;
    private readonly bool enableIndependentDrag;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool isEnabled;
    [ObservableProperty] private bool isSettingsOpen;
    [ObservableProperty] private double settingsPanelWidth;
    [ObservableProperty] private bool isConnected;
    private UiMessage statusMessage = UiMessage.FromKey("status.off.initial");
    [ObservableProperty] private string inputDiagnostics = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftStatus))]
    private bool isDragging;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightStatus))]
    private bool isTurning;
    private RigidPose? shownReferenceSpaceOffset;
    private Vector3? shownReferenceSpaceMovement;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private FlightMode mode = FlightMode.InfiniteWalking;
    [ObservableProperty] private bool headPilotEnabled;
    [ObservableProperty] private bool dragEnabled;
    [ObservableProperty] private bool useHeadTurnOrigin;
    [ObservableProperty] private bool inertiaCutoffEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragCutoffLabel))]
    private double dragCutoffCentimetresPerSecond = 40;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TurnCutoffLabel))]
    private double turnCutoffDegreesPerSecond = 45;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragAccelerationLabel))]
    private double dragAccelerationMultiplier = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TurnAccelerationLabel))]
    private double turnAccelerationMultiplier = 0.4;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZAccelerationLabel))]
    private double zAccelerationMultiplier = 2;
    [ObservableProperty] private bool inertiaAccelerationBoostEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InertiaAccelerationBoostMaximumLabel))]
    private double inertiaAccelerationBoostMaximumMultiplier = 4;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VectorRotationLabel))]
    private double vectorRotationMultiplier = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InertiaDecelerationLabel))]
    private double inertiaDecelerationPerSecond = 2;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InertiaStopDisplacementLabel))]
    private double inertiaStopDisplacementMetres = 0.001;
    [ObservableProperty] private bool decelerationExemptionEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragDecelerationExemptionDurationLabel))]
    private double dragDecelerationExemptionDurationRatio = 0.2;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TurnDecelerationExemptionDurationLabel))]
    private double turnDecelerationExemptionDurationRatio = 0.15;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecelerationExemptionStrengthLabel))]
    private double decelerationExemptionStrength = 0.9;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragSmoothLabel))]
    private double dragSmoothSeconds = 0.01;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TurnSmoothLabel))]
    private double turnSmoothSeconds = 0.05;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfiniteDragSmoothLabel))]
    private double infiniteDragSmoothSeconds;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfiniteTurnSmoothLabel))]
    private double infiniteTurnSmoothSeconds;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfiniteTurnHeadSmoothLabel))]
    private double infiniteTurnHeadSmoothSeconds;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfiniteWalkingBoostLabel))]
    private double infiniteWalkingBoostMultiplier = 0.6;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrakeRampLabel))]
    private double brakeRampSeconds = 0.4;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValveIndexPositionDeadZoneLabel))]
    private double valveIndexPositionDeadZone = 0.3;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValveIndexForceThresholdLabel))]
    private double valveIndexForceThreshold = 0.2;
    [ObservableProperty] private bool highRateCommandLoop;
    [ObservableProperty] private bool isValveIndexDetected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValveIndexForceStatus))]
    private double leftValveIndexForce;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValveIndexForceStatus))]
    private double rightValveIndexForce;
    /// <summary>UI text for the current language; the view refreshes static text on <see cref="UiStrings.LanguageChanged"/>.</summary>
    public UiStrings Strings { get; } = new();
    public IReadOnlyList<UiLanguage> Languages => UiStrings.Languages;

    /// <summary>UI language code (e.g. "ja", "en"); saved to config.json, switched live.</summary>
    public string Language
    {
        get => Strings.Language;
        set
        {
            if (closing || !Strings.SetLanguage(value)) return;
            settingsStore.Language = Strings.Language;
            OnPropertyChanged();
            foreach (string name in LocalizedProperties) OnPropertyChanged(name);
            settingsStore.Save(CreateSettings());
        }
    }

    [RelayCommand] private void SelectLanguage(string? language) => Language = language ?? UiStrings.DefaultLanguage;

    private static readonly string[] LocalizedProperties =
    [
        nameof(Status), nameof(LeftStatus), nameof(RightStatus), nameof(ReferenceSpaceOffsetStatus),
        nameof(ToggleLabel), nameof(ModeDescription), nameof(DragCutoffLabel), nameof(TurnCutoffLabel),
        nameof(DragAccelerationLabel), nameof(TurnAccelerationLabel), nameof(ZAccelerationLabel),
        nameof(InertiaAccelerationBoostMaximumLabel), nameof(VectorRotationLabel), nameof(InertiaDecelerationLabel),
        nameof(InertiaStopDisplacementLabel), nameof(DragDecelerationExemptionDurationLabel),
        nameof(TurnDecelerationExemptionDurationLabel), nameof(DecelerationExemptionStrengthLabel),
        nameof(DragSmoothLabel), nameof(TurnSmoothLabel), nameof(InfiniteDragSmoothLabel),
        nameof(InfiniteTurnSmoothLabel), nameof(InfiniteTurnHeadSmoothLabel), nameof(InfiniteWalkingBoostLabel),
        nameof(BrakeRampLabel), nameof(ValveIndexPositionDeadZoneLabel), nameof(ValveIndexForceThresholdLabel),
        nameof(ValveIndexForceStatus), nameof(DiagnosticStatus), nameof(DiagnosticFileList), nameof(DiagnosticPlanHeader),
        nameof(DetailedDiagnosticsStatus), nameof(CompassSizeLabel), nameof(CompassStatusText)
    ];

    /// <summary>
    /// Status line shown in the settings panel. Setting it takes raw text (as Core reports it,
    /// in Japanese); reading it returns that text translated for the current language.
    /// </summary>
    public string Status
    {
        get => statusMessage.Resolve(Strings);
        set => SetStatus(UiMessage.FromText(value));
    }

    private void SetStatus(UiMessage message)
    {
        statusMessage = message;
        OnPropertyChanged(nameof(Status));
    }

    public string LeftStatus => Strings[IsDragging ? "status.drag.active" : "status.drag.idle"];
    public string RightStatus => Strings[IsTurning ? "status.turn.active" : "status.turn.idle"];
    public string ReferenceSpaceOffsetStatus => shownReferenceSpaceOffset is { } offset
        ? FormatReferenceSpaceOffset(offset, shownReferenceSpaceMovement)
        : Strings["status.stageOffset.disconnected"];
    public string ToggleLabel => Strings[IsEnabled ? "main.toggle.on" : "main.toggle.off"];
    public string ModeLabel => Mode == FlightMode.InfiniteWalking ? "I" : "F";
    public string ModeDescription => Strings[Mode == FlightMode.InfiniteWalking ? "mode.infiniteWalking" : "mode.freeFlight"];
    public string DragCutoffLabel => Strings.Format("settings.inertiaCutoff.drag", DragCutoffCentimetresPerSecond);
    public string TurnCutoffLabel => Strings.Format("settings.inertiaCutoff.turn", TurnCutoffDegreesPerSecond);
    public string DragAccelerationLabel => Strings.Format("settings.inertiaAcceleration.drag", DragAccelerationMultiplier);
    public string TurnAccelerationLabel => Strings.Format("settings.inertiaAcceleration.turn", TurnAccelerationMultiplier);
    public string ZAccelerationLabel => Strings.Format("settings.zAcceleration.multiplier", ZAccelerationMultiplier);
    public string InertiaAccelerationBoostMaximumLabel =>
        Strings.Format("settings.accelerationBoost.maximum", InertiaAccelerationBoostMaximumMultiplier);
    public string VectorRotationLabel => Strings.Format("settings.vectorRotation.multiplier", VectorRotationMultiplier);
    public string InertiaDecelerationLabel => Strings.Format("settings.deceleration.rate", InertiaDecelerationPerSecond);
    public string InertiaStopDisplacementLabel =>
        Strings.Format("settings.deceleration.stopDisplacement", InertiaStopDisplacementMetres);
    public string DragDecelerationExemptionDurationLabel =>
        Strings.Format("settings.exemption.drag", DragDecelerationExemptionDurationRatio);
    public string TurnDecelerationExemptionDurationLabel =>
        Strings.Format("settings.exemption.turn", TurnDecelerationExemptionDurationRatio);
    public string DecelerationExemptionStrengthLabel =>
        Strings.Format("settings.exemption.strength", DecelerationExemptionStrength);
    public string DragSmoothLabel => Strings.Format("settings.dragSmooth.drag", DragSmoothSeconds);
    public string TurnSmoothLabel => Strings.Format("settings.dragSmooth.turn", TurnSmoothSeconds);
    public string InfiniteDragSmoothLabel => Strings.Format("settings.infiniteSmooth.drag", InfiniteDragSmoothSeconds);
    public string InfiniteTurnSmoothLabel => Strings.Format("settings.infiniteSmooth.turn", InfiniteTurnSmoothSeconds);
    public string InfiniteTurnHeadSmoothLabel =>
        Strings.Format("settings.infiniteSmooth.turnHead", InfiniteTurnHeadSmoothSeconds);
    public string InfiniteWalkingBoostLabel =>
        Strings.Format("settings.infiniteBoost.multiplier", InfiniteWalkingBoostMultiplier);
    public string BrakeRampLabel => Strings.Format("settings.dragBrake.ramp", BrakeRampSeconds);
    public string ValveIndexPositionDeadZoneLabel =>
        Strings.Format("settings.valveIndex.positionDeadZone", ValveIndexPositionDeadZone);
    public string ValveIndexForceThresholdLabel =>
        Strings.Format("settings.valveIndex.forceThreshold", ValveIndexForceThreshold);
    public string ValveIndexForceStatus =>
        Strings.Format("status.valveIndexForce", LeftValveIndexForce, RightValveIndexForce);

    public MainViewModel(string libraryPath, string? settingsPath = null,
        Func<Func<ValveIndexInputSettings>, IFlightRuntime>? runtimeFactory = null, bool enableIndependentDrag = false)
    {
        this.enableIndependentDrag = enableIndependentDrag;
        settingsStore = new(settingsPath);
        DiagnosticSourcesFactory = () => Diagnostics.DiagnosticSources.ForThisPc(settingsStore.Path);
        var savedSettings = settingsStore.Load();
        Strings.SetLanguage(settingsStore.Language);
        ApplySettings(savedSettings);
        PropertyChanged += SettingsChanged;
        controller = new(
            () => runtimeFactory is not null ? runtimeFactory(CreateValveIndexSettings)
                : OperatingSystem.IsWindows()
                ? new DriverFlightRuntime(new CompassOptions(CreateCompassSettings, compassRenderer))
                : new MonadoFlightRuntime(libraryPath, CreateValveIndexSettings),
            new UiProgress(Update),
            CreateSettings,
            OperatingSystem.IsWindows() ? Platform.HighResolutionDelay.Wait : null);
        if (OperatingSystem.IsWindows()) LoadDriverSettingsForThisPc();
        if (enableIndependentDrag && OperatingSystem.IsWindows()) controller.RefreshDragConnection();
        if (runtimeFactory is null && !OperatingSystem.IsWindows() && File.Exists(libraryPath))
        {
            Mode = FlightMode.InfiniteWalking;
            IsEnabled = true;
            SetStatus(UiMessage.FromKey("status.preparingInfiniteWalking"));
            controller.SetEnabled(true);
        }
    }

    private void SettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(DragEnabled) or nameof(HeadPilotEnabled) or nameof(Mode) or nameof(UseHeadTurnOrigin) or
            nameof(InertiaCutoffEnabled) or nameof(DragCutoffCentimetresPerSecond) or
            nameof(TurnCutoffDegreesPerSecond) or nameof(DragAccelerationMultiplier) or
            nameof(TurnAccelerationMultiplier) or nameof(ZAccelerationMultiplier) or
            nameof(InertiaAccelerationBoostEnabled) or nameof(InertiaAccelerationBoostMaximumMultiplier) or
            nameof(VectorRotationMultiplier) or
            nameof(InertiaDecelerationPerSecond) or nameof(InertiaStopDisplacementMetres) or nameof(DecelerationExemptionEnabled) or
            nameof(DragDecelerationExemptionDurationRatio) or nameof(TurnDecelerationExemptionDurationRatio) or
            nameof(DecelerationExemptionStrength) or nameof(DragSmoothSeconds) or nameof(TurnSmoothSeconds) or
            nameof(InfiniteDragSmoothSeconds) or nameof(InfiniteTurnSmoothSeconds) or
            nameof(InfiniteTurnHeadSmoothSeconds) or nameof(InfiniteWalkingBoostMultiplier) or
            nameof(ValveIndexPositionDeadZone) or nameof(ValveIndexForceThreshold) or
            nameof(BrakeRampSeconds) or nameof(HighRateCommandLoop)) &&
            Array.IndexOf(CompassSettingNames, e.PropertyName) < 0)
            return;

        settingsStore.Save(CreateSettings());
        if (enableIndependentDrag && OperatingSystem.IsWindows() && e.PropertyName is nameof(DragEnabled) or nameof(Mode))
            controller.RefreshDragConnection();
    }

    private void ApplySettings(FlugelKranzSettings root)
    {
        root = root.Normalized();
        Mode = root.Mode;
        var settings = root.FreeFlight;
        HeadPilotEnabled = settings.HeadPilotEnabled;
        DragEnabled = settings.DragEnabled;
        UseHeadTurnOrigin = settings.TurnOrigin == TurnOrigin.Head;
        InertiaCutoffEnabled = settings.InertiaCutoffEnabled;
        DragCutoffCentimetresPerSecond = Math.Round(settings.DragCutoffMetresPerSecond * 100, 6);
        TurnCutoffDegreesPerSecond = Math.Round(settings.TurnCutoffRadiansPerSecond * 180 / Math.PI, 4);
        DragAccelerationMultiplier = Math.Round(settings.DragAccelerationMultiplier, 6);
        TurnAccelerationMultiplier = Math.Round(settings.TurnAccelerationMultiplier, 6);
        ZAccelerationMultiplier = Math.Round(settings.ZAccelerationMultiplier, 6);
        InertiaAccelerationBoostEnabled = settings.InertiaAccelerationBoostEnabled;
        InertiaAccelerationBoostMaximumMultiplier = Math.Round(settings.InertiaAccelerationBoostMaximumMultiplier, 6);
        VectorRotationMultiplier = Math.Round(settings.VectorRotationMultiplier, 6);
        InertiaDecelerationPerSecond = Math.Round(settings.InertiaDecelerationPerSecond, 6);
        InertiaStopDisplacementMetres = Math.Round(settings.InertiaStopDisplacementMetres, 9);
        DecelerationExemptionEnabled = settings.DecelerationExemptionEnabled;
        DragDecelerationExemptionDurationRatio = Math.Round(settings.DragDecelerationExemptionDurationRatio, 6);
        TurnDecelerationExemptionDurationRatio = Math.Round(settings.TurnDecelerationExemptionDurationRatio, 6);
        DecelerationExemptionStrength = Math.Round(settings.DecelerationExemptionStrength, 6);
        DragSmoothSeconds = Math.Round(settings.DragSmoothSeconds, 6);
        TurnSmoothSeconds = Math.Round(settings.TurnSmoothSeconds, 6);
        BrakeRampSeconds = Math.Round(settings.BrakeRampSeconds, 6);
        InfiniteDragSmoothSeconds = Math.Round(root.InfiniteWalking.DragSmoothSeconds, 6);
        InfiniteTurnSmoothSeconds = Math.Round(root.InfiniteWalking.TurnSmoothSeconds, 6);
        InfiniteTurnHeadSmoothSeconds = Math.Round(root.InfiniteWalking.TurnHeadSmoothSeconds, 6);
        InfiniteWalkingBoostMultiplier = Math.Round(root.InfiniteWalking.TurnMovementBoostMultiplier, 6);
        ValveIndexPositionDeadZone = Math.Round(root.ValveIndex.PositionDeadZone, 6);
        ValveIndexForceThreshold = Math.Round(root.ValveIndex.ForceThreshold, 6);
        HighRateCommandLoop = root.HighRateCommandLoop;
        ApplyCompassSettings(root.Compass);
    }

    private void ResetToDefaults(Action<FlightMotionSettings> apply)
    {
        if (closing) return;
        apply(FlightMotionSettings.Default);
    }

    [RelayCommand] private void ResetHeadPilot() => HeadPilotEnabled = FlightMotionSettings.Default.HeadPilotEnabled;
    [RelayCommand] private void ResetDragEnabled() => DragEnabled = FlightMotionSettings.Default.DragEnabled;
    [RelayCommand] private void ResetTurnOrigin() => ResetToDefaults(s => UseHeadTurnOrigin = s.TurnOrigin == TurnOrigin.Head);
    [RelayCommand] private void ResetInertiaCutoffEnabled() => ResetToDefaults(s => InertiaCutoffEnabled = s.InertiaCutoffEnabled);
    [RelayCommand] private void ResetDragCutoff() => ResetToDefaults(s => DragCutoffCentimetresPerSecond = s.DragCutoffMetresPerSecond * 100);
    [RelayCommand] private void ResetTurnCutoff() => ResetToDefaults(s => TurnCutoffDegreesPerSecond = Math.Round(s.TurnCutoffRadiansPerSecond * 180 / Math.PI, 4));
    [RelayCommand] private void ResetDragAcceleration() => ResetToDefaults(s => DragAccelerationMultiplier = s.DragAccelerationMultiplier);
    [RelayCommand] private void ResetTurnAcceleration() => ResetToDefaults(s => TurnAccelerationMultiplier = s.TurnAccelerationMultiplier);
    [RelayCommand] private void ResetZAcceleration() => ResetToDefaults(s => ZAccelerationMultiplier = s.ZAccelerationMultiplier);
    [RelayCommand] private void ResetInertiaAccelerationBoostEnabled() => ResetToDefaults(s => InertiaAccelerationBoostEnabled = s.InertiaAccelerationBoostEnabled);
    [RelayCommand] private void ResetInertiaAccelerationBoostMaximumMultiplier() => ResetToDefaults(s => InertiaAccelerationBoostMaximumMultiplier = s.InertiaAccelerationBoostMaximumMultiplier);
    [RelayCommand] private void ResetVectorRotation() => ResetToDefaults(s => VectorRotationMultiplier = s.VectorRotationMultiplier);
    [RelayCommand] private void ResetDeceleration() => ResetToDefaults(s => InertiaDecelerationPerSecond = s.InertiaDecelerationPerSecond);
    [RelayCommand] private void ResetInertiaStopDisplacement() => ResetToDefaults(s => InertiaStopDisplacementMetres = s.InertiaStopDisplacementMetres);
    [RelayCommand] private void ResetExemptionEnabled() => ResetToDefaults(s => DecelerationExemptionEnabled = s.DecelerationExemptionEnabled);
    [RelayCommand] private void ResetDragExemption() => ResetToDefaults(s => DragDecelerationExemptionDurationRatio = s.DragDecelerationExemptionDurationRatio);
    [RelayCommand] private void ResetTurnExemption() => ResetToDefaults(s => TurnDecelerationExemptionDurationRatio = s.TurnDecelerationExemptionDurationRatio);
    [RelayCommand] private void ResetExemptionStrength() => ResetToDefaults(s => DecelerationExemptionStrength = s.DecelerationExemptionStrength);
    [RelayCommand] private void ResetDragSmooth() => ResetToDefaults(s => DragSmoothSeconds = s.DragSmoothSeconds);
    [RelayCommand] private void ResetTurnSmooth() => ResetToDefaults(s => TurnSmoothSeconds = s.TurnSmoothSeconds);
    [RelayCommand] private void ResetBrakeRamp() => ResetToDefaults(s => BrakeRampSeconds = s.BrakeRampSeconds);
    [RelayCommand] private void ResetInfiniteDragSmooth() => InfiniteDragSmoothSeconds = InfiniteWalkingSettings.Default.DragSmoothSeconds;
    [RelayCommand] private void ResetInfiniteTurnSmooth() => InfiniteTurnSmoothSeconds = InfiniteWalkingSettings.Default.TurnSmoothSeconds;
    [RelayCommand] private void ResetInfiniteTurnHeadSmooth() => InfiniteTurnHeadSmoothSeconds = InfiniteWalkingSettings.Default.TurnHeadSmoothSeconds;
    [RelayCommand] private void ResetInfiniteWalkingBoost() => InfiniteWalkingBoostMultiplier = Math.Round(InfiniteWalkingSettings.Default.TurnMovementBoostMultiplier, 6);
    [RelayCommand] private void ResetValveIndexPositionDeadZone() => ValveIndexPositionDeadZone = ValveIndexInputSettings.Default.PositionDeadZone;
    [RelayCommand] private void ResetValveIndexForceThreshold() => ValveIndexForceThreshold = Math.Round(ValveIndexInputSettings.Default.ForceThreshold, 6);
    [RelayCommand] private void ResetMode() => Mode = FlightMode.InfiniteWalking;
    [RelayCommand] private void ResetHighRateCommandLoop() => HighRateCommandLoop = FlugelKranzSettings.Default.HighRateCommandLoop;
    public bool SupportsSteamVrBindings => OperatingSystem.IsWindows();
    [RelayCommand] private void OpenSteamVrBindings() => controller.OpenBindings();

    [RelayCommand]
    private void ToggleMode()
    {
        if (closing) return;
        Mode = Mode == FlightMode.InfiniteWalking ? FlightMode.FreeFlight : FlightMode.InfiniteWalking;
        SetStatus(UiMessage.FromKey(Mode == FlightMode.InfiniteWalking
            ? "runtime.modeChanged.infiniteWalking"
            : "runtime.modeChanged.freeFlight"));
    }

    [RelayCommand]
    private void Toggle()
    {
        if (closing) return;
        IsEnabled = !IsEnabled;
        SetStatus(UiMessage.FromKey(IsEnabled ? "status.preparing" : "status.off.holding"));
        controller.SetEnabled(IsEnabled);
    }

    [RelayCommand]
    private async Task ToggleSettings()
    {
        if (closing)
            return;

        IsSettingsOpen = !IsSettingsOpen;
        if (IsSettingsOpen && OperatingSystem.IsWindows()) RefreshLoadedDriverAudit();
        settingsAnimation?.Cancel();
        settingsAnimation?.Dispose();
        settingsAnimation = new CancellationTokenSource();
        var cancellationToken = settingsAnimation.Token;
        double start = SettingsPanelWidth;
        double target = IsSettingsOpen ? settingsPanelTargetWidth : 0;

        try
        {
            for (int step = 1; step <= 12; step++)
            {
                await Task.Delay(16, cancellationToken);
                double progress = step / 12d;
                progress = 1 - Math.Pow(1 - progress, 3);
                double currentTarget = IsSettingsOpen ? settingsPanelTargetWidth : 0;
                SettingsPanelWidth = start + (currentTarget - start) * progress;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void SetSettingsPanelTargetWidth(double width)
    {
        settingsPanelTargetWidth = Math.Max(0, width);
        if (IsSettingsOpen)
            SettingsPanelWidth = settingsPanelTargetWidth;
    }

    [RelayCommand]
    private void Reset()
    {
        if (closing) return;
        controller.Reset();
    }

    private void Update(FlightStatus state)
    {
        if (closing) { Console.Error.WriteLine(state.Message); return; }
        IsEnabled = state.Enabled;
        IsConnected = state.Connected;
        Mode = state.Mode;
        Status = state.Message;
        InputDiagnostics = state.InputDiagnostics ?? "";
        IsDragging = state.Dragging;
        IsTurning = state.Turning;
        if (state.ReferenceSpaceOffset is { } referenceSpaceOffset)
        {
            shownReferenceSpaceOffset = referenceSpaceOffset;
            shownReferenceSpaceMovement = state.RecentReferenceSpaceMovement;
            OnPropertyChanged(nameof(ReferenceSpaceOffsetStatus));
        }
        else if (!state.Connected && shownReferenceSpaceOffset is not null)
        {
            shownReferenceSpaceOffset = null;
            shownReferenceSpaceMovement = null;
            OnPropertyChanged(nameof(ReferenceSpaceOffsetStatus));
        }
        IsValveIndexDetected = state.LeftTrackpadForce.HasValue || state.RightTrackpadForce.HasValue;
        LeftValveIndexForce = state.LeftTrackpadForce ?? 0;
        RightValveIndexForce = state.RightTrackpadForce ?? 0;
        UpdateCompassStatus(state);
    }

    private FlugelKranzSettings CreateSettings() => new()
    {
        Mode = Mode,
        FreeFlight = new()
        {
            HeadPilotEnabled = HeadPilotEnabled,
            DragEnabled = DragEnabled,
            TurnOrigin = UseHeadTurnOrigin ? TurnOrigin.Head : TurnOrigin.TrackedElementsMidpoint,
            InertiaCutoffEnabled = InertiaCutoffEnabled,
            DragCutoffMetresPerSecond = (float)(DragCutoffCentimetresPerSecond / 100),
            TurnCutoffRadiansPerSecond = (float)(TurnCutoffDegreesPerSecond * Math.PI / 180),
            DragAccelerationMultiplier = (float)DragAccelerationMultiplier,
            TurnAccelerationMultiplier = (float)TurnAccelerationMultiplier,
            ZAccelerationMultiplier = (float)ZAccelerationMultiplier,
            InertiaAccelerationBoostEnabled = InertiaAccelerationBoostEnabled,
            InertiaAccelerationBoostMaximumMultiplier = (float)InertiaAccelerationBoostMaximumMultiplier,
            VectorRotationMultiplier = (float)VectorRotationMultiplier,
            InertiaDecelerationPerSecond = (float)InertiaDecelerationPerSecond,
            InertiaStopDisplacementMetres = (float)InertiaStopDisplacementMetres,
            DecelerationExemptionEnabled = DecelerationExemptionEnabled,
            DragDecelerationExemptionDurationRatio = (float)DragDecelerationExemptionDurationRatio,
            TurnDecelerationExemptionDurationRatio = (float)TurnDecelerationExemptionDurationRatio,
            DecelerationExemptionStrength = (float)DecelerationExemptionStrength,
            DragSmoothSeconds = (float)DragSmoothSeconds,
            TurnSmoothSeconds = (float)TurnSmoothSeconds,
            BrakeRampSeconds = (float)BrakeRampSeconds
        },
        InfiniteWalking = new()
        {
            DragSmoothSeconds = (float)InfiniteDragSmoothSeconds,
            TurnSmoothSeconds = (float)InfiniteTurnSmoothSeconds,
            TurnHeadSmoothSeconds = (float)InfiniteTurnHeadSmoothSeconds,
            TurnMovementBoostMultiplier = (float)InfiniteWalkingBoostMultiplier
        },
        ValveIndex = CreateValveIndexSettings(),
        HighRateCommandLoop = HighRateCommandLoop,
        Compass = CreateCompassSettings()
    };

    private ValveIndexInputSettings CreateValveIndexSettings() => new()
    {
        PositionDeadZone = (float)ValveIndexPositionDeadZone,
        ForceThreshold = (float)ValveIndexForceThreshold
    };

    private string FormatReferenceSpaceOffset(RigidPose offset, Vector3? recentMovement)
    {
        var movement = recentMovement ?? Vector3.Zero;
        static string G9(float value) => value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        return Strings.Format("status.stageOffset.value",
            G9(offset.Position.X), G9(offset.Position.Y), G9(offset.Position.Z),
            G9(movement.X), G9(movement.Y), G9(movement.Z), G9(movement.Length()),
            G9(offset.Orientation.X), G9(offset.Orientation.Y), G9(offset.Orientation.Z), G9(offset.Orientation.W));
    }

    public async ValueTask DisposeAsync()
    {
        closing = true;
        settingsAnimation?.Cancel();
        settingsAnimation?.Dispose();
        settingsStore.Save(CreateSettings());
        IsEnabled = false;
        SetStatus(UiMessage.FromKey("status.restoring"));
        await controller.DisposeAsync();
        compassRenderer.Dispose();
    }

    private sealed class UiProgress(Action<FlightStatus> update) : IProgress<FlightStatus>
    {
        public void Report(FlightStatus value) => Dispatcher.UIThread.Post(() => update(value));
    }
}
