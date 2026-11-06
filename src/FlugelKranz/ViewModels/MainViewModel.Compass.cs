using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlugelKranz.Compass;
using FlugelKranz.Core;

namespace FlugelKranz.ViewModels;

/// <summary>Gravity compass settings ("compass" in config.json) and its status line.</summary>
public sealed partial class MainViewModel
{
    private readonly CompassRenderer compassRenderer = new();
    private CompassStatus? compassStatus;

    [ObservableProperty] private bool compassEnabled = CompassSettings.Default.Enabled;
    /// <summary>0 = left hand, 1 = right hand.</summary>
    [ObservableProperty] private int compassHandIndex = (int)CompassSettings.Default.Hand;
    /// <summary>0 = only while flying, 1 = always (while connected).</summary>
    [ObservableProperty] private int compassVisibilityIndex = (int)CompassSettings.Default.Visibility;
    /// <summary>0 = teal, 1 = lime, 2 = amber.</summary>
    [ObservableProperty] private int compassGlowIndex = (int)CompassSettings.Default.Glow;
    [ObservableProperty] private bool compassShowWorldDown = CompassSettings.Default.ShowWorldDown;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompassSizeLabel))]
    private double compassSizeCentimetres = CompassSettings.Default.SizeCentimetres;

    public string CompassSizeLabel => Strings.Format("settings.compass.size", CompassSizeCentimetres);

    public string CompassStatusText
    {
        get
        {
            if (!CompassEnabled) return Strings["settings.compass.status.off"];
            if (compassStatus is not { } status || !IsConnected) return Strings["settings.compass.status.disconnected"];
            string hand = Strings[status.Hand == CompassHand.Right ? "settings.compass.hand.right" : "settings.compass.hand.left"];
            return status.State switch
            {
                CompassState.Off => Strings["settings.compass.status.off"],
                CompassState.WaitingForFlight => Strings["settings.compass.status.waitingForFlight"],
                CompassState.WaitingForTracking => Strings.Format("settings.compass.status.waitingForTracking", hand),
                CompassState.WaitingForSteamVr => Strings.Format("settings.compass.status.waitingForSteamVr",
                    status.Refusals, status.LastRefusal ?? status.Detail ?? ""),
                CompassState.Visible => VisibleCompassText(status, hand),
                _ => Strings.Format("settings.compass.status.unavailable", status.Detail ?? "")
            };
        }
    }

    private string VisibleCompassText(CompassStatus status, string hand)
    {
        string text = Strings.Format("settings.compass.status.visible", hand) + "\n" +
            Strings.Format("settings.compass.status.stats", status.ImagesPerSecond, status.DrawMilliseconds,
                status.SteamVrMilliseconds, status.SteamVrMaxMilliseconds);
        if (status.Refusals > 0)
            text += "\n" + Strings.Format("settings.compass.status.uploadRetries", status.Refusals, status.LastRefusal ?? "");
        return text;
    }

    private static readonly string[] CompassSettingNames =
    [
        nameof(CompassEnabled), nameof(CompassHandIndex), nameof(CompassVisibilityIndex),
        nameof(CompassGlowIndex), nameof(CompassShowWorldDown), nameof(CompassSizeCentimetres)
    ];

    /// <summary>Read by the compass thread on every update, so changes apply live.</summary>
    private CompassSettings CreateCompassSettings() => new CompassSettings
    {
        Enabled = CompassEnabled,
        Hand = (CompassHand)CompassHandIndex,
        Visibility = (CompassVisibility)CompassVisibilityIndex,
        Glow = (CompassGlow)CompassGlowIndex,
        ShowWorldDown = CompassShowWorldDown,
        SizeCentimetres = (float)CompassSizeCentimetres
    }.Normalized();

    private void ApplyCompassSettings(CompassSettings settings)
    {
        settings = settings.Normalized();
        CompassEnabled = settings.Enabled;
        CompassHandIndex = (int)settings.Hand;
        CompassVisibilityIndex = (int)settings.Visibility;
        CompassGlowIndex = (int)settings.Glow;
        CompassShowWorldDown = settings.ShowWorldDown;
        CompassSizeCentimetres = Math.Round(settings.SizeCentimetres, 2);
    }

    private void UpdateCompassStatus(FlightStatus state)
    {
        compassStatus = state.Connected ? state.Compass ?? compassStatus : null;
        OnPropertyChanged(nameof(CompassStatusText));
    }

    partial void OnCompassEnabledChanged(bool value) => OnPropertyChanged(nameof(CompassStatusText));

    /// <summary>Release or test build: the compass's default on / off follows it.</summary>
    public BuildProfile BuildProfile { get; }

    [RelayCommand] private void ResetCompassEnabled() => CompassEnabled = BuildProfile.CompassDefaults().Enabled;
    [RelayCommand] private void ResetCompassHand() => CompassHandIndex = (int)CompassSettings.Default.Hand;
    [RelayCommand] private void ResetCompassVisibility() => CompassVisibilityIndex = (int)CompassSettings.Default.Visibility;
    [RelayCommand] private void ResetCompassGlow() => CompassGlowIndex = (int)CompassSettings.Default.Glow;
    [RelayCommand] private void ResetCompassShowWorldDown() => CompassShowWorldDown = CompassSettings.Default.ShowWorldDown;
    [RelayCommand] private void ResetCompassSize() => CompassSizeCentimetres = CompassSettings.Default.SizeCentimetres;
}
