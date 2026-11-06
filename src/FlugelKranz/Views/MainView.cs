using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using FlugelKranz.Localization;
using FlugelKranz.ViewModels;
using System.Linq.Expressions;

namespace FlugelKranz.Views;

public sealed class MainView(MainViewModel vm) : ViewBase<MainViewModel>(vm)
{
    protected override object Build(MainViewModel model)
    {
        var root = new Grid
        {
            ClipToBounds = true,
            ColumnDefinitions = new ColumnDefinitions("*, 0")
        };
        var toggle = new Button
        {
            Width = 300,
            Height = 220,
            FontSize = 52,
            CornerRadius = new CornerRadius(48),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        }
            .Content(model, x => x.ToggleLabel)
            .Command(model, x => x.ToggleCommand);
        toggle.Classes.Add("flight-toggle");
        void UpdateToggleClass()
        {
            if (model.IsEnabled)
                toggle.Classes.Add("flight-toggle-enabled");
            else
                toggle.Classes.Remove("flight-toggle-enabled");
        }
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.IsEnabled))
                UpdateToggleClass();
        };
        UpdateToggleClass();
        var modeButton = new Button
        {
            Width = 64,
            Height = 64,
            FontSize = 28,
            CornerRadius = new CornerRadius(32),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        }
            .Content(model, x => x.ModeLabel)
            .Command(model, x => x.ToggleModeCommand);
        LocalizeTip(model.Strings, modeButton, "main.mode.tooltip");
        var settingsButton = new Button
        {
            Width = 64,
            Height = 64,
            FontSize = 28,
            CornerRadius = new CornerRadius(32),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        }
            .Content("⚙")
            .Command(model, x => x.ToggleSettingsCommand);
        var resetPoseButton = new Button
        {
            Width = 64,
            Height = 64,
            FontSize = 28,
            CornerRadius = new CornerRadius(32),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        }
            .Content("↻")
            .IsEnabled(model, x => x.IsConnected)
            .Command(model, x => x.ResetCommand);
        LocalizeTip(model.Strings, resetPoseButton, "main.resetPose.tooltip");
        var main = new Border
        {
            Padding = new Thickness(28)
        }.Classes("app-surface")
            .Child(new Grid().Children(
                LanguageSelector(model)
                    .HorizontalAlignment(HorizontalAlignment.Right)
                    .VerticalAlignment(VerticalAlignment.Top),
                new StackPanel().Orientation(Orientation.Horizontal).Spacing(16)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .VerticalAlignment(VerticalAlignment.Center).Children(
                    modeButton,
                    toggle,
                    new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 16,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                        .Children(settingsButton, resetPoseButton)
                )));
        var panel = new Border
        {
            Padding = new Thickness(20),
            ClipToBounds = true
        }.Classes("settings-surface")
            .Width(model, x => x.SettingsPanelWidth)
            .Child(new ScrollViewer
            {
                AllowAutoHide = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 12, 0)
            }.Content(SettingsPanel(model)));

        Grid.SetColumn(panel, 1);
        root.Children.Add(main);
        root.Children.Add(panel);
        root.SizeChanged += (_, args) =>
        {
            double width = args.NewSize.Width;
            model.SetSettingsPanelTargetWidth(width <= 720 ? width : width * 0.8);
        };
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.SettingsPanelWidth))
                root.ColumnDefinitions[1].Width = new GridLength(model.SettingsPanelWidth);
        };
        root.ColumnDefinitions[1].Width = new GridLength(model.SettingsPanelWidth);
        return root;
    }

    private static Control SettingsPanel(MainViewModel model) =>
        new StackPanel().Spacing(10).Children(
            new DockPanel().Children(
                Localize(model.Strings, new TextBlock(), (t, s) => t.Text = s, "settings.title").FontSize(24).FontWeight(FontWeight.SemiBold)
                    .VerticalAlignment(VerticalAlignment.Center),
                new StackPanel().Orientation(Orientation.Horizontal).Spacing(12)
                    .HorizontalAlignment(HorizontalAlignment.Right).Children(
                        LanguageSelector(model),
                        Localize(model.Strings, new Button(), (b, s) => b.Content = s, "settings.close")
                            .Command(model, x => x.ToggleSettingsCommand))
            ),
            new TextBlock().Text(model, x => x.Status).TextWrapping(TextWrapping.Wrap),
            Localize(model.Strings, new Button(), (b, s) => b.Content = s, "settings.openBindings")
                .IsVisible(model, x => x.SupportsSteamVrBindings)
                .Command(model, x => x.OpenSteamVrBindingsCommand),
            new TextBlock().Text(model, x => x.ReferenceSpaceOffsetStatus)
                .TextWrapping(TextWrapping.Wrap),
            Localize(model.Strings, new TextBlock(), (t, s) => t.Text = s, "settings.inputStatus").FontWeight(FontWeight.SemiBold),
            new TextBlock().Text(model, x => x.LeftStatus),
            new TextBlock().Text(model, x => x.RightStatus),
            new TextBlock().Text(model, x => x.InputDiagnostics).TextWrapping(TextWrapping.Wrap),
            new TextBlock().Text(model, x => x.ValveIndexForceStatus)
                .IsVisible(model, x => x.IsValveIndexDetected),
            Divider(),
            Header(model.Strings, "settings.valveIndex.header"),
            Row(model, x => x.ValveIndexPositionDeadZoneLabel,
                new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.ValveIndexPositionDeadZone),
                ResetButton(model, x => x.ResetValveIndexPositionDeadZoneCommand)),
            Row(model, x => x.ValveIndexForceThresholdLabel,
                new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.ValveIndexForceThreshold),
                ResetButton(model, x => x.ResetValveIndexForceThresholdCommand)),
            Divider(),
            Header(model.Strings, "settings.mode.header"),
            Row(model, x => x.ModeDescription, Localize(model.Strings, new Button(), (b, s) => b.Content = s, "settings.mode.toggle")
                    .Command(model, x => x.ToggleModeCommand),
                ResetButton(model, x => x.ResetModeCommand)),
            Divider(),
            Header(model.Strings, "settings.infiniteSmooth.header"),
            Row(model, x => x.InfiniteDragSmoothLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.InfiniteDragSmoothSeconds),
                ResetButton(model, x => x.ResetInfiniteDragSmoothCommand)),
            Row(model, x => x.InfiniteTurnSmoothLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.InfiniteTurnSmoothSeconds),
                ResetButton(model, x => x.ResetInfiniteTurnSmoothCommand)),
            Row(model, x => x.InfiniteTurnHeadSmoothLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.InfiniteTurnHeadSmoothSeconds),
                ResetButton(model, x => x.ResetInfiniteTurnHeadSmoothCommand)),
            Divider(),
            Header(model.Strings, "settings.infiniteBoost.header"),
            Row(model, x => x.InfiniteWalkingBoostLabel, new Slider().Minimum(0).Maximum(2).TickFrequency(0.05)
                    .Value(model, x => x.InfiniteWalkingBoostMultiplier),
                ResetButton(model, x => x.ResetInfiniteWalkingBoostCommand)),
            Divider(),
            Header(model.Strings, "settings.headPilot.header"),
            Row(model.Strings, "settings.headPilot.enabled", new CheckBox { Name = "HeadPilotEnabled" }.IsChecked(model, x => x.HeadPilotEnabled),
                ResetButton(model, x => x.ResetHeadPilotCommand)),
            Divider(),
            Header(model.Strings, "settings.drag.header"),
            Row(model.Strings, "settings.drag.enabled", new CheckBox { Name = "DragEnabled" }.IsChecked(model, x => x.DragEnabled),
                ResetButton(model, x => x.ResetDragEnabledCommand)),
            Divider(),
            Header(model.Strings, "settings.turnOrigin.header"),
            Row(model.Strings, "settings.turnOrigin.head", new CheckBox { Name = "UseHeadTurnOrigin" }.IsChecked(model, x => x.UseHeadTurnOrigin),
                ResetButton(model, x => x.ResetTurnOriginCommand)),
            Divider(),
            Header(model.Strings, "settings.inertiaCutoff.header"),
            Row(model.Strings, "settings.enabled", new CheckBox().IsChecked(model, x => x.InertiaCutoffEnabled),
                ResetButton(model, x => x.ResetInertiaCutoffEnabledCommand)),
            Row(model, x => x.DragCutoffLabel, new Slider().Minimum(0).Maximum(50).TickFrequency(0.5)
                    .Value(model, x => x.DragCutoffCentimetresPerSecond),
                ResetButton(model, x => x.ResetDragCutoffCommand)),
            Row(model, x => x.TurnCutoffLabel, new Slider().Minimum(0).Maximum(180).TickFrequency(1)
                    .Value(model, x => x.TurnCutoffDegreesPerSecond),
                ResetButton(model, x => x.ResetTurnCutoffCommand)),
            Divider(),
            Header(model.Strings, "settings.inertiaAcceleration.header"),
            Row(model, x => x.DragAccelerationLabel, new Slider().Minimum(0).Maximum(5).TickFrequency(0.05)
                    .Value(model, x => x.DragAccelerationMultiplier),
                ResetButton(model, x => x.ResetDragAccelerationCommand)),
            Row(model, x => x.TurnAccelerationLabel, new Slider().Minimum(0).Maximum(5).TickFrequency(0.05)
                    .Value(model, x => x.TurnAccelerationMultiplier),
                ResetButton(model, x => x.ResetTurnAccelerationCommand)),
            Divider(),
            Header(model.Strings, "settings.zAcceleration.header"),
            Row(model, x => x.ZAccelerationLabel, new Slider().Minimum(1).Maximum(5).TickFrequency(0.05)
                    .Value(model, x => x.ZAccelerationMultiplier),
                ResetButton(model, x => x.ResetZAccelerationCommand)),
            Divider(),
            Header(model.Strings, "settings.accelerationBoost.header"),
            Row(model.Strings, "settings.enabled", new CheckBox().IsChecked(model, x => x.InertiaAccelerationBoostEnabled),
                ResetButton(model, x => x.ResetInertiaAccelerationBoostEnabledCommand)),
            Row(model, x => x.InertiaAccelerationBoostMaximumLabel, new Slider().Minimum(1).Maximum(6).TickFrequency(0.05)
                    .Value(model, x => x.InertiaAccelerationBoostMaximumMultiplier),
                ResetButton(model, x => x.ResetInertiaAccelerationBoostMaximumMultiplierCommand)),
            Divider(),
            Header(model.Strings, "settings.vectorRotation.header"),
            Row(model, x => x.VectorRotationLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.VectorRotationMultiplier),
                ResetButton(model, x => x.ResetVectorRotationCommand)),
            Divider(),
            Header(model.Strings, "settings.deceleration.header"),
            Row(model, x => x.InertiaDecelerationLabel, new Slider().Minimum(0).Maximum(10).TickFrequency(0.01)
                    .Value(model, x => x.InertiaDecelerationPerSecond),
                ResetButton(model, x => x.ResetDecelerationCommand)),
            Row(model, x => x.InertiaStopDisplacementLabel, new Slider().Minimum(0.000001).Maximum(0.001).TickFrequency(0.000001)
                    .Value(model, x => x.InertiaStopDisplacementMetres),
                ResetButton(model, x => x.ResetInertiaStopDisplacementCommand)),
            Divider(),
            Header(model.Strings, "settings.exemption.header"),
            Row(model.Strings, "settings.enabled", new CheckBox().IsChecked(model, x => x.DecelerationExemptionEnabled),
                ResetButton(model, x => x.ResetExemptionEnabledCommand)),
            Row(model, x => x.DragDecelerationExemptionDurationLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.DragDecelerationExemptionDurationRatio),
                ResetButton(model, x => x.ResetDragExemptionCommand)),
            Row(model, x => x.TurnDecelerationExemptionDurationLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.TurnDecelerationExemptionDurationRatio),
                ResetButton(model, x => x.ResetTurnExemptionCommand)),
            Row(model, x => x.DecelerationExemptionStrengthLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.DecelerationExemptionStrength),
                ResetButton(model, x => x.ResetExemptionStrengthCommand)),
            Divider(),
            Header(model.Strings, "settings.dragSmooth.header"),
            Row(model, x => x.DragSmoothLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.DragSmoothSeconds),
                ResetButton(model, x => x.ResetDragSmoothCommand)),
            Row(model, x => x.TurnSmoothLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.TurnSmoothSeconds),
                ResetButton(model, x => x.ResetTurnSmoothCommand)),
            Divider(),
            Header(model.Strings, "settings.dragBrake.header"),
            Row(model, x => x.BrakeRampLabel, new Slider().Minimum(0).Maximum(1).TickFrequency(0.01)
                    .Value(model, x => x.BrakeRampSeconds),
                ResetButton(model, x => x.ResetBrakeRampCommand)),
            Divider(),
            Header(model.Strings, "settings.commandLoop.header"),
            Row(model.Strings, "settings.commandLoop.highRate",
                new CheckBox { Name = "HighRateCommandLoop" }.IsChecked(model, x => x.HighRateCommandLoop),
                ResetButton(model, x => x.ResetHighRateCommandLoopCommand)),
            Note(model.Strings, "settings.commandLoop.note"),
            Divider(),
            DiagnosticsSection(model)
        );

    /// <summary>"Save diagnostic logs": a list of what goes in, then Save / Cancel.</summary>
    private static Control DiagnosticsSection(MainViewModel model) =>
        new StackPanel().Spacing(8).Children(
            Header(model.Strings, "settings.diagnostics.header"),
            Note(model.Strings, "settings.diagnostics.when"),
            Localize(model.Strings, new Button { Name = "PrepareDiagnosticLogs" }, (b, s) => b.Content = s, "settings.diagnostics.collect")
                .IsEnabled(model, x => x.CanUseDiagnostics)
                .Command(model, x => x.PrepareDiagnosticLogsCommand),
            new Border
            {
                Padding = new Thickness(10),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                BorderBrush = Brushes.Gray
            }
                .IsVisible(model, x => x.IsDiagnosticPlanVisible)
                .Child(new StackPanel().Spacing(8).Children(
                    new TextBlock().Text(model, x => x.DiagnosticPlanHeader).FontWeight(FontWeight.SemiBold)
                        .TextWrapping(TextWrapping.Wrap),
                    new SelectableTextBlock { Name = "DiagnosticFileList", FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12 }
                        .Text(model, x => x.DiagnosticFileList).TextWrapping(TextWrapping.Wrap),
                    Note(model.Strings, "settings.diagnostics.redaction"),
                    new StackPanel().Orientation(Orientation.Horizontal).Spacing(8).Children(
                        Localize(model.Strings, new Button { Name = "SaveDiagnosticLogs" }, (b, s) => b.Content = s, "settings.diagnostics.save")
                            .IsEnabled(model, x => x.CanUseDiagnostics)
                            .Command(model, x => x.SaveDiagnosticLogsCommand),
                        Localize(model.Strings, new Button(), (b, s) => b.Content = s, "settings.diagnostics.cancel")
                            .IsEnabled(model, x => x.CanUseDiagnostics)
                            .Command(model, x => x.CancelDiagnosticLogsCommand)))),
            new SelectableTextBlock { Name = "DiagnosticStatus" }.Text(model, x => x.DiagnosticStatus).TextWrapping(TextWrapping.Wrap),
            Localize(model.Strings, new Button(), (b, s) => b.Content = s, "settings.diagnostics.openFolder")
                .IsVisible(model, x => x.HasDiagnosticArchive)
                .Command(model, x => x.OpenDiagnosticFolderCommand),
            Note(model.Strings, "settings.diagnostics.share"));

    private static TextBlock Note(UiStrings strings, string key) =>
        Localize(strings, new TextBlock(), (t, s) => t.Text = s, key).TextWrapping(TextWrapping.Wrap).Opacity(0.75);

    private static Grid Row(UiStrings strings, string labelKey, Control editor, Button reset) =>
        Row(Localize(strings, new TextBlock(), (t, s) => t.Text = s, labelKey), editor, reset);

    /// <summary>Applies the localized text now and again whenever the UI language changes.</summary>
    private static T Localize<T>(UiStrings strings, T target, Action<T, string> apply, string key)
    {
        apply(target, strings[key]);
        strings.LanguageChanged += (_, _) => apply(target, strings[key]);
        return target;
    }

    private static void LocalizeTip(UiStrings strings, Control target, string key) =>
        Localize(strings, target, (c, s) => ToolTip.SetTip(c, s), key);

    /// <summary>ENG / JPN / … buttons, one per embedded language table; the active one is checked.</summary>
    private static Control LanguageSelector(MainViewModel model)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        LocalizeTip(model.Strings, panel, "main.language.tooltip");
        var buttons = new List<ToggleButton>();
        foreach (var language in model.Languages)
        {
            var button = new ToggleButton
            {
                Content = language.SelectorLabel,
                Tag = language.Code,
                MinWidth = 52,
                Padding = new Thickness(8, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Command = model.SelectLanguageCommand,
                CommandParameter = language.Code
            };
            button.Classes.Add("language-option");
            ToolTip.SetTip(button, language.NativeName);
            buttons.Add(button);
            panel.Children.Add(button);
        }
        void Sync()
        {
            foreach (var button in buttons)
                button.IsChecked = Equals(button.Tag, model.Language);
        }
        // A click toggles IsChecked itself; re-sync afterwards so exactly the active language stays checked.
        foreach (var button in buttons)
            button.Click += (_, _) => Sync();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.Language)) Sync();
        };
        Sync();
        return panel;
    }

    private static Separator Divider() => new() { Margin = new Thickness(0, 8) };

    private static Button ResetButton(
        MainViewModel model,
        Expression<Func<MainViewModel, System.Windows.Input.ICommand>> command) =>
        CreateResetButton(model, command);

    private static Button CreateResetButton(
        MainViewModel model,
        Expression<Func<MainViewModel, System.Windows.Input.ICommand>> command)
    {
        var button = new Button
        {
            Content = "↻",
            FontSize = 20,
            Width = 36,
            Height = 36,
            MinWidth = 36,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        LocalizeTip(model.Strings, button, "settings.reset.tooltip");
        return button.Command(model, command);
    }

    private static TextBlock Header(UiStrings strings, string key) =>
        Localize(strings, new TextBlock(), (t, s) => t.Text = s, key).FontWeight(FontWeight.SemiBold).FontSize(16);

    private static Grid Row(MainViewModel model, Expression<Func<MainViewModel, string>> label, Control editor, Button reset) =>
        Row(new TextBlock().Text(model, label), editor, reset);

    private static Grid Row(Control label, Control editor, Button reset)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*, 3*, Auto"),
            ColumnSpacing = 8
        };
        label.VerticalAlignment = VerticalAlignment.Center;
        editor.VerticalAlignment = VerticalAlignment.Center;
        reset.VerticalAlignment = VerticalAlignment.Center;
        if (label is TextBlock textBlock)
            textBlock.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(label, 0);
        Grid.SetColumn(editor, 1);
        Grid.SetColumn(reset, 2);
        row.Children.Add(label);
        row.Children.Add(editor);
        row.Children.Add(reset);
        return row;
    }
}
